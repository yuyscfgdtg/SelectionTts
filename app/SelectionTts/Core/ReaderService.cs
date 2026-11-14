using System.Diagnostics;
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
    string? Error,
    int SynthChars,
    int SynthTotal,
    bool SynthesisDone)
{
    public double Progress => CharCount <= 0
        ? 0
        : Math.Clamp(CharIndex / (double)CharCount, 0, 1);

    /// <summary>全文里已经合成（缓存好）的比例。进度条只表示这个。</summary>
    public double SynthesisProgress => SynthesisDone
        ? 1
        : SynthTotal <= 0
            ? 0
            : Math.Clamp(SynthChars / (double)SynthTotal, 0, 1);

    public double RemainingSeconds => Math.Max(0, DurationSeconds - PositionSeconds);

    public string StatusText => Status == ReadStatus.Failed && Error is not null
        ? $"朗读失败：{Error}"
        : ReaderService.Describe(Status);
}

/// <summary>
/// 朗读编排：一句一段做缓存，像流媒体那样"边缓冲边播放"。
///
/// - 复制后从第 1 句开始合成，合成好的句子进缓存
/// - 播放头按句子顺序取缓存，命中即播，没命中就等这一句合成完（最多一句的等待）
/// - 点某一行 = seek 到那一行所在句子：已缓存秒开，未缓存就让合成器优先做这一句
/// - 读到结尾就算读完；剩下没合成的句子在后台补齐（不朗读），进度条补到 100%
/// - 换新文本时整块丢弃旧缓存，内存里只保留当前这一段文本
/// </summary>
public sealed class ReaderService : IDisposable
{
    private sealed class SegmentRuntime
    {
        public TextSegment Segment = default;
        public List<WordSpan> Words = [];
        public TaskCompletionSource<byte[]> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[]? Mp3;
        public double DurationSeconds;
        public long StartPcmBytes;
        public long PcmBytes;

        /// <summary>这一句合成失败被跳过（缓存空音频，进度仍算它已处理）。</summary>
        public bool Failed;
    }

    private readonly record struct WordSpan(int End, double StartSeconds);

    private readonly AppSettings _settings;
    private readonly StreamingAudioPlayer _player = new();
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;

    private ISpeechSynthesizer _engine;

    private int _generation;          // 文本级：换文本就 +1
    private int _session;             // 播放级：每次 seek 就 +1
    private CancellationTokenSource? _cts;         // 文本级取消
    private CancellationTokenSource? _sessionCts;  // 播放级取消

    private string _text = "";
    private List<SegmentRuntime> _plan = [];
    private ReadStatus _status = ReadStatus.Idle;
    private string? _error;
    private int _charIndex;
    private int _sessionStart;
    private bool _feederDone;
    private double _positionFrozen;
    private long _speakStartedAt;

    private int _jumpTarget = -1;   // 合成器优先处理这一句
    private int _synthCursor;       // 顺序合成指针
    private ReadStatus _lastRaised = ReadStatus.Idle;

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

    /// <summary>切换合成引擎，下一条朗读生效（正在朗读的那条不受影响）。</summary>
    public void SetEngine(ISpeechSynthesizer engine) => _engine = engine;

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

            var synthChars = 0;
            var done = _plan.Count > 0;

            foreach (var item in _plan)
            {
                if (item.Mp3 is not null)
                {
                    synthChars += item.Segment.Length;
                }
                else
                {
                    done = false;
                }
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
                _error,
                synthChars,
                _text.Length,
                done);
        }
    }

    /// <summary>新的朗读目标。整篇替换，旧缓存立即丢弃。</summary>
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

        StartNew(normalized);
    }

    /// <summary>seek 到某一行：落到这一行所在句子的开头。</summary>
    public void JumpTo(int charIndex)
    {
        int generation;
        int session;
        int startIndex;
        CancellationToken token;

        lock (_gate)
        {
            if (_text.Length == 0 || _plan.Count == 0)
            {
                return;
            }

            var sentenceStart = TextSegmenter.SentenceStart(_text, charIndex);
            startIndex = FindSegmentLocked(sentenceStart);
            if (startIndex < 0)
            {
                return;
            }

            _jumpTarget = startIndex;
            _charIndex = sentenceStart;
            _positionFrozen = 0;
            session = ++_session;
            _sessionCts?.Cancel();
            _sessionCts = new CancellationTokenSource();
            token = _sessionCts.Token;
            generation = _generation;
            _status = _plan[startIndex].Mp3 is null ? ReadStatus.Preparing : ReadStatus.Playing;
        }

        _player.Stop();
        Log.Info($"seek：第 {startIndex + 1}/{_plan.Count} 句起朗读");
        RaiseStatus();

        _ = Task.Run(() => PlaybackLoopAsync(generation, session, startIndex, token), token);
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_status is not (ReadStatus.Playing or ReadStatus.Preparing))
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

    /// <summary>停止播放，保留文本和缓存。</summary>
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
            _session++;
            _sessionCts?.Cancel();
            _sessionCts = null;
            _feederDone = true;
        }

        _player.Stop();
        Log.Info("status: 已停止");
        RaiseStatus();
    }

    /// <summary>总开关关闭：停止 + 丢弃文本与全部音频缓存。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _generation++;
            _cts?.Cancel();
            _cts = null;
            _session++;
            _sessionCts?.Cancel();
            _sessionCts = null;

            _text = "";
            _plan = [];
            _charIndex = 0;
            _error = null;
            _feederDone = true;
            _positionFrozen = 0;
            _jumpTarget = -1;
            _synthCursor = 0;
            _sessionStart = 0;
            _status = ReadStatus.Idle;
        }

        _player.Stop();
        RaiseStatus();
    }

    private void StartNew(string text)
    {
        int generation;
        CancellationToken token;
        List<SegmentRuntime> plan;

        lock (_gate)
        {
            // 整块丢弃旧缓存
            _generation++;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
            generation = _generation;

            _session++;
            _sessionCts?.Cancel();
            _sessionCts = null;

            _text = text;
            _plan = [];
            _error = null;
            _status = ReadStatus.Preparing;
            _feederDone = false;
            _positionFrozen = 0;
            _charIndex = 0;
            _jumpTarget = -1;
            _synthCursor = 0;
            _sessionStart = 0;
            _speakStartedAt = Environment.TickCount64;
        }

        _player.Stop();

        plan = TextSegmenter.SplitSentences(text)
            .Select(segment => new SegmentRuntime { Segment = segment })
            .ToList();

        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            _plan = plan;
        }

        if (plan.Count == 0)
        {
            Fail(generation, new InvalidOperationException("没有可朗读的文本"));
            return;
        }

        Log.Info($"开始朗读 {text.Length} 字 | {plan.Count} 句 | 引擎={_engine.Name}");
        RaiseStatus();

        _ = Task.Run(() => SynthesisLoopAsync(generation, token), token);
        JumpToStart(generation);
    }

    private void JumpToStart(int generation)
    {
        int session;
        CancellationToken token;

        lock (_gate)
        {
            if (generation != _generation || _plan.Count == 0)
            {
                return;
            }

            session = ++_session;
            _sessionCts?.Cancel();
            _sessionCts = new CancellationTokenSource();
            token = _sessionCts.Token;
        }

        _ = Task.Run(() => PlaybackLoopAsync(generation, session, 0, token), token);
    }

    /// <summary>合成器：优先补 seek 目标，然后顺序往后，到结尾后回头补空洞。</summary>
    private async Task SynthesisLoopAsync(int generation, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();

        try
        {
            while (!token.IsCancellationRequested)
            {
                int index;
                SegmentRuntime item;
                int total;

                lock (_gate)
                {
                    if (generation != _generation)
                    {
                        return;
                    }

                    index = TakeNextIndexLocked();
                    if (index < 0)
                    {
                        break;
                    }

                    item = _plan[index];
                    total = _plan.Count;
                }

                var synthesized = await SynthesizeWithRetryAsync(generation, index, total, item, token);
                if (synthesized is null)
                {
                    continue;
                }

                lock (_gate)
                {
                    if (generation != _generation)
                    {
                        return;
                    }

                    item.Words = BuildWordSpans(item.Segment.Text, synthesized.Value.Boundaries);
                    item.Mp3 = synthesized.Value.Audio;
                    item.DurationSeconds = synthesized.Value.Audio.Length * 8d / 48_000d;
                }

                item.Ready.TrySetResult(synthesized.Value.Audio);

                Log.Info(
                    $"合成 {index + 1}/{total} 句 | {item.Segment.Length} 字 | " +
                    $"{synthesized.Value.ElapsedMs}ms | 音频≈{item.DurationSeconds:F2}s");
            }

            Log.Info($"合成完成 | 全程 {watch.ElapsedMilliseconds}ms | 引擎={_engine.Name}");
        }
        catch (OperationCanceledException)
        {
            Log.Info("合成已取消");
        }
        catch (Exception ex)
        {
            if (generation == _generation)
            {
                Fail(generation, ex);
            }
        }
    }

    private int TakeNextIndexLocked()
    {
        var count = _plan.Count;

        if (_jumpTarget >= 0 && _jumpTarget < count && _plan[_jumpTarget].Mp3 is null)
        {
            var jump = _jumpTarget;
            _jumpTarget = -1;
            _synthCursor = jump;
            return jump;
        }

        _jumpTarget = -1;

        for (var i = _synthCursor; i < count; i++)
        {
            if (_plan[i].Mp3 is null)
            {
                _synthCursor = i;
                return i;
            }
        }

        // 已经合成到结尾，回头补前面没合成的
        for (var i = 0; i < Math.Min(_synthCursor, count); i++)
        {
            if (_plan[i].Mp3 is null)
            {
                _synthCursor = i;
                return i;
            }
        }

        return -1;
    }

    private readonly record struct Synthesized(byte[] Audio, List<SpeechBoundary> Boundaries, int ElapsedMs);

    /// <summary>
    /// 合成一句，失败重试一次；再失败就跳过这一句（缓存空音频），不让整篇朗读跟着挂掉。
    /// 返回 null 表示已跳过。
    /// </summary>
    private async Task<Synthesized?> SynthesizeWithRetryAsync(
        int generation, int index, int total, SegmentRuntime item, CancellationToken token)
    {
        try
        {
            return await SynthesizeAsync(item.Segment.Text, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"第 {index + 1}/{total} 句合成失败，重试一次：{ex.Message}");
        }

        await Task.Delay(600, token);

        try
        {
            return await SynthesizeAsync(item.Segment.Text, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"第 {index + 1}/{total} 句合成失败，跳过这一句", ex);
        }

        lock (_gate)
        {
            if (generation != _generation)
            {
                return null;
            }

            item.Mp3 = [];
            item.Failed = true;
            item.DurationSeconds = 0;
        }

        item.Ready.TrySetResult([]);
        return null;
    }

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

    /// <summary>播放会话：从第 startIndex 句开始，顺序取缓存喂给播放缓冲。</summary>
    private async Task PlaybackLoopAsync(int generation, int session, int startIndex, CancellationToken token)
    {
        List<SegmentRuntime> plan;
        int playerGeneration;

        lock (_gate)
        {
            if (generation != _generation || session != _session)
            {
                return;
            }

            plan = _plan;
            if (startIndex >= plan.Count)
            {
                return;
            }

            foreach (var item in plan)
            {
                item.StartPcmBytes = 0;
                item.PcmBytes = 0;
            }

            _sessionStart = startIndex;
            _feederDone = false;
            _charIndex = plan[startIndex].Segment.Start;
            playerGeneration = _player.Begin();

            // 用户可能在这几十毫秒的窗口里按了暂停：Begin 会把输出重新拉起来，这里补一次暂停
            if (_status == ReadStatus.Paused)
            {
                _player.Pause();
            }
        }

        var watch = Stopwatch.StartNew();
        long cursor = 0;
        long firstAudioAt = -1;

        try
        {
            for (var i = startIndex; i < plan.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var mp3 = await plan[i].Ready.Task.WaitAsync(token);
                if (IsStale(generation, session))
                {
                    return;
                }

                var pcm = StreamingAudioPlayer.DecodeMp3(mp3);

                lock (_gate)
                {
                    if (generation != _generation || session != _session)
                    {
                        return;
                    }

                    plan[i].StartPcmBytes = cursor;
                    plan[i].PcmBytes = pcm.Length;

                    if (firstAudioAt < 0 && _status == ReadStatus.Preparing)
                    {
                        _status = ReadStatus.Playing;
                    }
                }

                if (firstAudioAt < 0)
                {
                    firstAudioAt = watch.ElapsedMilliseconds;
                    Log.Info($"首声={firstAudioAt}ms | 从第 {startIndex + 1} 句开始");
                    RaiseStatus();
                }

                cursor += pcm.Length;
                await _player.WritePcmAsync(pcm, playerGeneration, token);
            }

            _player.MarkFeederDone();
            lock (_gate)
            {
                if (generation == _generation && session == _session)
                {
                    _feederDone = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // seek / 换文本时的正常路径
        }
        catch (Exception ex)
        {
            if (!IsStale(generation, session))
            {
                Fail(generation, ex);
            }
        }
    }

    private void UpdateCharIndexLocked()
    {
        if (_plan.Count == 0 || _text.Length == 0)
        {
            return;
        }

        var posBytes = (long)(_player.PositionSeconds * StreamingAudioPlayer.BytesPerSecond);

        SegmentRuntime? current = null;
        for (var i = _sessionStart; i < _plan.Count; i++)
        {
            var item = _plan[i];
            if (item.PcmBytes <= 0)
            {
                // 合成失败被跳过的句子没有音频，直接跳过继续找
                if (item.Failed)
                {
                    continue;
                }

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
            var last = _plan.LastOrDefault(item => item.PcmBytes > 0);
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
        var cachedSeconds = 0d;
        var cachedChars = 0;
        var pendingChars = 0;

        foreach (var item in _plan)
        {
            if (item.Mp3 is not null)
            {
                cachedSeconds += item.DurationSeconds;
                cachedChars += item.Segment.Length;
            }
            else
            {
                pendingChars += item.Segment.Length;
            }
        }

        if (cachedChars == 0)
        {
            return 0;
        }

        return cachedSeconds + pendingChars * (cachedSeconds / cachedChars);
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

        Log.Info($"朗读结束 | {detail}");
        RaiseStatus();
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
            _session++;
            _sessionCts?.Cancel();

            // 让还卡在"等这一句合成"的播放循环立刻退出，不要一直挂着
            foreach (var item in _plan)
            {
                item.Ready.TrySetCanceled();
            }
        }

        _player.Stop();
        Log.Error("朗读失败", ex);
        RaiseStatus();
    }

    private int FindSegmentLocked(int charIndex)
    {
        for (var i = 0; i < _plan.Count; i++)
        {
            if (_plan[i].Segment.End > charIndex)
            {
                return i;
            }
        }

        return _plan.Count - 1;
    }

    private bool IsStale(int generation, int session)
    {
        lock (_gate)
        {
            return generation != _generation || session != _session;
        }
    }

    /// <summary>
    /// 把服务端返回的词时间戳对齐到原文字符位置。
    /// 服务端会对数字/单位做规范化，对不上就退回游标。
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

    private void RaiseStatus()
    {
        ReadStatus status;
        lock (_gate)
        {
            status = _status;
            if (status == _lastRaised)
            {
                return;
            }

            _lastRaised = status;
        }

        Log.Info($"status: {Describe(status)}");
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate)
        {
            _generation++;
            _cts?.Cancel();
            _sessionCts?.Cancel();
        }

        _player.Dispose();
    }
}
