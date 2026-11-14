using System.Diagnostics;
using SelectionTts.Audio;
using SelectionTts.Tts;

namespace SelectionTts.Core;

/// <summary>
/// 朗读编排：文本 → 合成 → 播放。
/// 新的请求总是打断旧的（停音频、取消合成），不再用"正在朗读中"拒绝。
/// </summary>
public sealed class ReaderService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly EdgeTtsClient _tts = new();
    private readonly AudioPlayer _player = new();
    private readonly object _gate = new();
    private int _requestId;
    private CancellationTokenSource? _cts;
    private bool _active;

    public event Action<string>? StatusChanged;
    public event Action<string>? Failed;

    public ReaderService(AppSettings settings)
    {
        _settings = settings;
    }

    public void Speak(string text)
    {
        int generation;
        CancellationToken token;
        lock (_gate)
        {
            generation = ++_requestId;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
            _player.Stop();
            _active = true;
        }

        Task.Run(() => Worker(generation, text, token), token);
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_active)
            {
                return;
            }

            _active = false;
            _requestId++;
            _cts?.Cancel();
            _player.Stop();
        }

        StatusChanged?.Invoke("已停止");
    }

    private async Task Worker(int generation, string text, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();

        try
        {
            var (normalized, truncated) = TextUtil.Truncate(
                TextUtil.Normalize(text), _settings.MaxChars);

            if (normalized.Length == 0)
            {
                StatusChanged?.Invoke("没有可朗读的文本");
                return;
            }

            if (truncated)
            {
                Log.Info($"文本超过 {_settings.MaxChars} 字，截断朗读");
            }

            StatusChanged?.Invoke($"正在合成（{normalized.Length} 字）…");
            var audio = await _tts.SynthesizeAsync(normalized, _settings.Voice, _settings.Rate, token);

            if (IsStale(generation))
            {
                return;
            }

            StatusChanged?.Invoke("开始朗读");
            lock (_gate)
            {
                if (generation != _requestId)
                {
                    return;
                }

                _player.Play(audio.Audio, ok =>
                {
                    if (IsStale(generation))
                    {
                        return;
                    }

                    lock (_gate)
                    {
                        _active = false;
                    }

                    StatusChanged?.Invoke(ok ? "朗读完成" : "已停止");
                });
            }

            Log.Info(
                $"朗读 {normalized.Length} 字 | 合成={audio.ElapsedMs}ms | " +
                $"尝试={audio.Attempts} | 总计={watch.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException)
        {
            Log.Info("朗读已取消");
        }
        catch (Exception ex)
        {
            if (IsStale(generation))
            {
                return;
            }

            lock (_gate)
            {
                _active = false;
            }

            Log.Error("朗读失败", ex);
            Failed?.Invoke($"朗读失败：{ex.Message}");
            StatusChanged?.Invoke("朗读失败");
        }
    }

    private bool IsStale(int generation)
    {
        lock (_gate)
        {
            return generation != _requestId;
        }
    }

    public void Dispose()
    {
        Stop();
        _player.Dispose();
    }
}
