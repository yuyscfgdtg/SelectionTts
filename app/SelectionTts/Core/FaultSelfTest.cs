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
        await SingleSentenceFailureAsync();
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

        public string Name => "Fake";

        public async Task<SynthesisSummary> StreamAsync(
            string text,
            string voice,
            string rate,
            Action<ReadOnlyMemory<byte>> onAudio,
            Action<SpeechBoundary>? onBoundary,
            CancellationToken cancellationToken)
        {
            if (Gate is not null)
            {
                await Gate(text, cancellationToken).ConfigureAwait(false);
            }

            _ = onBoundary;
            onAudio(new ReadOnlyMemory<byte>(new byte[480]));
            return new SynthesisSummary(480, 0, 0, 1);
        }
    }

    private sealed class FakePlayer : IAudioPlayer
    {
        private readonly Dictionary<int, long> _written = [];
        private int _current;

        public bool ThrowOnBegin { get; set; }

        public bool FeederDone { get; private set; }

        public int Begin()
        {
            if (ThrowOnBegin)
            {
                throw new InvalidOperationException("无可用音频设备（故障注入）");
            }

            _current++;
            _written[_current] = 0;
            FeederDone = false;
            return _current;
        }

        public Task<int> WriteMp3Async(byte[] mp3, int sessionId, CancellationToken cancellationToken)
        {
            if (sessionId == _current)
            {
                _written[_current] += mp3.Length;
            }

            return Task.FromResult(mp3.Length);
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

        public void Stop() => FeederDone = true;

        public double PositionSeconds => 0;

        public bool IsDrained => FeederDone;

        public bool IsPlaying => true;

        public void Dispose()
        {
        }
    }
}
