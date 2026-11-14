using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using SelectionTts.Core;

namespace SelectionTts.Tts;

public sealed record SynthesisResult(byte[] Audio, int ElapsedMs, int Attempts);

/// <summary>
/// 直接实现 Edge 朗读接口（与 Python 版 edge-tts 相同的 WebSocket 协议）。
/// 只依赖音频流，不解析字幕事件。
/// </summary>
public sealed class EdgeTtsClient
{
    private const string OutputFormat = "audio-24khz-48kbitrate-mono-mp3";

    private static readonly string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        $"(KHTML, like Gecko) Chrome/{DrmToken.ChromiumVersion.Split('.')[0]}.0.0.0 " +
        $"Safari/537.36 Edg/{DrmToken.ChromiumVersion.Split('.')[0]}.0.0.0";

    public async Task<SynthesisResult> SynthesizeAsync(
        string text,
        string voice,
        string rate,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        Exception? lastError = null;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var audio = await SynthesizeOnceAsync(text, voice, rate, cancellationToken);
                return new SynthesisResult(audio, (int)watch.ElapsedMilliseconds, attempt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Log.Warn($"Edge TTS 第 {attempt} 次失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        throw lastError ?? new InvalidOperationException("Edge TTS 合成失败");
    }

    private static async Task<byte[]> SynthesizeOnceAsync(
        string text,
        string voice,
        string rate,
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

        var audio = new MemoryStream();
        var buffer = new byte[32 * 1024];

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

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var payload = Encoding.UTF8.GetString(message.ToArray());
                if (payload.Contains("Path:turn.end", StringComparison.Ordinal))
                {
                    break;
                }

                // turn.start / audio.metadata 之类的文本帧不含音频，忽略
                continue;
            }

            // 二进制帧：2 字节头长度（大端）+ 头文本 + 音频数据
            var frame = message.ToArray();
            if (frame.Length <= 2)
            {
                continue;
            }

            var headerLength = (frame[0] << 8) | frame[1];
            var audioStart = 2 + headerLength;
            if (audioStart >= frame.Length)
            {
                continue;
            }

            var header = Encoding.ASCII.GetString(frame, 2, Math.Min(headerLength, frame.Length - 2));
            if (!header.Contains("Path:audio", StringComparison.Ordinal))
            {
                continue;
            }

            audio.Write(frame, audioStart, frame.Length - audioStart);
        }

        if (audio.Length == 0)
        {
            throw new InvalidOperationException("Edge TTS 没有返回音频");
        }

        return audio.ToArray();
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
            "\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"}," +
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
