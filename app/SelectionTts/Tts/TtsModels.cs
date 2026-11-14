namespace SelectionTts.Tts;

/// <summary>
/// 服务端返回的一个时间边界。OffsetTicks / DurationTicks 以 100ns 为单位，
/// 相对于本次请求的音频起点（每次分块请求都从 0 附近重新计时）。
/// </summary>
public readonly record struct SpeechBoundary(long OffsetTicks, long DurationTicks, string Text)
{
    public double OffsetSeconds => OffsetTicks / 10_000_000d;

    public double DurationSeconds => DurationTicks / 10_000_000d;
}

public sealed record SynthesisSummary(int AudioBytes, int BoundaryCount, int ElapsedMs, int Attempts)
{
    /// <summary>48kbps CBR MP3：时长（秒）= 字节数 × 8 ÷ 48000。</summary>
    public double DurationSeconds => AudioBytes * 8d / 48_000d;
}

/// <summary>
/// 音频合成器。Edge 和 Azure 共用这套回调接口，上层流水线不需要知道用的是哪个引擎。
/// onAudio 的缓冲区在回调返回后即失效，需要保留必须自己复制。
/// </summary>
public interface ISpeechSynthesizer
{
    Task<SynthesisSummary> StreamAsync(
        string text,
        string voice,
        string rate,
        Action<ReadOnlyMemory<byte>> onAudio,
        Action<SpeechBoundary>? onBoundary,
        CancellationToken cancellationToken);
}
