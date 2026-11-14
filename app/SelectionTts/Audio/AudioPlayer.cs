using NAudio.Wave;
using SelectionTts.Core;

namespace SelectionTts.Audio;

/// <summary>
/// 单轨 MP3 播放器。新播放会立即停掉旧的；旧回调通过 generation 丢弃，
/// 保证只有“最后一次请求”会报告朗读完成。
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private readonly object _gate = new();
    private WaveOutEvent? _output;
    private int _generation;

    public void Play(byte[] mp3, Action<bool>? onDone = null)
    {
        Stop();

        var stream = new MemoryStream(mp3);
        var reader = new Mp3FileReader(stream);
        var output = new WaveOutEvent();

        int generation;
        lock (_gate)
        {
            generation = ++_generation;
            _output = output;
        }

        output.Init(reader);
        output.PlaybackStopped += (_, args) =>
        {
            var current = false;
            lock (_gate)
            {
                if (generation == _generation)
                {
                    current = true;
                    _output = null;
                }
            }

            try
            {
                output.Dispose();
            }
            catch
            {
                // ignore
            }

            try
            {
                reader.Dispose();
            }
            catch
            {
                // ignore
            }

            if (current)
            {
                if (args.Exception is not null)
                {
                    Log.Error("播放失败", args.Exception);
                }

                onDone?.Invoke(args.Exception is null);
            }
        };

        output.Play();
    }

    public void Stop()
    {
        WaveOutEvent? output;
        lock (_gate)
        {
            _generation++;
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
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose() => Stop();
}
