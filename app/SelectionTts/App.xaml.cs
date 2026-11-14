using System.Threading;
using System.Windows;
using SelectionTts.Core;
using SelectionTts.Platform;
using SelectionTts.Tray;

namespace SelectionTts;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private TrayIconManager? _tray;
    private HotkeyManager? _hotkeys;
    private ReaderService? _reader;
    private ClipboardWatcher? _watcher;
    private Ui.ReaderBoardWindow? _board;
    private AppSettings? _settings;
    private bool _watching;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.Initialize();
        Log.Initialize(AppPaths.DataDir);

        if (e.Args.Contains("--selftest-drm"))
        {
            foreach (var timestamp in new long[] { 1700000000, 1760000000 })
            {
                Log.Info($"drm {timestamp} {Tts.DrmToken.Generate(timestamp)}");
            }

            Shutdown(0);
            return;
        }

        if (e.Args.Contains("--selftest-tts"))
        {
            RunTtsSelfTest();
            return;
        }

        if (e.Args.Contains("--selftest-stream"))
        {
            RunStreamSelfTest();
            return;
        }

        if (e.Args.Contains("--selftest-pipeline"))
        {
            RunPipelineSelfTest();
            return;
        }

        _instanceMutex = new Mutex(true, @"Local\SelectionTts.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Log.Info("已有实例在运行，本次启动退出");
            Shutdown();
            return;
        }

        Log.Info($"启动 SelectionTts {typeof(App).Assembly.GetName().Version} | 数据目录 {AppPaths.DataDir}");

        _settings = SettingsStore.Load();
        _reader = new ReaderService(_settings);
        _tray = new TrayIconManager(_settings);
        _hotkeys = new HotkeyManager();
        _watcher = new ClipboardWatcher();
        _board = new Ui.ReaderBoardWindow(_settings, _reader);

        _tray.ToggleWatchRequested += () => SetWatching(!_watching, persist: true);
        _tray.StopRequested += () => _reader.Stop();
        _tray.ExitRequested += Shutdown;
        _tray.ToggleBoardRequested += () =>
        {
            _board.ToggleBoard();
            _tray.SetBoardVisible(_board.IsBoardVisible);
        };

        _hotkeys.ToggleWatchRequested += () => Dispatcher.Invoke(() => SetWatching(!_watching, persist: true));

        _reader.StatusChanged += status => Dispatcher.Invoke(() =>
        {
            var state = _reader.GetState();
            Log.Info($"status: {ReaderService.Describe(status)}");
            _tray.SetStatus(ReaderService.Describe(status));

            if (status == ReadStatus.Failed)
            {
                _tray.Notify(state.Error is null ? "朗读失败" : $"朗读失败：{state.Error}");
            }
        });

        // 复制即读：新内容直接顶掉正在朗读的旧内容
        _watcher.TextCopied += text => _reader.Speak(text);

        if (!_hotkeys.RegisterDefaults())
        {
            _tray.Notify("快捷键注册失败，可能已被其他程序占用");
        }

        if (_settings.BoardVisible)
        {
            _board.ShowBoard();
        }

        var startWatching = _settings.WatchEnabled || e.Args.Contains("--watch");
        SetWatching(startWatching, persist: false);

        Log.Info("初始化完成");
    }

    private void SetWatching(bool on, bool persist)
    {
        if (_watcher is null || _tray is null)
        {
            return;
        }

        _watching = on;
        _watcher.SetEnabled(on);
        _tray.SetEnabled(on);
        _board?.SetEnabled(on);

        if (!on)
        {
            // 总开关关闭：停止并释放文本，内存里不留副本
            _reader?.Clear();
        }

        if (persist && _settings is not null)
        {
            _settings.WatchEnabled = on;
            SettingsStore.Save(_settings);
            _tray.Notify(on ? "已启用朗读：复制文本即朗读" : "已关闭朗读");
        }
    }

    /// <summary>
    /// 验证流式合成：首包延迟、词时间戳条数、时间戳与音频时长是否吻合。
    /// 结果写日志，控制台不输出（WinExe 没有控制台）。
    /// </summary>
    private async void RunStreamSelfTest()
    {
        try
        {
            var settings = SettingsStore.Load();
            var client = new Tts.EdgeTtsClient();
            var watch = System.Diagnostics.Stopwatch.StartNew();

            var text =
                "这是一段用于验证流式合成的测试文本。它的长度大约三百字，包含多个句子，" +
                "用来确认服务端会为每一句话返回时间戳。第一句用来测首包延迟。" +
                "第二句稍微长一点，中间有一个逗号，看看词边界的切分是否合理，" +
                "以及 offset 是否随音频推进而递增。第三句继续，" +
                "把文本撑到足够长，这样进度条才有东西可以显示。" +
                "第四句用来确认时间戳不会在中途断掉。" +
                "最后一句收尾，用来核对最后一个时间戳和音频总时长是否吻合。";

            long firstAudioAt = -1;
            long firstBoundaryAt = -1;
            long audioBytes = 0;
            var audioFrames = 0;
            Tts.SpeechBoundary first = default;
            Tts.SpeechBoundary last = default;

            var summary = await client.StreamAsync(
                text,
                settings.Voice,
                settings.Rate,
                buffer =>
                {
                    Interlocked.CompareExchange(ref firstAudioAt, watch.ElapsedMilliseconds, -1);
                    Interlocked.Add(ref audioBytes, buffer.Length);
                    Interlocked.Increment(ref audioFrames);
                },
                boundary =>
                {
                    Interlocked.CompareExchange(ref firstBoundaryAt, watch.ElapsedMilliseconds, -1);
                    if (first.Text is null)
                    {
                        first = boundary;
                    }

                    last = boundary;
                },
                CancellationToken.None);

            Log.Info(
                $"selftest stream | 字数={text.Length} | 首包={firstAudioAt}ms | " +
                $"首时间戳={firstBoundaryAt}ms | 音频块={audioFrames} | 字节={audioBytes} | " +
                $"估算时长={summary.DurationSeconds:F2}s | 时间戳={summary.BoundaryCount} | " +
                $"耗时={summary.ElapsedMs}ms | 尝试={summary.Attempts}");
            Log.Info(
                $"selftest stream 首个边界 offset={first.OffsetSeconds:F3}s text=[{first.Text}] | " +
                $"末个边界 offset={last.OffsetSeconds:F3}s text=[{last.Text}]");
            Log.Info($"selftest stream done | 时间戳>0={summary.BoundaryCount > 0}");
        }
        catch (Exception ex)
        {
            Log.Error("selftest stream failed", ex);
        }
        finally
        {
            Shutdown(0);
        }
    }

    /// <summary>
    /// 验证流水线：首声延迟、分块推进、抢占、停止。
    /// </summary>
    private async void RunPipelineSelfTest()
    {
        var settings = SettingsStore.Load();
        var reader = new ReaderService(settings);
        var started = Environment.TickCount64;
        var longText = string.Concat(Enumerable.Repeat(
            "这是一段用来验证分块流水线的长文本，它应该在第一秒左右就开始出声，而不是等全部合成完再播。", 20));

        Log.Info($"selftest pipeline | 字数={longText.Length}");

        reader.StatusChanged += status => Log.Info(
            $"selftest pipeline status={status} | 距开始={Environment.TickCount64 - started}ms");

        reader.Speak(longText);

        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(250);
            var state = reader.GetState();
            if (i % 2 == 0)
            {
                Log.Info(
                    $"selftest 进度={state.Progress:P0} | 位置={state.PositionSeconds:F1}s | " +
                    $"总长={state.DurationSeconds:F0}s | 剩余={state.RemainingSeconds:F0}s | 状态={state.Status}");
            }
        }

        Log.Info("selftest 抢占：换成一句短的");
        var preemptAt = Environment.TickCount64;
        reader.Speak("抢占测试：新内容应该立刻顶掉上一段长文本。");

        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            var state = reader.GetState();
            if (i % 4 == 0)
            {
                Log.Info($"selftest 抢占后 位置={state.PositionSeconds:F1}s | 状态={state.Status} | 进度={state.Progress:P0}");
            }

            if (state.Status == ReadStatus.Completed)
            {
                Log.Info($"selftest 抢占后完成 | 用时={Environment.TickCount64 - preemptAt}ms");
                break;
            }
        }

        Log.Info("selftest 停止：朗读中停止，文本应保留");
        reader.Speak(string.Concat(Enumerable.Repeat("这一段用来测试停止响应速度。", 20)));
        await Task.Delay(1500);
        var stopAt = Environment.TickCount64;
        reader.Stop();
        var stopElapsed = Environment.TickCount64 - stopAt;
        var stopped = reader.GetState();
        await Task.Delay(500);
        var after = reader.GetState();
        Log.Info(
            $"selftest stop | 耗时={stopElapsed}ms | 状态={stopped.Status} | " +
            $"停止后位置={after.PositionSeconds:F1}s | 文本保留={after.CharCount > 0}");

        reader.Dispose();
        Log.Info("selftest pipeline done");
        Shutdown(0);
    }

    private async void RunTtsSelfTest()
    {
        try
        {
            var settings = SettingsStore.Load();
            var tts = new Tts.EdgeTtsClient();
            var audio = await tts.SynthesizeAsync(
                "这是一段用于验证 C 井版朗读引擎的测试文本。",
                settings.Voice,
                settings.Rate,
                CancellationToken.None);

            var path = Path.Combine(AppPaths.CacheDir, "selftest.mp3");
            File.WriteAllBytes(path, audio.Audio);
            Log.Info($"selftest tts ok | {audio.Audio.Length}B | 合成={audio.ElapsedMs}ms | 尝试={audio.Attempts}");

            var player = new Audio.StreamingAudioPlayer();
            var generation = player.Begin();
            await player.WritePcmAsync(
                Audio.StreamingAudioPlayer.DecodeMp3(audio.Audio), generation, CancellationToken.None);
            player.MarkFeederDone();

            var waited = 0;
            while (!player.IsDrained && waited < 60000)
            {
                await Task.Delay(100);
                waited += 100;
            }

            Log.Info($"selftest play done | 播放={player.PositionSeconds:F2}s | 等待={waited}ms");
            player.Dispose();
            Shutdown(0);
        }
        catch (Exception ex)
        {
            Log.Error("selftest tts failed", ex);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watcher?.Dispose();
        _hotkeys?.Dispose();
        _reader?.Dispose();
        _board?.Close();
        _tray?.Dispose();
        Log.Info("退出");
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
