using NAudio.Wave;
using SelectionTts.Core;

namespace SelectionTts.Audio;

/// <summary>
/// 边合成边播放：把一段段 MP3 解码成 PCM 追加到同一个缓冲里，
/// 由 WaveOut 连续读，所以块与块之间不会有"停顿一下再继续"的接缝。
///
/// 位置是真位置：已写入的 PCM 字节 - 还在缓冲里的字节 = 已经放出去的字节。
/// 进度条和剩余时间都靠它，不靠估算。
///
/// 会话语义：每次 Begin 递增会话编号并清零计数。写入、计数、喂完标记
/// 都在同一把锁内核对会话号——旧流水线跨过 seek 后写的字节必须被丢弃，
/// 否则新会话的位置会跳变。
/// </summary>
public sealed class StreamingAudioPlayer : IAudioPlayer
{
    public const int SampleRate = 24000;
    public const int Channels = 1;
    public const int DefaultBytesPerSecond = SampleRate * Channels * 2;

    private readonly object _gate = new();
    private WaveOutEvent? _output;
    private BufferedWaveProvider? _buffer;
    private int _session;
    private long _writtenBytes;
    private bool _feederDone;
    private bool _paused;

    // 位置外推：设备位置一跳就重新锚定，两跳之间按真实时间补
    private int _positionSession;
    private long _deviceAnchor;
    private long _deviceAnchorAt;
    private long _reported;
    private long _debugDevice;
    private long _debugEstimate;
    private long _debugPlayed;

    /// <summary>--diag-highlight 用：位置换算的原始量（设备位置/缓冲/已写/暂停）。</summary>
    public (long Device, long Buffered, long Written, bool Paused, long Anchor, long AnchorAge, long Reported, int Session, long Estimate, long Played)
        PositionDebug
    {
        get
        {
            lock (_gate)
            {
                return (
                    _debugDevice,
                    _buffer?.BufferedBytes ?? 0,
                    _writtenBytes,
                    _paused,
                    _deviceAnchor,
                    Environment.TickCount64 - _deviceAnchorAt,
                    _reported,
                    _positionSession,
                    _debugEstimate,
                    _debugPlayed);
            }
        }
    }

    /// <summary>开始一次新的播放会话，返回该会话的编号。旧会话的写入会被丢弃。</summary>
    public int Begin()
    {
        Stop();

        var buffer = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, Channels))
        {
            BufferDuration = TimeSpan.FromSeconds(10),
            // 缓冲空了返回静音而不是触发 PlaybackStopped，否则换块的瞬间会被当成"播放结束"
            ReadFully = true,
            DiscardOnBufferOverflow = false,
        };

        var output = new WaveOutEvent { DesiredLatency = 200 };

        try
        {
            output.Init(buffer);

            int session;
            lock (_gate)
            {
                session = ++_session;
                _buffer = buffer;
                _output = output;
                _writtenBytes = 0;
                _feederDone = false;
                _paused = false;
            }

            output.Play();
            return session;
        }
        catch
        {
            // Init / Play 失败：不能把半初始化的设备留在字段里，否则后续 Stop 会访问坏状态
            lock (_gate)
            {
                if (ReferenceEquals(_output, output))
                {
                    _output = null;
                    _buffer = null;
                }
            }

            try
            {
                output.Dispose();
            }
            catch
            {
                // 设备自身已经坏了，释放失败也无计可施
            }

            throw;
        }
    }

    /// <summary>把整段 MP3 解码后写进缓冲，返回实际写入的 PCM 字节数。</summary>
    public async Task<int> WriteMp3Async(
        byte[] mp3, int sessionId, CancellationToken cancellationToken, int skipPcmBytes = 0)
    {
        var pcm = DecodeMp3(mp3);
        var skip = Math.Clamp(skipPcmBytes, 0, pcm.Length);

        await WritePcmAsync(pcm, skip, sessionId, cancellationToken).ConfigureAwait(false);
        return pcm.Length - skip;
    }

    /// <summary>把整段 MP3 解码成 24kHz/16bit/单声道 PCM。</summary>
    public static byte[] DecodeMp3(byte[] mp3)
    {
        if (mp3.Length == 0)
        {
            return [];
        }

        using var stream = new MemoryStream(mp3);
        using var reader = new Mp3FileReader(stream);
        using var pcm = new MemoryStream(mp3.Length * 6);

        var buffer = new byte[16 * 1024];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            pcm.Write(buffer, 0, read);
        }

        return pcm.ToArray();
    }

    public Task WritePcmAsync(byte[] pcm, int sessionId, CancellationToken cancellationToken) =>
        WritePcmAsync(pcm, 0, sessionId, cancellationToken);

    /// <summary>
    /// 把 PCM 写进缓冲（可从 startOffset 开始，用于跳转时丢弃句首）。
    /// 缓冲满时等待播放消耗（背压，不丢音频）；会话已切换则立即放弃。
    /// </summary>
    public async Task WritePcmAsync(
        byte[] pcm, int startOffset, int sessionId, CancellationToken cancellationToken)
    {
        var offset = Math.Clamp(startOffset, 0, pcm.Length);

        while (offset < pcm.Length)
        {
            var wrote = false;

            lock (_gate)
            {
                if (sessionId != _session || _buffer is null)
                {
                    return;
                }

                var free = _buffer.BufferLength - _buffer.BufferedBytes;
                if (free > 0)
                {
                    var count = Math.Min(free, pcm.Length - offset);
                    _buffer.AddSamples(pcm, offset, count);
                    _writtenBytes += count;
                    offset += count;
                    wrote = true;
                }
            }

            if (!wrote)
            {
                await Task.Delay(40, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>只有当前会话才能标记"喂完"；旧会话迟到的标记会被忽略。</summary>
    public void MarkFeederDone(int sessionId)
    {
        lock (_gate)
        {
            if (sessionId == _session)
            {
                _feederDone = true;
            }
        }
    }

    /// <summary>输出格式的字节率；不使用硬编码常量，避免格式变化后位置换算整体漂移。</summary>
    public double BytesPerSecond
    {
        get
        {
            lock (_gate)
            {
                return _buffer?.WaveFormat.AverageBytesPerSecond ?? 48_000;
            }
        }
    }

    public double PositionSeconds
    {
        get
        {
            WaveOutEvent? output;
            BufferedWaveProvider? buffer;
            long written;
            int session;

            lock (_gate)
            {
                output = _output;
                buffer = _buffer;
                written = _writtenBytes;
                session = _session;
            }

            if (buffer is null)
            {
                return 0;
            }

            var bps = buffer.WaveFormat.AverageBytesPerSecond;

            // 设备报的位置：最接近耳朵；有的驱动粒度粗，就靠下面的时间外推补平
            long device = 0;
            bool playing;

            if (output is not null)
            {
                try
                {
                    device = output.GetPosition();
                }
                catch
                {
                    device = 0;
                }

                // 换会话的瞬间驱动可能还在报旧位置
                device = Math.Clamp(device, 0, written);
            }

            lock (_gate)
            {
                playing = !_paused;
            }

            var now = Environment.TickCount64;
            long played;

            lock (_gate)
            {
                if (session != _positionSession)
                {
                    _positionSession = session;
                    _deviceAnchor = device;
                    _deviceAnchorAt = now;
                    _reported = 0;
                }

                // 设备位置一跳就重新锚定（粒度粗的驱动几十毫秒到一秒才跳一次）
                if (device > _deviceAnchor)
                {
                    _deviceAnchor = device;
                    _deviceAnchorAt = now;
                }

                _debugDevice = device;

                // 两跳之间按真实时间外推：这就是高亮要的几十毫秒级进度
                var estimate = _deviceAnchor;
                if (playing && now > _deviceAnchorAt)
                {
                    estimate += (long)((now - _deviceAnchorAt) * bps / 1000d);
                }

                // 不超过真正写入的音频；暂停时也不再往前走
                played = Math.Min(estimate, written);
                _debugEstimate = estimate;
                _debugPlayed = played;

                // 只增不减：驱动抖动不能让高亮来回跳
                if (played > _reported)
                {
                    _reported = played;
                }

                played = Math.Clamp(_reported, 0, written);
            }

            return played / (double)bps;
        }
    }

    /// <summary>所有音频都放完了（没有新的要喂，缓冲也空了）。</summary>
    public bool IsDrained
    {
        get
        {
            lock (_gate)
            {
                return _feederDone && _buffer is not null && _buffer.BufferedBytes == 0;
            }
        }
    }

    public void Pause()
    {
        try
        {
            _output?.Pause();

            lock (_gate)
            {
                _paused = true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"暂停失败：{ex.Message}");
        }
    }

    public void Resume()
    {
        try
        {
            _output?.Play();

            lock (_gate)
            {
                _paused = false;
                // 从暂停点继续外推，别把整段暂停时间算成播放进度
                _deviceAnchorAt = Environment.TickCount64;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"继续播放失败：{ex.Message}");
        }
    }

    public void Stop()
    {
        WaveOutEvent? output;
        lock (_gate)
        {
            _session++;
            _buffer = null;
            _feederDone = true;
            _writtenBytes = 0;
            _paused = false;
            output = _output;
            _output = null;
        }

        if (output is null)
        {
            return;
        }

        try
        {
            output.Stop();
            output.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose() => Stop();
}
