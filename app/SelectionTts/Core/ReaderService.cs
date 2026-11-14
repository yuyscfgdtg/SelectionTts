using System.Diagnostics;
using System.Threading.Channels;
using SelectionTts.Audio;
using SelectionTts.Tts;

namespace SelectionTts.Core;

public enum ReadStatus
{
    Idle,
    Preparing,
    Playing,
    Paused,
    Stopped,
    Completed,
    Failed,
}

/// <summary>朗读板渲染需要的全部状态。窗口按固定频率读取，不用事件风暴。</summary>
public sealed record ReadState(
    string Text,
    ReadStatus Status,
    int CharIndex,
    int CharCount,
    double PositionSeconds,
    double DurationSeconds,
    string? Error)
{
    public double Progress => CharCount <= 0
        ? 0
        : Math.Clamp(CharIndex / (double)CharCount, 0, 1);

    public double RemainingSeconds => Math.Max(0, DurationSeconds - PositionSeconds);

    public string StatusText => Status == ReadStatus.Failed && Error is not null
        ? $"朗读失败：{Error}"
        : ReaderService.Describe(Status);
}

/// <summary>
/// 朗读编排：文本 → 切块 → 流式合成 → 连续播放。
///
/// 合成和写入是解耦的两条腿：合成好的块丢进队列，写入线程按顺序喂给播放缓冲。
/// 这样合成可以跑在播放前面，块与块之间不会因为"等解码/等合成"出现停顿，
/// 每块的长度也在入队时就记好了，进度条立刻能算。
///
/// 新文本直接顶掉旧文本（取消合成、停音频、重置时间轴）。
/// </summary>
public sealed class ReaderService : IDisposable
{
    private sealed class SegmentRuntime
    {
        public TextSegment Segment;
        public List<WordSpan> Words = [];
        public long StartPcmBytes;
        public long PcmBytes;
    }

    private readonly record struct WordSpan(int End, double StartSeconds);

    private readonly AppSettings _settings;
    private ISpeechSynthesizer _engine;
    private readonly StreamingAudioPlayer _player = new();
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;

    private int _generation;
    private CancellationTokenSource? _cts;
    private string _text = "";
    private List<SegmentRuntime> _runtime = [];
    private ReadStatus _status = ReadStatus.Idle;
    private string? _error;
    private int _charIndex;
    private bool _feederDone;
    private double _positionFrozen;
    private long _speakStartedAt;

    public ReaderService(AppSettings settings, ISpeechSynthesizer? engine = null)
    {
        _settings = settings;
        _engine = engine ?? new EdgeTtsClient();
        _timer = new System.Threading.Timer(_ => CheckCompletion(), null, 100, 100);
    }

    /// <summary>粗粒度状态变化，用于托盘文案。进度请用 GetState()。</summary>
    public event Action<ReadStatus>? StatusChanged;

    public static string Describe(ReadStatus status) => status switch
    {
        ReadStatus.Idle => "待命",
        ReadStatus.Preparing => "正在合成…",
        ReadStatus.Playing => "正在朗读",
        ReadStatus.Paused => "已暂停",
        ReadStatus.Stopped => "已停止",
        ReadStatus.Completed => "朗读完成",
        ReadStatus.Failed => "朗读失败",
        _ => "",
    };

    public ReadState GetState()
    {
        lock (_gate)
        {
            if (_status is ReadStatus.Playing or ReadStatus.Paused)
            {
                UpdateCharIndexLocked();
            }

            if (_charIndex > _text.Length)
            {
                _charIndex = _text.Length;
            }

            var position = _status is ReadStatus.Preparing or ReadStatus.Playing or ReadStatus.Paused
                ? _player.PositionSeconds
                : _positionFrozen;

            return new ReadState(
                _text,
                _status,
                _charIndex,
                _text.Length,
                position,
                EstimateDurationLocked(),
                _error);
        }
    }

    /// <summary>切换合成引擎，下一条朗读生效（正在朗读的那条不受影响）。</summary>
    public void SetEngine(ISpeechSynthesizer engine) => _engine = engine;

    /// <summary>新的朗读目标。整篇替换，旧的一律作废。</summary>
    public void Speak(string text)
    {
        var (normalized, truncated) = TextUtil.Truncate(
            TextUtil.Normalize(text), _settings.MaxChars);

        if (normalized.Length == 0)
        {
            Log.Info("没有可朗读的文本");
            return;
        }

        if (truncated)
        {
            Log.Info($"文本超过 {_settings.MaxChars} 字，截断朗读");
        }

        Start(normalized, 0, 0);
    }

    /// <summary>从某一块开始重读（朗读板上点某一块触发）。</summary>
    public void JumpTo(int charIndex)
    {
        string text;
        int segmentIndex;

        lock (_gate)
        {
            if (_text.Length == 0 || _runtime.Count == 0)
            {
                return;
            }

            text = _text;
            segmentIndex = 0;
            for (var i = 0; i < _runtime.Count; i++)
            {
                if (_runtime[i].Segment.End > charIndex)
                {
                    segmentIndex = i;
                    break;
                }
            }
        }

        Start(text, segmentIndex, -1);
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_status != ReadStatus.Playing)
            {
                return;
            }

            _status = ReadStatus.Paused;
        }

        _player.Pause();
        Log.Info("status: 已暂停");
        RaiseStatus();
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_status != ReadStatus.Paused)
            {
                return;
            }

            _status = ReadStatus.Playing;
        }

        _player.Resume();
        Log.Info("status: 继续朗读");
        RaiseStatus();
    }

    /// <summary>停止播放，保留文本和当前位置（方便在板上回看）。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_status is ReadStatus.Idle or ReadStatus.Stopped or ReadStatus.Completed or ReadStatus.Failed)
            {
                return;
            }

            UpdateCharIndexLocked();
            _positionFrozen = _player.PositionSeconds;
            _status = ReadStatus.Stopped;
            _generation++;
            _cts?.Cancel();
            _cts = null;
            _feederDone = true;
        }

        _player.Stop();
        Log.Info("status: 已停止");
        RaiseStatus();
    }

    /// <summary>总开关关闭：停止 + 释放文本（内存里不留副本）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _generation++;
            _cts?.Cancel();
            _cts = null;
            _text = "";
            _runtime = [];
            _charIndex = 0;
            _error = null;
            _feederDone = true;
            _positionFrozen = 0;
            _status = ReadStatus.Idle;
        }

        _player.Stop();
        RaiseStatus();
    }

    private void Start(string text, int startSegment, int resetCharIndex)
    {
        int generation;
        CancellationToken token;

        lock (_gate)
        {
            generation = ++_generation;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            token = _cts.Token;

            _text = text;
            _runtime = [];
            _error = null;
            _status = ReadStatus.Preparing;
            _feederDone = false;
            _positionFrozen = 0;
            _charIndex = Math.Max(0, resetCharIndex);
            _speakStartedAt = Environment.TickCount64;
        }

        _player.Stop();
        RaiseStatus();

        Task.Run(() => WorkerAsync(generation, text, startSegment, token), token);
    }

    private async Task WorkerAsync(int generation, string text, int startSegment, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        var segments = TextSegmenter.Split(text);
        if (segments.Count == 0)
        {
            Fail(generation, new InvalidOperationException("没有可朗读的文本"));
            return;
        }

        var target = Math.Clamp(startSegment, 0, segments.Count - 1);
        var runtime = segments.Select(segment => new SegmentRuntime { Segment = segment }).ToList();

        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(3)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        int playerGeneration;
        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            _runtime = runtime;
            if (target > 0)
            {
                _charIndex = runtime[target].Segment.Start;
            }

            playerGeneration = _player.Begin();
        }

        var writer = Task.Run(() => WriterLoopAsync(playerGeneration, channel.Reader, token), token);
        long pcmCursor = 0;
        long firstAudioAt = -1;

        try
        {
            for (var i = target; i < runtime.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (IsStale(generation))
                {
                    return;
                }

                var item = runtime[i];
                var synthesized = await SynthesizeAsync(item.Segment.Text, token);
                if (IsStale(generation))
                {
                    return;
                }

                var pcm = StreamingAudioPlayer.DecodeMp3(synthesized.Audio);

                // 记账在入队时就做完：播放进度立刻能算出这一段在时间轴上的位置
                item.Words = BuildWordSpans(item.Segment.Text, synthesized.Boundaries);
                item.StartPcmBytes = pcmCursor;
                item.PcmBytes = pcm.Length;
                pcmCursor += pcm.Length;

                if (firstAudioAt < 0)
                {
                    firstAudioAt = watch.ElapsedMilliseconds;
                    lock (_gate)
                    {
                        if (generation == _generation && _status == ReadStatus.Preparing)
                        {
                            _status = ReadStatus.Playing;
                        }
                    }

                    RaiseStatus();
                }

                await channel.Writer.WriteAsync(pcm, token);

                Log.Info(
                    $"分块 {i + 1}/{runtime.Count} | {item.Segment.Length} 字 | " +
                    $"合成={synthesized.ElapsedMs}ms | 音频={pcm.Length / (double)StreamingAudioPlayer.BytesPerSecond:F2}s | " +
                    $"时间戳={synthesized.Boundaries.Count}");
            }

            channel.Writer.TryComplete();

            Log.Info(
                $"朗读 {text.Length} 字 | 分块={runtime.Count} | 首声={firstAudioAt}ms | " +
                $"合成总耗时={watch.ElapsedMilliseconds}ms | 引擎={_engine.Name}");

            // 等所有音频写进缓冲（缓冲只有 10 秒，所以这里会随播放推进而逐步完成）
            await writer;

            _player.MarkFeederDone();
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _feederDone = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info("朗读已取消");
        }
        catch (Exception ex)
        {
            if (!IsStale(generation))
            {
                Fail(generation, ex);
            }
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    private async Task WriterLoopAsync(int playerGeneration, ChannelReader<byte[]> reader, CancellationToken token)
    {
        try
        {
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var pcm))
                {
                    await _player.WritePcmAsync(pcm, playerGeneration, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 抢占/停止时的正常路径
        }
    }

    private readonly record struct Synthesized(byte[] Audio, List<SpeechBoundary> Boundaries, int ElapsedMs);

    private async Task<Synthesized> SynthesizeAsync(string text, CancellationToken token)
    {
        using var audio = new MemoryStream();
        var boundaries = new List<SpeechBoundary>();

        var summary = await _engine.StreamAsync(
            text,
            _settings.Voice,
            _settings.Rate,
            buffer => audio.Write(buffer.Span),
            boundary => boundaries.Add(boundary),
            token);

        return new Synthesized(audio.ToArray(), boundaries, summary.ElapsedMs);
    }

    /// <summary>
    /// 把服务端返回的词时间戳对齐到原文字符位置。
    /// 服务端会对数字/单位做规范化（"2024" 念成"二零二四"），对不上就退回游标，
    /// 只损失这一处精度，不会导致整体错位。
    /// </summary>
    private static List<WordSpan> BuildWordSpans(string text, List<SpeechBoundary> boundaries)
    {
        var spans = new List<WordSpan>(boundaries.Count);
        var cursor = 0;

        foreach (var boundary in boundaries)
        {
            var word = boundary.Text ?? "";
            var start = cursor;

            if (word.Length > 0)
            {
                var found = cursor < text.Length
                    ? text.IndexOf(word, cursor, StringComparison.Ordinal)
                    : -1;

                if (found < 0)
                {
                    found = text.IndexOf(word, StringComparison.Ordinal);
                }

                if (found >= 0)
                {
                    start = found;
                }
            }

            var end = Math.Min(text.Length, start + word.Length);
            spans.Add(new WordSpan(end, boundary.OffsetSeconds));
            cursor = Math.Max(cursor, end);
        }

        return spans;
    }

    private void UpdateCharIndexLocked()
    {
        if (_runtime.Count == 0 || _text.Length == 0)
        {
            return;
        }

        var posBytes = (long)(_player.PositionSeconds * StreamingAudioPlayer.BytesPerSecond);

        SegmentRuntime? current = null;
        foreach (var item in _runtime)
        {
            if (item.PcmBytes <= 0)
            {
                break;
            }

            if (posBytes < item.StartPcmBytes + item.PcmBytes)
            {
                current = item;
                break;
            }
        }

        if (current is null)
        {
            var last = _runtime.LastOrDefault(item => item.PcmBytes > 0);
            _charIndex = last?.Segment.End ?? _charIndex;
            return;
        }

        var localSeconds = (posBytes - current.StartPcmBytes) / (double)StreamingAudioPlayer.BytesPerSecond;

        if (current.Words.Count == 0)
        {
            var ratio = current.PcmBytes <= 0
                ? 0
                : Math.Clamp(localSeconds * StreamingAudioPlayer.BytesPerSecond / current.PcmBytes, 0, 1);
            _charIndex = current.Segment.Start + (int)(current.Segment.Length * ratio);
            return;
        }

        var end = current.Segment.Start;
        foreach (var word in current.Words)
        {
            if (word.StartSeconds > localSeconds)
            {
                break;
            }

            end = current.Segment.Start + word.End;
        }

        _charIndex = Math.Clamp(end, current.Segment.Start, current.Segment.End);
    }

    private double EstimateDurationLocked()
    {
        var synthesized = 0d;
        var synthesizedChars = 0;
        var pendingChars = 0;

        foreach (var item in _runtime)
        {
            if (item.PcmBytes > 0)
            {
                synthesized += item.PcmBytes / (double)StreamingAudioPlayer.BytesPerSecond;
                synthesizedChars += item.Segment.Length;
            }
            else
            {
                pendingChars += item.Segment.Length;
            }
        }

        if (synthesizedChars == 0)
        {
            return 0;
        }

        return synthesized + pendingChars * (synthesized / synthesizedChars);
    }

    private void CheckCompletion()
    {
        var completed = false;
        var detail = "";

        lock (_gate)
        {
            if (_status == ReadStatus.Playing && _feederDone && _player.IsDrained)
            {
                UpdateCharIndexLocked();
                _charIndex = _text.Length;
                _positionFrozen = EstimateDurationLocked();
                _status = ReadStatus.Completed;
                completed = true;
                detail = $"{_text.Length} 字 | 用时={(Environment.TickCount64 - _speakStartedAt) / 1000.0:F1}s";
            }
        }

        if (!completed)
        {
            return;
        }

        Log.Info($"status: 朗读完成 | {detail}");
        StatusChanged?.Invoke(ReadStatus.Completed);
    }

    private void Fail(int generation, Exception ex)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            UpdateCharIndexLocked();
            _positionFrozen = _player.PositionSeconds;
            _status = ReadStatus.Failed;
            _error = ex.Message;
            _feederDone = true;
        }

        _player.Stop();
        Log.Error("朗读失败", ex);
        RaiseStatus();
    }

    private bool IsStale(int generation)
    {
        lock (_gate)
        {
            return generation != _generation;
        }
    }

    private void RaiseStatus()
    {
        ReadStatus status;
        lock (_gate)
        {
            status = _status;
        }

        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate)
        {
            _generation++;
            _cts?.Cancel();
        }

        _player.Dispose();
    }
}
