namespace SelectionTts.Audio;

/// <summary>
/// 播放器抽象。存在的唯一目的是让朗读管线可以被故障注入测试：
/// 没有音频设备、Begin 抛异常、旧会话越界写入这些场景，真实设备上很难稳定复现。
///
/// 所有写操作都带 sessionId：会话切换（seek / 新文本）后，旧流水线的写入必须被丢弃，
/// 不能污染新会话的计数器与"已喂完"标记。
/// </summary>
public interface IAudioPlayer : IDisposable
{
    /// <summary>开始一次新的播放会话，返回会话编号；旧会话的写入会失效。</summary>
    int Begin();

    /// <summary>
    /// 把一段 MP3 写进播放缓冲，返回实际写入的 PCM 字节数。
    /// skipPcmBytes 用于「歌词式跳转」：跳过句首一段音频，从中间开始播放。
    /// </summary>
    Task<int> WriteMp3Async(byte[] mp3, int sessionId, CancellationToken cancellationToken, int skipPcmBytes = 0);

    /// <summary>标记某一会话的音频已经全部写入（旧会话调用会被忽略）。</summary>
    void MarkFeederDone(int sessionId);

    void Pause();

    void Resume();

    void Stop();

    /// <summary>解码后 PCM 的实际字节率（不写死 48000，由输出格式决定）。</summary>
    double BytesPerSecond { get; }

    double PositionSeconds { get; }

    bool IsDrained { get; }
}
