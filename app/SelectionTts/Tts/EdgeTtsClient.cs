using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SelectionTts.Core;

namespace SelectionTts.Tts;

public sealed record SynthesisResult(byte[] Audio, int ElapsedMs, int Attempts);

/// <summary>
/// 直接实现 Edge 朗读接口（与 Python 版 edge-tts 相同的 WebSocket 协议）。
/// 音频边收边回调，同时解析 audio.metadata 里的词时间戳——进度条和剩余时间靠它，
/// 不是靠猜。
///
/// 注意：服务端 sentenceBoundary 和 wordBoundary 同时开启时只会返回 WordBoundary，
/// 所以这里只开 wordBoundary。
/// </summary>
public sealed class EdgeTtsClient : ISpeechSynthesizer
{
    private const string OutputFormat = "audio-24khz-48kbitrate-mono-mp3";

    private static readonly string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        $"(KHTML, like Gecko) Chrome/{DrmToken.ChromiumVersion.Split('.')[0]}.0.0.0 " +
        $"Safari/537.36 Edg/{DrmToken.ChromiumVersion.Split('.')[0]}.0.0.0";

    /// <summary>
    /// 流式合成。音频到达就回调，不攒到最后。
    /// 只在"还没吐出任何音频"时重试——已经开始播了再重试会把同一句念两遍。
    /// </summary>
    public async Task<SynthesisSummary> StreamAsync(
        string text,
        string voice,
        string rate,
        Action<ReadOnlyMemory<byte>> onAudio,
        Action<SpeechBoundary>? onBoundary,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        Exception? lastError = null;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var emitted = 0;
            try
            {
                var summary = await StreamOnceAsync(
                    text,
                    voice,
                    rate,
                    buffer =>
                    {
                        Interlocked.Exchange(ref emitted, 1);
                        onAudio(buffer);
                    },
                    onBoundary,
                    cancellationToken);

                return summary with
                {
                    ElapsedMs = (int)watch.ElapsedMilliseconds,
                    Attempts = attempt,
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Log.Warn($"Edge TTS 流式第 {attempt} 次失败：{ex.GetType().Name}: {ex.Message}");

                if (Volatile.Read(ref emitted) == 1)
                {
                    break;
                }
            }
        }

        throw lastError ?? new InvalidOperationException("Edge TTS 合成失败");
    }

    /// <summary>兼容旧调用：收完再返回。只有自检在用，正式朗读走 StreamAsync。</summary>
    public async Task<SynthesisResult> SynthesizeAsync(
        string text,
        string voice,
        string rate,
        CancellationToken cancellationToken)
    {
        using var audio = new MemoryStream();
        var summary = await StreamAsync(
            text,
            voice,
            rate,
            buffer => audio.Write(buffer.Span),
            null,
            cancellationToken);

        return new SynthesisResult(audio.ToArray(), summary.ElapsedMs, summary.Attempts);
    }

    private static async Task<SynthesisSummary> StreamOnceAsync(
        string text,
        string voice,
        string rate,
        Action<ReadOnlyMemory<byte>> onAudio,
        Action<SpeechBoundary>? onBoundary,
        CancellationToken cancellationToken)
    {
        var url =
            "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1" +
            $"?TrustedClientToken={DrmToken.TrustedClientToken}" +
            $"&Sec-MS-GEC={DrmToken.Generate()}" +
            $"&Sec-MS-GEC-Version={DrmToken.SecMsGecVersion}" +
            $"&ConnectionId={Guid.NewGuid():N}";

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Pragma", "no-cache");
        socket.Options.SetRequestHeader("Cache-Control", "no-cache");
        socket.Options.SetRequestHeader("User-Agent", UserAgent);
        socket.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        socket.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        socket.Options.SetRequestHeader("Cookie", $"muid={Guid.NewGuid():N};");

        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri(url), connectTimeout.Token);

        await SendTextAsync(socket, BuildConfigMessage(), cancellationToken);
        await SendTextAsync(socket, BuildSsmlMessage(text, voice, rate), cancellationToken);

        var buffer = new byte[32 * 1024];
        var audioBytes = 0;
        var boundaryCount = 0;

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    break;
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            if (message.Length == 0)
            {
                continue;
            }

            var payload = message.GetBuffer();

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var header = Encoding.UTF8.GetString(payload, 0, (int)message.Length);
                if (header.Contains("Path:turn.end", StringComparison.Ordinal))
                {
                    break;
                }

                if (header.Contains("Path:audio.metadata", StringComparison.Ordinal))
                {
                    var split = header.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (split >= 0)
                    {
                        boundaryCount += ParseMetadata(header[(split + 4)..], onBoundary);
                    }
                }

                // turn.start / speech.config / 其它文本帧都不含音频
                continue;
            }

            // 二进制帧：2 字节头长度（大端）+ 头文本 + 音频数据
            if (message.Length <= 2)
            {
                continue;
            }

            var headerLength = (payload[0] << 8) | payload[1];
            var audioStart = 2 + headerLength;
            var frameLength = (int)message.Length;
            if (audioStart >= frameLength)
            {
                continue;
            }

            var frameHeader = Encoding.ASCII.GetString(
                payload, 2, Math.Min(headerLength, frameLength - 2));
            if (!frameHeader.Contains("Path:audio", StringComparison.Ordinal))
            {
                continue;
            }

            var length = frameLength - audioStart;
            audioBytes += length;
            onAudio(new ReadOnlyMemory<byte>(payload, audioStart, length));
        }

        if (audioBytes == 0)
        {
            throw new InvalidOperationException("Edge TTS 没有返回音频");
        }

        return new SynthesisSummary(audioBytes, boundaryCount, 0, 1);
    }

    /// <summary>
    /// 解析 audio.metadata 的正文：
    /// {"Metadata":[{"Type":"WordBoundary","Data":{"Offset":1000000,"Duration":3375000,
    ///  "text":{"Text":"第一","Length":2,"BoundaryType":"WordBoundary"}}}]}
    /// </summary>
    private static int ParseMetadata(string json, Action<SpeechBoundary>? onBoundary)
    {
        if (onBoundary is null)
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Metadata", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            var count = 0;
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("Type", out var typeElement))
                {
                    continue;
                }

                var type = typeElement.GetString();
                if (type is not ("WordBoundary" or "SentenceBoundary"))
                {
                    continue;
                }

                if (!item.TryGetProperty("Data", out var data))
                {
                    continue;
                }

                var offset = data.TryGetProperty("Offset", out var offsetElement) &&
                             offsetElement.TryGetInt64(out var offsetValue)
                    ? offsetValue
                    : 0;

                var duration = data.TryGetProperty("Duration", out var durationElement) &&
                               durationElement.TryGetInt64(out var durationValue)
                    ? durationValue
                    : 0;

                var text = "";
                if (data.TryGetProperty("text", out var textElement) &&
                    textElement.ValueKind == JsonValueKind.Object)
                {
                    if (textElement.TryGetProperty("Text", out var value))
                    {
                        text = value.GetString() ?? "";
                    }
                }

                onBoundary(new SpeechBoundary(offset, duration, text));
                count++;
            }

            return count;
        }
        catch (JsonException ex)
        {
            Log.Warn($"解析音频时间戳失败：{ex.Message}");
            return 0;
        }
    }

    private static Task SendTextAsync(ClientWebSocket socket, string payload, CancellationToken ct) =>
        socket.SendAsync(
            new ArraySegment<byte>(Encoding.UTF8.GetBytes(payload)),
            WebSocketMessageType.Text,
            true,
            ct);

    private static string BuildConfigMessage()
    {
        var config =
            "{\"context\":{\"synthesis\":{\"audio\":{" +
            "\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"true\"}," +
            "\"outputFormat\":\"" + OutputFormat + "\"}}}}";

        return $"X-Timestamp:{Timestamp()}\r\n" +
               "Content-Type:application/json; charset=utf-8\r\n" +
               "Path:speech.config\r\n\r\n" +
               config;
    }

    private static string BuildSsmlMessage(string text, string voice, string rate)
    {
        var ssml =
            "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
            $"<voice name='{Escape(voice)}'>" +
            $"<prosody pitch='+0Hz' rate='{Escape(rate)}' volume='+0%'>" +
            Escape(text) +
            "</prosody></voice></speak>";

        return $"X-RequestId:{Guid.NewGuid():N}\r\n" +
               "Content-Type:application/ssml+xml\r\n" +
               $"X-Timestamp:{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}\r\n" +
               "Path:ssml\r\n\r\n" +
               ssml;
    }

    private static string Timestamp() =>
        DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'",
            System.Globalization.CultureInfo.InvariantCulture);

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;");
}
