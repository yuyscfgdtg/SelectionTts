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
    bool SynthesisDone,
    IReadOnlyList<Clause> Clauses)
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
///
/// 并发模型（改代码前先读这段）：
/// - 所有可变字段只允许在 <see cref="_gate"/> 内读写；方法名带 Locked 的表示"调用方已持锁"。
/// - "这次还有效吗"只有一个判据：捕获的作用域对象是否仍是当前对象（引用比较）。
///   不要新增递增的版本号字段——两个计数器各自递增，最容易配错。
/// - 状态只能通过 <see cref="TransitionLocked"/> 修改，不要直接写 _status。
/// </summary>
public sealed class ReaderService : IDisposable
{
    private sealed class SegmentRuntime
    {
        public TextSegment Segment = default;

        /// <summary>该段对应的原始整句范围。重切后仍保留，供再次点击前半部分时恢复。</summary>
        public TextSegment Original = default;

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

    /// <summary>本次朗读的合成参数快照：朗读中途换音色/语速/引擎不影响正在读的这篇。</summary>
    private sealed record ReadingOptions(string Voice, string Rate, ISpeechSynthesizer Engine);

    /// <summary>
    /// 一次「合成批次」：换文本、失败后点击重启，都会新建一个。
    /// 旧批次的后台线程只做引用比较就立刻退出，不需要再核对任何数字。
    /// </summary>
    private sealed class SynthesisScope
    {
        public CancellationTokenSource Cts { get; } = new();

        public CancellationToken Token => Cts.Token;
    }

    /// <summary>
    /// 一次「播放会话」：每次跳转/停止/换文本都新建一个。
    /// 它绑定创建时的合成批次——批次失效，会话一定失效。
    /// </summary>
    private sealed class PlaybackScope(SynthesisScope synth)
    {
        public SynthesisScope Synth { get; } = synth;

        public CancellationTokenSource Cts { get; } = new();

        public CancellationToken Token => Cts.Token;
    }

    private readonly AppSettings _settings;
    private readonly IAudioPlayer _player;
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;

    private ISpeechSynthesizer _engine;
    private ReadingOptions? _options;

    // 当前合成批次 / 播放会话。null 表示没有正在播的会话（空闲、停止、完成、失败）。
    private SynthesisScope _synthScope = new();
    private PlaybackScope? _playScope;

    private string _text = "";
    private List<SegmentRuntime> _plan = [];
    private List<Clause> _clauses = [];   // 标点子句表（点击与高亮的单位）
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
    private long _lastDiagAt;
    private int _sessionSkipBytes;      // 本次会话从段首跳过的 PCM 字节
    private int _sessionSkipCharOffset; // 未合成时记下点击词偏移，合成完成后补算
    private int _sessionStartChar;      // 本次会话的起始字符（跳转时=点击的子句首字）
    private double _positionOffsetSeconds; // 本次会话在整篇中的起始秒数
    private bool _mappingFrozen;        // 刚跳转、播放会话还没重建时，暂停位置映射
    private bool _synthHalted;          // 合成线程已停（失败熔断）：点击可从点击处重新开始

    /// <summary>--diag-highlight：把位置/段/字符的换算过程写进日志，用于定位高亮问题。</summary>
    internal static bool Diag { get; set; }

    public ReaderService(AppSettings settings, ISpeechSynthesizer? engine = null, IAudioPlayer? player = null)
    {
        _settings = settings;
        _engine = engine ?? new EdgeTtsClient();
        _player = player ?? new StreamingAudioPlayer();
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

    // —— 会话身份：跨线程判断"我还是当前那张票吗" ——

    /// <summary>合成批次已不是当前批次：换文本、失败重启、Clear、Dispose。</summary>
    private bool IsSynthStale(SynthesisScope scope)
    {
        lock (_gate)
        {
            return !ReferenceEquals(scope, _synthScope);
        }
    }

    /// <summary>播放会话已失效：跳转、停止、换文本、失败，或它所属的合成批次已换。</summary>
    private bool IsPlayStale(PlaybackScope play)
    {
        lock (_gate)
        {
            return !ReferenceEquals(play, _playScope) ||
                   !ReferenceEquals(play.Synth, _synthScope);
        }
    }

    /// <summary>换一个合成批次（旧的立即取消）。</summary>
    private SynthesisScope NewSynthScopeLocked()
    {
        _synthScope.Cts.Cancel();
        _synthScope = new SynthesisScope();
        return _synthScope;
    }

    /// <summary>换一个播放会话（旧的立即取消）。</summary>
    private PlaybackScope NewPlayScopeLocked()
    {
        _playScope?.Cts.Cancel();
        _playScope = new PlaybackScope(_synthScope);
        return _playScope;
    }

    /// <summary>结束当前播放会话，不建立新的。</summary>
    private void EndPlayScopeLocked()
    {
        _playScope?.Cts.Cancel();
        _playScope = null;
    }

    /// <summary>
    /// 状态迁移的唯一入口。
    ///
    /// 为什么集中：以前 _status 有八九处赋值点，出问题时日志只能看到"当前是什么"，
    /// 看不到"从什么变成什么、谁改的"。现在每次迁移都记一行，异常迁移单独 WARN。
    /// 只记录不阻断——真实设备上的时序问题不能因为一次断言变得更糟。
    /// </summary>
    private void TransitionLocked(ReadStatus to)
    {
        if (_status == to)
        {
            return;
        }

        if (IsImpossibleTransition(_status, to))
        {
            Log.Warn($"status 异常迁移：{Describe(_status)} → {Describe(to)}");
        }
        else
        {
            Log.Info($"status: {Describe(_status)} → {Describe(to)}");
        }

        _status = to;
    }

    /// <summary>正常路径不会出现的迁移。只用来在日志里报警，不改变行为。</summary>
    private static bool IsImpossibleTransition(ReadStatus from, ReadStatus to) => to switch
    {
        ReadStatus.Playing => from is not (
            ReadStatus.Preparing or ReadStatus.Paused or ReadStatus.Stopped or
            ReadStatus.Completed or ReadStatus.Failed),
        ReadStatus.Paused => from is not (ReadStatus.Preparing or ReadStatus.Playing),
        ReadStatus.Completed => from is not ReadStatus.Playing,
        ReadStatus.Stopped => from is not (
            ReadStatus.Preparing or ReadStatus.Playing or ReadStatus.Paused),
        _ => false,
    };

    private double BytesPerSecond()
    {
        var bps = _player.BytesPerSecond;
        return bps > 0 ? bps : StreamingAudioPlayer.DefaultBytesPerSecond;
    }

    /// <summary>还有句子没合成完（缓存为空）。失败后点击要靠它判断是否值得重启合成。</summary>
    private bool HasPendingLocked() => _plan.Any(item => item.Mp3 is null);

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
                ? _player.PositionSeconds + _positionOffsetSeconds
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
                done,
                _clauses);
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

    /// <summary>
    /// 歌词式跳转：不修改文本、不丢缓存，只改变播放起点。
    /// charIndex 是点击命中子句的第一个字（由朗读板按 ClauseMap 换算）：
    /// 已合成句直接从该字的 PCM 偏移开始播放；未合成句等合成完再补算偏移。
    /// </summary>
    public void JumpTo(int charIndex)
    {
        PlaybackScope play;
        SynthesisScope? restart = null;
        int startIndex;
        int cutIndex;
        int skipBytes;

        lock (_gate)
        {
            if (_text.Length == 0 || _plan.Count == 0)
            {
                return;
            }

            startIndex = FindSegmentByOriginalLocked(
                Math.Clamp(charIndex, 0, Math.Max(0, _text.Length - 1)));
            if (startIndex < 0)
            {
                return;
            }

            var segment = _plan[startIndex];
            var offset = Math.Clamp(
                charIndex - segment.Segment.Start, 0, Math.Max(0, segment.Segment.Length - 1));

            // 点击已经对齐到子句开头，不再往前找词边界：
            // 那会把起点退到上一子句的标点上，声音和高亮都会早一拍。
            var wordStart = offset;

            // 离句首太近就直接从句子开头读，避免为了几个字做偏移
            if (wordStart < 3)
            {
                wordStart = 0;
            }

            cutIndex = segment.Segment.Start + wordStart;

            var bps = BytesPerSecond();

            // 只用已解码过的句内时间；未合成时从句子开头开始
            var wordSeconds = wordStart == 0 || segment.Words.Count == 0
                ? 0
                : WordSecondsAt(segment, wordStart);
            skipBytes = (int)Math.Round(wordSeconds * bps);
            skipBytes -= skipBytes % 2;

            // 只有已经算过本会话 PCM 长度的段才知道能跳多远；
            // 缓存里有音频但还没喂过的段 PcmBytes 是 0，这里不能夹成 0
            // （那会把跳转退化成"从句首读"），交给播放器按解码长度再夹一次。
            if (segment.PcmBytes > 0)
            {
                skipBytes = (int)Math.Clamp((long)skipBytes, 0L, Math.Max(0L, segment.PcmBytes - 2));
            }
            else
            {
                skipBytes = Math.Max(0, skipBytes);
            }

            // 合成失败后点击：换一个合成批次，把还没合成的句子重新挂上就绪闸，
            // 合成线程从点击的这句重新开始。已合成的句子保留缓存，不重复花流量；
            // 失败过的句子（空音频）保持跳过。
            if (_synthHalted && HasPendingLocked())
            {
                restart = NewSynthScopeLocked();
                _synthHalted = false;
                _error = null;

                foreach (var item in _plan)
                {
                    if (item.Mp3 is null)
                    {
                        item.Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }

                _synthCursor = startIndex;
            }

            _jumpTarget = startIndex;
            _charIndex = cutIndex;
            _positionFrozen = 0;
            play = NewPlayScopeLocked();
            _feederDone = false;
            _sessionSkipBytes = skipBytes;
            _sessionSkipCharOffset = segment.Words.Count == 0 ? wordStart : 0;
            _sessionStartChar = cutIndex;
            _positionOffsetSeconds = SegmentStartSecondsLocked(startIndex) + skipBytes / bps;
            _mappingFrozen = true;
            TransitionLocked(segment.Mp3 is null ? ReadStatus.Preparing : ReadStatus.Playing);

            Log.Info(
                $"seek：第 {startIndex + 1}/{_plan.Count} 段，从第 {cutIndex} 字起（偏移 {skipBytes} 字节）");

            // 停旧声音必须在锁内：连点时旧任务的 Stop 晚于新任务的 Begin 执行，
            // 会把新会话一起关掉，真机表现就是"点了没声音"。锁内停完，
            // 任何更新的跳转只可能整体排在本段之后，顺序是确定的。
            _player.Stop();
        }

        RaiseStatus();

        if (restart is not null)
        {
            Log.Info("合成失败后点击：从当前句子重新开始合成");
            _ = Task.Run(() => SynthesisLoopAsync(restart, restart.Token), restart.Token);
        }

        _ = Task.Run(() => PlaybackLoopAsync(play, startIndex, skipBytes), play.Token);
    }

    /// <summary>段内某个字符偏移对应的音频秒数（词时间戳；没有词表时按字数比例）。</summary>
    private static double WordSecondsAt(SegmentRuntime segment, int relative)
    {
        if (segment.Words.Count > 0)
        {
            foreach (var word in segment.Words)
            {
                if (word.End > relative)
                {
                    return word.StartSeconds;
                }
            }

            return segment.DurationSeconds;
        }

        if (segment.Segment.Length <= 0 || segment.DurationSeconds <= 0)
        {
            return 0;
        }

        var ratio = Math.Clamp(relative / (double)segment.Segment.Length, 0, 1);
        return segment.DurationSeconds * ratio;
    }

    /// <summary>该段之前所有句子的估算时长之和（用于跳转后的「剩余时间」显示）。</summary>
    private double SegmentStartSecondsLocked(int index)
    {
        var seconds = 0d;
        for (var i = 0; i < index && i < _plan.Count; i++)
        {
            seconds += _plan[i].DurationSeconds;
        }

        return seconds;
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_status is not (ReadStatus.Playing or ReadStatus.Preparing))
            {
                return;
            }

            TransitionLocked(ReadStatus.Paused);
            _player.Pause();
        }

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

            TransitionLocked(ReadStatus.Playing);
            _player.Resume();
        }

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
            _positionFrozen = _player.PositionSeconds + _positionOffsetSeconds;
            EndPlayScopeLocked();
            _feederDone = true;
            TransitionLocked(ReadStatus.Stopped);
            _player.Stop();
        }

        RaiseStatus();
    }

    /// <summary>总开关关闭：停止 + 丢弃文本与全部音频缓存。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            NewSynthScopeLocked();
            EndPlayScopeLocked();

            _text = "";
            _plan = [];
            _clauses = [];
            _charIndex = 0;
            _error = null;
            _feederDone = true;
            _positionFrozen = 0;
            _jumpTarget = -1;
            _synthCursor = 0;
            _sessionStart = 0;
            _sessionStartChar = 0;
            _sessionSkipBytes = 0;
            _sessionSkipCharOffset = 0;
            _positionOffsetSeconds = 0;
            _mappingFrozen = false;
            _synthHalted = false;
            TransitionLocked(ReadStatus.Idle);
            _player.Stop();
        }

        RaiseStatus();
    }

    private void StartNew(string text)
    {
        // 切句是纯函数（不碰任何共享状态），放在锁外算完。
        // 然后在同一次加锁里把 文本/子句表/句子表 一次写完：
        // 以前先写文本、出锁再切句、第二次进锁才填 plan，中间那几十毫秒里
        // 朗读板上的点击会因为 _plan.Count == 0 被直接丢掉（"点了没反应"）。
        var plan = TextSegmenter.SplitSentences(text)
            .Select(segment => new SegmentRuntime { Segment = segment, Original = segment })
            .ToList();

        var clauses = ClauseMap.Split(text);

        SynthesisScope synth;
        PlaybackScope play;

        lock (_gate)
        {
            // 整块丢弃旧缓存
            synth = NewSynthScopeLocked();
            EndPlayScopeLocked();

            _text = text;
            _plan = plan;
            _clauses = clauses;
            _error = null;
            _feederDone = false;
            _positionFrozen = 0;
            _charIndex = 0;
            _jumpTarget = -1;
            _synthCursor = 0;
            _sessionStart = 0;
            _sessionStartChar = 0;
            _sessionSkipBytes = 0;
            _sessionSkipCharOffset = 0;
            _positionOffsetSeconds = 0;
            _mappingFrozen = true;
            _synthHalted = false;
            _speakStartedAt = Environment.TickCount64;

            // 快照本次朗读的合成参数：之后托盘换音色/语速、设置页换引擎都只影响下一篇
            _options = new ReadingOptions(_settings.Voice, _settings.Rate, _engine);

            // 播放会话和文本在同一次加锁里建好：之后的第一次点击只是换一个会话，
            // 不会再撞上"文本换了但还没准备好"的窗口。
            play = NewPlayScopeLocked();

            TransitionLocked(ReadStatus.Preparing);

            // 设备和状态在同一把锁里停：旧会话的 Stop 不可能落到新会话的 Begin 之后
            _player.Stop();
        }

        if (plan.Count == 0)
        {
            Fail(synth, new InvalidOperationException("没有可朗读的文本"));
            return;
        }

        Log.Info($"开始朗读 {text.Length} 字 | {plan.Count} 句 | 引擎={_options?.Engine.Name ?? _engine.Name}");
        RaiseStatus();

        _ = Task.Run(() => SynthesisLoopAsync(synth, synth.Token), synth.Token);
        _ = Task.Run(() => PlaybackLoopAsync(play, 0, 0), play.Token);
    }

    /// <summary>合成器：优先补 seek 目标，然后顺序往后，到结尾后回头补空洞。</summary>
    private async Task SynthesisLoopAsync(SynthesisScope synth, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        var consecutiveFailures = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                int index;
                SegmentRuntime item;
                int total;

                lock (_gate)
                {
                    if (IsSynthStale(synth))
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

                var synthesized = await SynthesizeWithRetryAsync(synth, index, total, item, token);
                if (synthesized is null)
                {
                    // 单句失败已经重试并跳过，进度照样往前走；
                    // 但连着失败说明是网络/服务整体不可用，继续硬爬只会让进度条
                    // 一点点挪上十几分钟，不如停下来把原因说清楚。
                    // 阈值取 2：单句失败最多已经吃掉 4 次建连（4 秒超时），
                    // 再让第三句陪着超时，用户看到的就是"点了没反应"。
                    if (++consecutiveFailures >= 2)
                    {
                        Log.Warn($"连续 {consecutiveFailures} 句合成失败，停止本篇朗读");
                        Fail(synth, new InvalidOperationException("无法连接语音服务，请检查网络后重试"));
                        return;
                    }

                    continue;
                }

                consecutiveFailures = 0;

                lock (_gate)
                {
                    if (IsSynthStale(synth))
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

            Log.Info($"合成完成 | 全程 {watch.ElapsedMilliseconds}ms | 引擎={_options?.Engine.Name ?? _engine.Name}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Log.Info("合成已取消");
        }
        catch (Exception ex)
        {
            if (!IsSynthStale(synth))
            {
                Fail(synth, ex);
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
        SynthesisScope synth, int index, int total, SegmentRuntime item, CancellationToken token)
    {
        try
        {
            return await SynthesizeAsync(item.Segment.Text, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
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
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"第 {index + 1}/{total} 句合成失败，跳过这一句", ex);
        }

        lock (_gate)
        {
            if (IsSynthStale(synth))
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
        // 用本次朗读开始时拍下的快照，而不是共享配置的当前值
        var options = _options
            ?? new ReadingOptions(_settings.Voice, _settings.Rate, _engine);

        using var audio = new MemoryStream();
        var boundaries = new List<SpeechBoundary>();

        var summary = await options.Engine.StreamAsync(
            text,
            options.Voice,
            options.Rate,
            buffer => audio.Write(buffer.Span),
            boundary => boundaries.Add(boundary),
            token);

        return new Synthesized(audio.ToArray(), boundaries, summary.ElapsedMs);
    }

    /// <summary>播放会话：从第 startIndex 句开始，顺序取缓存喂给播放缓冲。</summary>
    private async Task PlaybackLoopAsync(PlaybackScope play, int startIndex, int skipBytes)
    {
        var token = play.Token;
        List<SegmentRuntime> plan = [];
        int playerSession = 0;
        int skipCharOffset;

        try
        {
            // 设备初始化也在异常边界内：没有音频设备、设备被独占时必须走到 Fail，
            // 而不是让后台任务悄悄 fault、界面永远停在「正在合成」。
            lock (_gate)
            {
                if (IsPlayStale(play))
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
                _mappingFrozen = false;
                skipCharOffset = _sessionSkipCharOffset;
                // 跳转后先把高亮放在点击的子句上，别闪回句首
                _charIndex = Math.Clamp(
                    _sessionStartChar,
                    plan[startIndex].Segment.Start,
                    Math.Max(plan[startIndex].Segment.Start, plan[startIndex].Segment.End - 1));

                // 停旧声音放在锁内：否则连续点击时，旧任务的 Stop 可能晚于新任务
                // 的 Begin 执行，把新会话一起关掉，表现是"点了没声音"。
                // 锁内停完，任何更新的跳转只能在本任务之后执行 Stop，顺序是确定的。
                //
                // 也不在这里开设备：缓冲为空的 WaveOut 会把等待合成的时间当静音
                // 继续推进播放位置，那段会被算成"已经读了"，高亮就会越到点击位置之后。
                // 设备等第一段音频到手再开。
                //
                // 这里不再 Stop：所有入口（换文本 / 跳转 / 停止 / 清理 / 失败）都已经
                // 在同一把锁里停过设备，多停一次只会增加一个可以被新会话越过的窗口。
            }

            var watch = Stopwatch.StartNew();
            long cursor = 0;
            long firstAudioAt = -1;

            for (var i = startIndex; i < plan.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var mp3 = await plan[i].Ready.Task.WaitAsync(token);
                if (IsPlayStale(play))
                {
                    return;
                }

                lock (_gate)
                {
                    if (IsPlayStale(play))
                    {
                        return;
                    }

                    plan[i].StartPcmBytes = cursor;

                    if (firstAudioAt < 0 && _status == ReadStatus.Preparing)
                    {
                        TransitionLocked(ReadStatus.Playing);
                    }
                }

                if (firstAudioAt < 0)
                {
                    firstAudioAt = watch.ElapsedMilliseconds;
                    Log.Info($"首声={firstAudioAt}ms | 从第 {startIndex + 1} 句开始");
                    RaiseStatus();
                }

                // 解码在播放器内部完成；会话已失效时它直接返回 0。
                // 起始段按跳转偏移跳过句首音频（歌词式 seek）。
                var segmentSkip = i == startIndex ? skipBytes : 0;

                if (i == startIndex && segmentSkip == 0 && skipCharOffset > 0)
                {
                    // 跳转时这句还没合成（或引擎不给时间戳）：现在按已知信息补算偏移。
                    // 有词表走词表，没有词表 WordSecondsAt 会按字数比例估算，
                    // 所以 Azure（REST 无时间戳）也能从点击的子句起读，而不是退化成句首。
                    var seconds = WordSecondsAt(plan[i], skipCharOffset);

                    if (seconds > 0)
                    {
                        var bps = BytesPerSecond();

                        segmentSkip = (int)Math.Round(seconds * bps);
                        segmentSkip -= segmentSkip % 2;

                        lock (_gate)
                        {
                            if (!IsPlayStale(play))
                            {
                                _sessionSkipBytes = segmentSkip;
                                _positionOffsetSeconds += segmentSkip / bps;
                            }
                        }
                    }
                }

                if (playerSession == 0)
                {
                    // 第一段音频到手才开设备：位置必须从真实音频的第一个字节算起。
                    // Begin 也在锁内：它会把输出的播放状态重新拉起来（并顺带 Stop 旧设备），
                    // 与跳转的 Stop 交错时会让新会话被关掉或者暂停被冲掉。
                    lock (_gate)
                    {
                        if (IsPlayStale(play))
                        {
                            return;
                        }

                        playerSession = _player.Begin();

                        // 用户可能在这几十毫秒的窗口里按了暂停：Begin 会把输出重新拉起来，这里补一次暂停
                        if (_status == ReadStatus.Paused)
                        {
                            _player.Pause();
                        }
                    }
                }

                var pcmBytes = await _player.WriteMp3Async(mp3, playerSession, token, segmentSkip);
                if (IsPlayStale(play))
                {
                    return;
                }

                lock (_gate)
                {
                    if (IsPlayStale(play))
                    {
                        return;
                    }

                    plan[i].PcmBytes = pcmBytes;
                }

                cursor += pcmBytes;
            }

            _player.MarkFeederDone(playerSession);
            lock (_gate)
            {
                if (!IsPlayStale(play))
                {
                    _feederDone = true;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || IsPlayStale(play))
        {
            // seek / 换文本 / 停止 / 失败：本次会话被主动取消，不是错误
        }
        catch (OperationCanceledException ex)
        {
            // 令牌没取消却收到取消：说明取消来自某个已失效的就绪闸（失败路径）。
            // 以前这里被一律吞掉，界面就永远停在「正在合成 X%」或「正在朗读 0:00」。
            if (!IsPlayStale(play))
            {
                Fail(play.Synth, ex);
            }
        }
        catch (Exception ex)
        {
            if (!IsPlayStale(play))
            {
                Fail(play.Synth, ex);
            }
        }
    }

    private void UpdateCharIndexLocked()
    {
        if (_mappingFrozen || _plan.Count == 0 || _text.Length == 0)
        {
            return;
        }

        var bps = BytesPerSecond();

        var position = _player.PositionSeconds;
        var posBytes = (long)(position * bps);

        SegmentRuntime? current = null;
        SegmentRuntime? lastWritten = null;
        var currentIndex = -1;

        for (var i = _sessionStart; i < _plan.Count; i++)
        {
            var item = _plan[i];

            // 没写入的段（还在合成/排队）不能中断扫描：后面的段可能已经写好了
            if (item.PcmBytes <= 0)
            {
                continue;
            }

            lastWritten = item;

            if (posBytes < item.StartPcmBytes + item.PcmBytes)
            {
                current = item;
                currentIndex = i;
                break;
            }
        }

        // 位置超过最后一段（下溢/静音）时，停在最后一段的末尾
        if (current is null)
        {
            current = lastWritten;
            currentIndex = lastWritten is null ? -1 : _plan.IndexOf(lastWritten);
        }

        if (current is null)
        {
            return;
        }

        var skipBytes = currentIndex == _sessionStart ? _sessionSkipBytes : 0;
        var localBytes = Math.Max(0, posBytes - current.StartPcmBytes + skipBytes);
        var localSeconds = localBytes / bps;

        if (current.Words.Count == 0)
        {
            var totalBytes = Math.Max(1, current.PcmBytes + skipBytes);
            var ratio = Math.Clamp(localBytes / (double)totalBytes, 0, 1);
            _charIndex = current.Segment.Start + (int)(current.Segment.Length * ratio);
        }
        else
        {
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

        // 跳转后的首段：词表按整词取起点，映射回来可能落在点击的子句之前。
        // 高亮不能早于用户点的那一句，否则点了 A 却亮 B。
        if (currentIndex == _sessionStart && _sessionStartChar > current.Segment.Start)
        {
            _charIndex = Math.Max(_charIndex, _sessionStartChar);
        }

        if (Diag && Environment.TickCount64 - _lastDiagAt >= 500)
        {
            _lastDiagAt = Environment.TickCount64;
            var debug = _player is StreamingAudioPlayer stream ? stream.PositionDebug : default;
            Log.Info(
                $"diag 高亮 | 位置={position:F3}s | 字节={posBytes} | " +
                $"段={currentIndex + 1}/{_plan.Count} | 段范围={current.StartPcmBytes}..{current.StartPcmBytes + current.PcmBytes} | " +
                $"字符={_charIndex}/{_text.Length} | 采样率={bps:F0} | " +
                $"设备={debug.Device} 缓冲={debug.Buffered} 写入={debug.Written} 暂停={debug.Paused} | " +
                $"锚点={debug.Anchor} 锚龄={debug.AnchorAge}ms 已报={debug.Reported} 会话={debug.Session}");
        }
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
                TransitionLocked(ReadStatus.Completed);
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

    private void Fail(SynthesisScope synth, Exception ex)
    {
        lock (_gate)
        {
            if (IsSynthStale(synth))
            {
                return;
            }

            UpdateCharIndexLocked();
            _positionFrozen = _player.PositionSeconds + _positionOffsetSeconds;
            _error = ex.Message;
            _feederDone = true;

            // 失败后合成线程必须停：否则界面已经显示"朗读失败"，
            // 后台还在一句句请求网络，白烧流量，也制造"点一下变成正在朗读却不出声"的变体。
            // 点击可以从这里恢复——JumpTo 会换一个新的合成批次重新开始。
            _synthHalted = true;
            synth.Cts.Cancel();
            EndPlayScopeLocked();

            // 让还卡在"等这一句合成"的播放循环立刻退出，不要一直挂着
            foreach (var item in _plan)
            {
                item.Ready.TrySetCanceled();
            }

            TransitionLocked(ReadStatus.Failed);

            // 停设备也在锁内：失败和"用户马上点击恢复"可能同时发生，
            // 锁外的 Stop 会把点击后新建的播放会话一起关掉（点了没声音）。
            _player.Stop();
        }

        Log.Error("朗读失败", ex);
        RaiseStatus();
    }

    /// <summary>按原始整句范围找段：重切后仍能点到该句的前半部分。</summary>
    private int FindSegmentByOriginalLocked(int charIndex)
    {
        for (var i = 0; i < _plan.Count; i++)
        {
            if (_plan[i].Original.End > charIndex)
            {
                return i;
            }
        }

        return _plan.Count - 1;
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

        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate)
        {
            EndPlayScopeLocked();
            NewSynthScopeLocked();
        }

        _player.Dispose();
    }
}
