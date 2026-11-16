using SelectionTts.Audio;
using SelectionTts.Tts;

namespace SelectionTts.Core;

/// <summary>
/// 故障注入自检（--selftest-fault）。
///
/// 真实设备上「没有声卡」「seek 与写入交错」这些路径很难稳定复现，
/// 这里用假播放器/假合成器把状态机逼到那些分支上，结果写日志供核对。
/// 不做断言抛异常：某个用例失败只记一行 FAIL，不影响其它用例。
/// </summary>
internal static class FaultSelfTest
{
    public static async Task RunAsync()
    {
        await PlayerBeginFailureAsync();
        await PlayerSessionIsolationAsync();
        await TextPreemptionAsync();
        await PrepWindowJumpAsync();
        await SingleSentenceFailureAsync();
        await TimeoutNotTreatedAsCancelAsync();
        await ConsecutiveFailureStopsAsync();
        await FailureRecoveryAsync();
        await RapidJumpAsync();
        TextBoundary();
        Log.Info("selftest fault done");
    }

    /// <summary>按 UTF-16 截断时不能把 emoji 的代理对切成半个字符。</summary>
    private static void TextBoundary()
    {
        var emoji = char.ConvertFromUtf32(0x1F600);
        var source = "a" + emoji + "b";

        var (cutEmoji, truncated) = TextUtil.Truncate(source, 2);
        var emojiOk = truncated && cutEmoji == "a" && !cutEmoji.Any(char.IsHighSurrogate);

        var (plain, _) = TextUtil.Truncate("abcdef", 3);
        var plainOk = plain == "abc";

        Log.Info(
            $"selftest fault 代理对截断 | 结果=[{cutEmoji}] 长度={cutEmoji.Length} | " +
            $"普通截断=[{plain}] | 通过={emojiOk && plainOk}");
    }

    /// <summary>P1-1：播放器初始化抛异常时必须进入 Failed，而不是永远停在 Preparing。</summary>
    private static async Task PlayerBeginFailureAsync()
    {
        var settings = new AppSettings();
        var player = new FakePlayer { ThrowOnBegin = true };

        using var reader = new ReaderService(settings, new FakeSynth(), player);
        reader.Speak("播放器初始化失败测试。");

        var state = await WaitForAsync(reader, s => s == ReadStatus.Failed, 3000);
        var ok = state is { Status: ReadStatus.Failed } &&
                 state.Error?.Contains("故障注入") == true;

        Log.Info(
            $"selftest fault 播放器初始化失败 | 状态={state?.Status} | 提示=[{state?.Error}] | 通过={ok}");
    }

    /// <summary>P1-2：seek/重启会话后，旧会话的写入与「喂完」标记都不能影响新会话。</summary>
    private static async Task PlayerSessionIsolationAsync()
    {
        try
        {
            using var player = new StreamingAudioPlayer();

            var first = player.Begin();
            await player.WritePcmAsync(new byte[4800], first, CancellationToken.None);

            var second = player.Begin();

            // 旧会话迟到的写入
            await player.WritePcmAsync(new byte[4800], first, CancellationToken.None);

            // 旧会话迟到的「喂完」
            player.MarkFeederDone(first);

            var ok = player.PositionSeconds < 0.001 && !player.IsDrained;
            Log.Info(
                $"selftest fault 会话隔离 | 会话={first}→{second} | 位置={player.PositionSeconds:F3}s | " +
                $"IsDrained={player.IsDrained} | 通过={ok}");
        }
        catch (Exception ex)
        {
            Log.Info($"selftest fault 会话隔离 跳过（无可用播放设备）：{ex.Message}");
        }
    }

    /// <summary>换文本必须让旧文本的合成立刻失效，新文本能正常读完。</summary>
    private static async Task TextPreemptionAsync()
    {
        var settings = new AppSettings();
        var synth = new FakeSynth
        {
            Gate = async (text, token) =>
            {
                if (text.StartsWith("旧", StringComparison.Ordinal))
                {
                    await Task.Delay(5000, token).ConfigureAwait(false);
                }
            },
        };

        using var reader = new ReaderService(settings, synth, new FakePlayer());
        reader.Speak("旧文本正在慢慢合成。");
        await Task.Delay(300);

        reader.Speak("新文本内容。");
        var state = await WaitForAsync(reader, s => s == ReadStatus.Completed, 3000);

        var ok = state is { Status: ReadStatus.Completed, Text: "新文本内容。" };
        Log.Info(
            $"selftest fault 文本抢占 | 状态={state?.Status} | 文本=[{state?.Text}] | 通过={ok}");
    }

    /// <summary>单句连续失败两次后跳过，后面正常的句子要继续读完。</summary>
    private static async Task SingleSentenceFailureAsync()
    {
        var settings = new AppSettings();
        var synth = new FakeSynth
        {
            Gate = (text, _) => text.Contains("坏", StringComparison.Ordinal)
                ? Task.FromException(new InvalidOperationException("故障注入：这一句必然失败"))
                : Task.CompletedTask,
        };

        using var reader = new ReaderService(settings, synth, new FakePlayer());
        reader.Speak("好的一句。坏的一句。好的一句。");

        var state = await WaitForAsync(reader, s => s == ReadStatus.Completed, 6000);
        var ok = state is { Status: ReadStatus.Completed };
        Log.Info(
            $"selftest fault 单句失败跳过 | 状态={state?.Status} | 通过={ok}");
    }

    /// <summary>
    /// 连接超时/底层超时抛的是 TaskCanceledException，它不是"用户取消"。
    /// 以前这里把它当成取消，整篇合成直接停掉：进度条永远停在最后一个百分比，
    /// 后面的句子再也不合成。正确行为是重试、仍失败就跳过这一句继续往下。
    /// </summary>
    private static async Task TimeoutNotTreatedAsCancelAsync()
    {
        var settings = new AppSettings();
        var synth = new FakeSynth
        {
            Gate = (text, _) => text.Contains("超时", StringComparison.Ordinal)
                ? Task.FromException(new TaskCanceledException("A task was canceled."))
                : Task.CompletedTask,
        };

        using var reader = new ReaderService(settings, synth, new FakePlayer());
        reader.Speak("好的一句。超时的一句。好的一句。");

        var state = await WaitForAsync(reader, s => s == ReadStatus.Completed, 8000);
        var ok = state is { Status: ReadStatus.Completed } && state.SynthesisDone;
        Log.Info(
            $"selftest fault 超时不算取消 | 状态={state?.Status} | 合成完整={state?.SynthesisDone} | 通过={ok}");
    }

    /// <summary>
    /// 连续 2 句失败要停下来报错，而不是让进度条一点点挪十几分钟。
    /// 提示必须能让用户动手处理（检查网络），而不是只写"合成失败"。
    /// </summary>
    private static async Task ConsecutiveFailureStopsAsync()
    {
        var settings = new AppSettings();
        var synth = new FakeSynth
        {
            Gate = (_, _) => Task.FromException(new InvalidOperationException("故障注入：一直失败")),
        };

        using var reader = new ReaderService(settings, synth, new FakePlayer());
        reader.Speak("第一句。第二句。第三句。第四句。第五句。");

        var state = await WaitForAsync(
            reader, s => s is ReadStatus.Failed or ReadStatus.Completed, 15000);
        var ok = state is { Status: ReadStatus.Failed } &&
                 state.Error?.Contains("请检查网络") == true;
        Log.Info(
            $"selftest fault 连续失败停止 | 状态={state?.Status} | 提示=[{state?.Error}] | 通过={ok}");
    }

    /// <summary>
    /// 准备期点击不能丢：文本刚换好、第一句还在合成时点第 3 句，
    /// 必须从第 3 句开始读，而不是因为"句子表还没填"被直接忽略。
    /// </summary>
    private static async Task PrepWindowJumpAsync()
    {
        var settings = new AppSettings();
        var synth = new FakeSynth
        {
            // 首句合成慢一些，保证点击发生在"还没开始播"的窗口里
            Gate = (_, token) => Task.Delay(800, token),
        };

        var player = new FakePlayer();

        using var reader = new ReaderService(settings, synth, player);
        reader.Speak("第一句。第二句。第三句。");

        // 立刻点第三句（第 8 个字）
        reader.JumpTo(8);

        var state = await WaitForAsync(reader, s => s == ReadStatus.Completed, 8000);

        // 起点真的落在第 3 句：只写了 1 段音频；点击被丢掉的话会从第 1 句写 3 段
        var writes = player.WriteCounts(player.CurrentSession);
        var ok = state is { Status: ReadStatus.Completed } && writes == 1;

        Log.Info(
            $"selftest fault 准备期点击 | 状态={state?.Status} | 写入段数={writes} " +
            $"Begin={player.BeginCount} | 通过={ok}");
    }

    /// <summary>
    /// 熔断失败后点击必须能恢复：合成线程停下、点击重新拉起合成并读完整篇，
    /// 而不是界面显示「正在朗读」却永远 0:00。
    /// </summary>
    private static async Task FailureRecoveryAsync()
    {
        var settings = new AppSettings();
        var failing = true;

        var synth = new FakeSynth
        {
            Gate = (_, _) => failing
                ? Task.FromException(new InvalidOperationException("故障注入：暂时全部失败"))
                : Task.CompletedTask,
        };

        using var reader = new ReaderService(settings, synth, new FakePlayer());
        reader.Speak("第一句。第二句。第三句。第四句。第五句。");

        var failedState = await WaitForAsync(reader, s => s == ReadStatus.Failed, 15000);
        var failedOk = failedState is { Status: ReadStatus.Failed };

        // 网络恢复，用户点第一句：应当重新开始合成并读完
        failing = false;
        reader.JumpTo(0);

        var recovered = await WaitForAsync(
            reader, s => s is ReadStatus.Completed or ReadStatus.Failed, 8000);
        var ok = failedOk &&
                 recovered is { Status: ReadStatus.Completed, SynthesisDone: true, Error: null } &&
                 synth.Successes > 0;

        Log.Info(
            $"selftest fault 失败后点击恢复 | 首次={failedState?.Status} | 恢复后={recovered?.Status} | " +
            $"成功合成={synth.Successes} 句 | 提示=[{recovered?.Error}] | 通过={ok}");
    }

    /// <summary>
    /// 连续快速点击之后，最后一次点击必须生效并把整篇读完：
    /// 旧播放会话不能把新会话的设备关掉（真机表现是「点了没声音」）。
    /// </summary>
    private static async Task RapidJumpAsync()
    {
        var settings = new AppSettings();
        var player = new FakePlayer();

        using var reader = new ReaderService(settings, new FakeSynth(), player);
        reader.Speak("第一句。第二句。第三句。第四句。第五句。");
        await WaitForAsync(reader, s => s is ReadStatus.Playing or ReadStatus.Completed, 3000);

        // 连点四次（子句首字位置），模拟用户快速换段落
        foreach (var index in new[] { 4, 8, 12, 16 })
        {
            reader.JumpTo(index);
        }

        // 让连点产生的旧任务退出，再点回第一句：最后一次点击必须赢
        await Task.Delay(400);
        reader.JumpTo(0);

        var state = await WaitForAsync(reader, s => s == ReadStatus.Completed, 8000);
        var writes = player.WriteCounts(player.CurrentSession);
        var ok = state is { Status: ReadStatus.Completed } && writes > 0;

        Log.Info(
            $"selftest fault 连点跳转 | 状态={state?.Status} | Begin={player.BeginCount} " +
            $"Stop={player.StopCount} | 当前会话写入={writes} 段 | 通过={ok}");
    }

    private static async Task<ReadState?> WaitForAsync(
        ReaderService reader, Func<ReadStatus, bool> predicate, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            var state = reader.GetState();
            if (predicate(state.Status))
            {
                return state;
            }

            await Task.Delay(50);
        }

        return null;
    }

    private sealed class FakeSynth : ISpeechSynthesizer
    {
        /// <summary>返回前先过这道闸；可以让某句永久挂起或必失败。</summary>
        public Func<string, CancellationToken, Task>? Gate { get; set; }

        private int _calls;
        private int _successes;

        /// <summary>进来的合成请求总数（含重试）。</summary>
        public int Calls => Volatile.Read(ref _calls);

        /// <summary>真正产出音频的请求数（失败重试不计）。</summary>
        public int Successes => Volatile.Read(ref _successes);

        public string Name => "Fake";

        public async Task<SynthesisSummary> StreamAsync(
            string text,
            string voice,
            string rate,
            Action<ReadOnlyMemory<byte>> onAudio,
            Action<SpeechBoundary>? onBoundary,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);

            if (Gate is not null)
            {
                await Gate(text, cancellationToken).ConfigureAwait(false);
            }

            _ = onBoundary;
            onAudio(new ReadOnlyMemory<byte>(new byte[480]));
            Interlocked.Increment(ref _successes);
            return new SynthesisSummary(480, 0, 0, 1);
        }
    }

    private sealed class FakePlayer : IAudioPlayer
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, int> _writes = [];
        private int _current;
        private int _begins;
        private int _stops;

        public bool ThrowOnBegin { get; set; }

        public bool FeederDone { get; private set; }

        public int CurrentSession
        {
            get
            {
                lock (_gate)
                {
                    return _current;
                }
            }
        }

        public int BeginCount
        {
            get
            {
                lock (_gate)
                {
                    return _begins;
                }
            }
        }

        public int StopCount
        {
            get
            {
                lock (_gate)
                {
                    return _stops;
                }
            }
        }

        /// <summary>某个会话收到过多少次写入（旧会话的写入不计入当前会话）。</summary>
        public int WriteCounts(int sessionId)
        {
            lock (_gate)
            {
                return _writes.TryGetValue(sessionId, out var count) ? count : 0;
            }
        }

        public int Begin()
        {
            if (ThrowOnBegin)
            {
                throw new InvalidOperationException("无可用音频设备（故障注入）");
            }

            lock (_gate)
            {
                _current++;
                _begins++;
                _writes[_current] = 0;
                FeederDone = false;
                return _current;
            }
        }

        public Task<int> WriteMp3Async(
            byte[] mp3, int sessionId, CancellationToken cancellationToken, int skipPcmBytes = 0)
        {
            lock (_gate)
            {
                if (sessionId == _current)
                {
                    _writes[_current] = _writes.TryGetValue(_current, out var count) ? count + 1 : 1;
                }
            }

            return Task.FromResult(Math.Max(0, mp3.Length - Math.Min(skipPcmBytes, mp3.Length)));
        }

        public void MarkFeederDone(int sessionId)
        {
            if (sessionId == _current)
            {
                FeederDone = true;
            }
        }

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void Stop()
        {
            lock (_gate)
            {
                _stops++;
            }

            FeederDone = true;
        }

        public double BytesPerSecond => StreamingAudioPlayer.DefaultBytesPerSecond;

        public double PositionSeconds => 0;

        public bool IsDrained => FeederDone;

        public void Dispose()
        {
        }
    }
}
