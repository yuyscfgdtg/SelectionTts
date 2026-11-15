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
    public const int BytesPerSecond = SampleRate * Channels * 2;

    private readonly object _gate = new();
    private WaveOutEvent? _output;
    private BufferedWaveProvider? _buffer;
    private int _session;
    private long _writtenBytes;
    private bool _feederDone;

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

    /// <summary>把整段 MP3 解码后写进缓冲，返回 PCM 字节数。</summary>
    public async Task<int> WriteMp3Async(byte[] mp3, int sessionId, CancellationToken cancellationToken)
    {
        var pcm = DecodeMp3(mp3);
        await WritePcmAsync(pcm, sessionId, cancellationToken).ConfigureAwait(false);
        return pcm.Length;
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

    /// <summary>
    /// 把 PCM 写进缓冲。缓冲满时等待播放消耗（背压，不丢音频）。
    /// 写入与计数在同一同步边界内完成；会话已切换则立即放弃。
    /// </summary>
    public async Task WritePcmAsync(byte[] pcm, int sessionId, CancellationToken cancellationToken)
    {
        var offset = 0;

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

    public double PositionSeconds
    {
        get
        {
            lock (_gate)
            {
                if (_buffer is null)
                {
                    return 0;
                }

                var played = Math.Max(0, _writtenBytes - _buffer.BufferedBytes);
                return played / (double)BytesPerSecond;
            }
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

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    public void Pause()
    {
        try
        {
            _output?.Pause();
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
