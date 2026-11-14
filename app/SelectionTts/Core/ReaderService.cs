using System.Diagnostics;
using SelectionTts.Audio;
using SelectionTts.Core;
using SelectionTts.Platform;
using SelectionTts.Tts;

namespace SelectionTts.Core;

/// <summary>
/// 朗读编排：取词 → 合成 → 播放。
/// 新的请求总是打断旧的（停音频、取消合成），不再用“正在朗读中”拒绝。
/// </summary>
public sealed class ReaderService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly EdgeTtsClient _tts = new();
    private readonly AudioPlayer _player = new();
    private readonly object _gate = new();
    private int _requestId;
    private CancellationTokenSource? _cts;

    public event Action<string>? StatusChanged;
    public event Action<string>? Failed;

    public ReaderService(AppSettings settings)
    {
        _settings = settings;
    }

    public void ReadSelection(int waitReleaseMs = 250) =>
        Start(preloadedText: null, source: "clipboard", waitReleaseMs);

    /// <summary>朗读浮窗已经取到的文本，不再重复取词。</summary>
    public void ReadText(string text) =>
        Start(preloadedText: text, source: "popup", waitReleaseMs: 0);

    public void Stop()
    {
        lock (_gate)
        {
            _requestId++;
            _cts?.Cancel();
            _player.Stop();
        }

        StatusChanged?.Invoke("已停止");
    }

    private void Start(string? preloadedText, string source, int waitReleaseMs)
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
            if (generation > 1)
            {
                Log.Info("新的朗读请求，打断上一次朗读");
            }
        }

        Task.Run(() => Worker(generation, preloadedText, source, waitReleaseMs, token), token);
    }

    private async Task Worker(
        int generation,
        string? preloadedText,
        string source,
        int waitReleaseMs,
        CancellationToken token)
    {
        var watch = Stopwatch.StartNew();

        try
        {
            string raw;
            string method;
            var selectionMs = 0;

            if (preloadedText is not null)
            {
                raw = preloadedText;
                method = source;
            }
            else
            {
                StatusChanged?.Invoke("正在读取选中文本…");
                var selection = SelectionReader.Read(
                    _settings.UseUia,
                    _settings.ClipboardRestore,
                    waitReleaseMs);

                if (IsStale(generation))
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(selection.Text))
                {
                    StatusChanged?.Invoke("没有检测到选中文本");
                    Log.Info("取词结果为空");
                    return;
                }

                raw = selection.Text;
                method = selection.Method;
                selectionMs = selection.ElapsedMs;
            }

            var (text, truncated) = TextUtil.Truncate(TextUtil.Normalize(raw), _settings.MaxChars);
            if (text.Length == 0)
            {
                StatusChanged?.Invoke("没有检测到选中文本");
                return;
            }

            if (truncated)
            {
                StatusChanged?.Invoke($"文本较长，仅朗读前 {_settings.MaxChars} 字");
            }

            StatusChanged?.Invoke($"正在合成（{text.Length} 字）…");
            var audio = await _tts.SynthesizeAsync(text, _settings.Voice, _settings.Rate, token);

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
                    if (!IsStale(generation))
                    {
                        StatusChanged?.Invoke(ok ? "朗读完成" : "已停止");
                    }
                });
            }

            Log.Info(
                $"朗读 {text.Length} 字 | 方法={method} | 取词={selectionMs}ms | " +
                $"合成={audio.ElapsedMs}ms | 尝试={audio.Attempts} | 总计={watch.ElapsedMilliseconds}ms");
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
