using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using SelectionTts.Core;

namespace SelectionTts.Tts;

/// <summary>
/// Azure 语音服务（备用引擎）。和 Edge 用同一个输出格式，
/// 所以解码、播放、进度、高亮全部复用，只是换了个"取音频"的地方。
///
/// 与 Edge 的差别：REST 接口不返回词时间戳，所以高亮只能按字数比例推进；
/// 又因为它是"一句话一个请求"，免费层每分钟请求数有限，撞到 429 要等一下再试。
/// </summary>
public sealed class AzureTtsClient : ISpeechSynthesizer
{
    private const string OutputFormat = "audio-24khz-48kbitrate-mono-mp3";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(40),
    };

    /// <summary>被限流后要等到的时刻（UTC ticks），所有请求共享，避免连续撞墙。</summary>
    private static long _throttleUntilTicks;

    private readonly string _region;
    private readonly string _key;

    public AzureTtsClient(string region, string key)
    {
        _region = (region ?? "").Trim().ToLowerInvariant();
        _key = (key ?? "").Trim();
    }

    public bool IsConfigured => _region.Length > 0 && _key.Length > 0;

    public string Name => "Azure";

    public async Task<SynthesisSummary> StreamAsync(
        string text,
        string voice,
        string rate,
        Action<ReadOnlyMemory<byte>> onAudio,
        Action<SpeechBoundary>? onBoundary,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("没有配置 Azure 区域或 Key");
        }

        var watch = Stopwatch.StartNew();
        var url = $"https://{_region}.tts.speech.microsoft.com/cognitiveservices/v1";
        var total = 0;

        for (var attempt = 1; ; attempt++)
        {
            await WaitForThrottleAsync(cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", _key);
            request.Headers.TryAddWithoutValidation("X-Microsoft-OutputFormat", OutputFormat);
            request.Headers.TryAddWithoutValidation("User-Agent", "SelectionTts");
            request.Content = new StringContent(BuildSsml(text, voice, rate), Encoding.UTF8);
            request.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/ssml+xml");

            using var response = await Http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(3.5);
                SetThrottle(wait);

                if (attempt >= 5)
                {
                    throw new HttpRequestException(
                        "Azure 请求过于频繁（429），重试多次仍被拒绝", null, HttpStatusCode.TooManyRequests);
                }

                Log.Warn($"Azure 限流（429），等 {wait.TotalSeconds:F1}s 后重试（第 {attempt} 次）");
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(DescribeFailure(response.StatusCode), null, response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[32 * 1024];

            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                onAudio(new ReadOnlyMemory<byte>(buffer, 0, read));
            }

            break;
        }

        if (total == 0)
        {
            throw new InvalidOperationException("Azure 没有返回音频");
        }

        _ = onBoundary; // REST 接口没有时间戳
        return new SynthesisSummary(total, 0, (int)watch.ElapsedMilliseconds, 1);
    }

    private static void SetThrottle(TimeSpan wait)
    {
        var until = DateTime.UtcNow.Add(wait).Ticks;
        long current;

        do
        {
            current = Interlocked.Read(ref _throttleUntilTicks);
            if (current >= until)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _throttleUntilTicks, until, current) != current);
    }

    private static async Task WaitForThrottleAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var until = Interlocked.Read(ref _throttleUntilTicks);
            var remaining = until - DateTime.UtcNow.Ticks;
            if (remaining <= 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromTicks(remaining), cancellationToken);
        }
    }

    private static string DescribeFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "Azure Key 无效（401）",
        HttpStatusCode.Forbidden => "Azure Key 无权访问该区域（403）",
        HttpStatusCode.TooManyRequests => "Azure 请求过多或超过配额（429）",
        HttpStatusCode.NotFound => "Azure 区域地址不对（404）",
        _ => $"Azure 合成失败（HTTP {(int)status}）",
    };

    private static string BuildSsml(string text, string voice, string rate)
    {
        var locale = LocaleOf(voice);
        var ssml =
            "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' " +
            $"xml:lang='{locale}'>" +
            $"<voice name='{Escape(voice)}'>" +
            $"<prosody rate='{Escape(rate)}'>" +
            Escape(text) +
            "</prosody></voice></speak>";
        return ssml;
    }

    /// <summary>zh-CN-XiaoxiaoNeural → zh-CN</summary>
    private static string LocaleOf(string voice)
    {
        var parts = (voice ?? "").Split('-');
        return parts.Length >= 2 ? $"{parts[0]}-{parts[1]}" : "zh-CN";
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;");
}