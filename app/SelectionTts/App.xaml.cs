using System.Threading;
using System.Windows;
using SelectionTts.Core;
using SelectionTts.Platform;
using SelectionTts.Tray;
using SelectionTts.Tts;

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
    private bool _ownsInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.Initialize();
        Log.Initialize(AppPaths.DataDir);

        // 任何自检都不许碰用户的真实配置：配置读写全部指到临时文件（日志仍然写 data/logs）
        if (e.Args.Any(arg => arg.StartsWith("--selftest", StringComparison.Ordinal)))
        {
            AppPaths.UseTemporaryConfig();
        }

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

        if (e.Args.Contains("--selftest-config"))
        {
            RunConfigSelfTest();
            return;
        }

        if (e.Args.Contains("--selftest-autostart"))
        {
            RunAutoStartSelfTest();
            return;
        }

        if (e.Args.Contains("--selftest-fault"))
        {
            RunFaultSelfTest();
            return;
        }

        if (e.Args.Contains("--selftest-clause"))
        {
            RunClauseSelfTest();
            return;
        }

        if (e.Args.Contains("--diag-highlight"))
        {
            ReaderService.Diag = true;
        }

        _instanceMutex = new Mutex(true, @"Local\SelectionTts.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Log.Info("已有实例在运行，本次启动退出");
            Shutdown();
            return;
        }

        _ownsInstanceMutex = true;

        Log.Info($"启动 SelectionTts {typeof(App).Assembly.GetName().Version} | 数据目录 {AppPaths.DataDir}");

        _settings = SettingsStore.Load();
        _reader = new ReaderService(_settings, CreateEngine());
        _tray = new TrayIconManager(_settings);
        _hotkeys = new HotkeyManager();
        _watcher = new ClipboardWatcher();
        _board = new Ui.ReaderBoardWindow(_settings, _reader);

        _tray.ToggleWatchRequested += () => SetWatching(!_watching, persist: true);
        _tray.ExitRequested += Shutdown;
        _tray.ToggleBoardRequested += () =>
        {
            _board.ToggleBoard();
            _tray.SetBoardVisible(_board.IsBoardVisible);
        };
        _tray.SettingsRequested += OpenSettings;

        // 板子上的"停止/启动"就是总开关，和热键同一个入口
        _board.ToggleMaster += () => SetWatching(!_watching, persist: true);

        // 板子上的"暂停/继续"和暂停热键也共用一个入口
        _board.TogglePauseRequested += TogglePause;

        _hotkeys.Pressed += actionId => Dispatcher.Invoke(() =>
        {
            switch (actionId)
            {
                case "toggle-watch":
                    SetWatching(!_watching, persist: true);
                    break;
                case "toggle-pause":
                    TogglePause();
                    break;
                default:
                    Log.Warn($"未处理的热键动作 {actionId}");
                    break;
            }
        });

        _reader.StatusChanged += status => Dispatcher.Invoke(() =>
        {
            var state = _reader.GetState();
            _tray.SetStatus(ReaderService.Describe(status));

            if (status == ReadStatus.Failed)
            {
                _tray.Notify(state.Error is null ? "朗读失败" : $"朗读失败：{state.Error}");
            }
        });

        // 复制即读：新内容直接顶掉正在朗读的旧内容
        _watcher.TextCopied += text => _reader.Speak(text);

        ApplyHotkeys();

        if (_settings.BoardVisible)
        {
            _board.ShowBoard();
        }

        var startWatching = _settings.WatchEnabled || e.Args.Contains("--watch");
        SetWatching(startWatching, persist: false);

        if (e.Args.Contains("--settings"))
        {
            Dispatcher.BeginInvoke(new Action(OpenSettings));
        }

        Log.Info("初始化完成");
    }

    private ISpeechSynthesizer CreateEngine()
    {
        if (_settings is not null &&
            string.Equals(_settings.Engine, "azure", StringComparison.OrdinalIgnoreCase))
        {
            var key = Dpapi.Unprotect(_settings.AzureKeyProtected);
            var client = new Tts.AzureTtsClient(_settings.AzureRegion, key);
            if (client.IsConfigured)
            {
                Log.Info($"引擎：Azure（{_settings.AzureRegion}）");
                return client;
            }

            Log.Warn("配置为 Azure 但缺少区域或 Key，回退到 Edge");
        }

        return new Tts.EdgeTtsClient();
    }

    private void ApplyHotkeys()
    {
        if (_settings is null || _hotkeys is null || _tray is null)
        {
            return;
        }

        var desired = new List<(string ActionId, HotkeyGesture Gesture)>();

        foreach (var action in HotkeyActions.All)
        {
            var text = _settings.Hotkeys.GetValueOrDefault(action.Id);
            if (!HotkeyGesture.TryParse(text, out var gesture) &&
                !HotkeyGesture.TryParse(action.DefaultGesture, out gesture))
            {
                // 配置里是空的（出厂不绑定）是正常情况，只有写坏了才值得警告
                if (!string.IsNullOrWhiteSpace(text))
                {
                    Log.Warn($"热键 {action.Id} 的配置无效，已跳过：{text}");
                }

                continue;
            }

            desired.Add((action.Id, gesture));
        }

        var ok = _hotkeys.TryApply(desired, out var error);

        // 无论成败都刷新显示：成功的那几项可能已经换掉了
        RefreshHotkeyTexts();

        if (ok)
        {
            return;
        }

        Log.Warn($"热键注册失败：{error}");
        _tray.Notify(error);
    }

    /// <summary>设置窗口保存路径：整批生效，失败由 HotkeyManager 回滚到原绑定。</summary>
    private (bool Ok, string Error) ApplyHotkeySet(
        IReadOnlyList<(string ActionId, HotkeyGesture Gesture)> desired)
    {
        if (_hotkeys is null)
        {
            return (false, "热键组件不可用");
        }

        var ok = _hotkeys.TryApplyExclusive(desired, out var error);
        RefreshHotkeyTexts();
        return (ok, error);
    }

    private void RefreshHotkeyTexts()
    {
        var watchText = _hotkeys?.Describe(HotkeyActions.ToggleWatch.Id) ?? "";
        _tray?.SetHotkeyText(watchText);
        _board?.SetHotkeyText(watchText);
    }

    /// <summary>暂停 / 继续。板子按钮和热键共用；空闲或读完时按了不做任何事。</summary>
    private void TogglePause()
    {
        if (_reader is null)
        {
            return;
        }

        var status = _reader.GetState().Status;

        if (status == ReadStatus.Paused)
        {
            _reader.Resume();
        }
        else if (status is ReadStatus.Playing or ReadStatus.Preparing)
        {
            _reader.Pause();
        }
        else
        {
            Log.Info($"暂停/继续：当前状态 {status}，忽略");
        }
    }

    private void OpenSettings()
    {
        if (_settings is null || _reader is null || _tray is null || _hotkeys is null)
        {
            return;
        }

        var window = new Ui.SettingsWindow(_settings)
        {
            SuspendHotkeys = () => _hotkeys.UnregisterAll(),
            ResumeHotkeys = ApplyHotkeys,
            ApplyHotkeys = ApplyHotkeySet,
            Cleanup = () => Maintenance.Clean(),
        };

        window.ShowDialog();

        if (!window.Saved)
        {
            return;
        }

        // 设置里点了「恢复默认尺寸」并保存：让板子立刻回到默认大小
        _board?.ApplySavedSize();

        _reader.SetEngine(CreateEngine());
        _tray.Notify("设置已保存");
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
            _ = SettingsStore.Save(_settings);

            // 板子看得见的时候它已经显示状态了，再弹气泡只会挡住板子
            if (_board is null || !_board.IsBoardVisible)
            {
                _tray.Notify(on ? "已启用朗读：复制文本即朗读" : "已关闭朗读");
            }
        }
    }

    private static HotkeyGesture CtrlAlt(uint virtualKey) =>
        new(HotkeyGesture.ModControl | HotkeyGesture.ModAlt, virtualKey);

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

        // 真机 seek：朗读中连续跳转 5 次，最后一次必须生效（旧会话不能把新会话的声音关掉）
        Log.Info("selftest 跳转：朗读中连续跳转，最后一次必须生效");
        var jumpText = string.Concat(Enumerable.Repeat("这一段用来测试连续跳转之后的播放。", 8));
        reader.Speak(jumpText);
        await Task.Delay(1200);

        for (var i = 0; i < 5; i++)
        {
            reader.JumpTo(jumpText.Length * (i + 1) / 6);
            await Task.Delay(80);
        }

        await Task.Delay(900);
        var afterJump = reader.GetState();

        // 真机暂停：位置必须冻结，继续后必须接着往前走
        Log.Info("selftest 暂停：位置应冻结");
        reader.Pause();
        await Task.Delay(200);
        var pausedA = reader.GetState();
        await Task.Delay(900);
        var pausedB = reader.GetState();
        var frozen = Math.Abs(pausedB.PositionSeconds - pausedA.PositionSeconds) < 0.05;

        reader.Resume();
        await Task.Delay(900);
        var resumed = reader.GetState();
        var advanced = resumed.PositionSeconds > pausedB.PositionSeconds + 0.2;

        Log.Info(
            $"selftest seek/pause | 跳转后状态={afterJump.Status} 位置={afterJump.PositionSeconds:F1}s | " +
            $"暂停={pausedA.Status} 位置冻结={frozen}（{pausedA.PositionSeconds:F1}s→{pausedB.PositionSeconds:F1}s）| " +
            $"继续={resumed.Status} 位置推进={advanced}（→{resumed.PositionSeconds:F1}s）");

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

    /// <summary>验证开机自启：开→关→复原，快捷方式随状态出现和消失。</summary>
    private void RunAutoStartSelfTest()
    {
        try
        {
            var initial = AutoStart.IsEnabled;
            Log.Info($"selftest autostart | 初始={initial} | 路径={AutoStart.ShortcutPath}");

            AutoStart.Apply(true);
            Log.Info($"selftest autostart 开启后 | 状态={AutoStart.IsEnabled} | 文件存在={File.Exists(AutoStart.ShortcutPath)}");

            AutoStart.Apply(false);
            Log.Info($"selftest autostart 关闭后 | 状态={AutoStart.IsEnabled} | 文件存在={File.Exists(AutoStart.ShortcutPath)}");

            AutoStart.Apply(initial);
            Log.Info($"selftest autostart 复原后 | 状态={AutoStart.IsEnabled}（初始为 {initial}）");
        }
        catch (Exception ex)
        {
            Log.Error("selftest autostart failed", ex);
        }
        finally
        {
            Shutdown(0);
        }
    }

    /// <summary>--selftest-clause：断句规则回归（不联网、不开界面）。</summary>
    private void RunClauseSelfTest()
    {
        var cases = new (string Text, string Expected)[]
        {
            ("他说：“你好。”然后走了。", "他说：|“你好。”|然后走了。"),
            ("版本 1.0.3，时间 12:30。", "版本 1.0.3，|时间 12:30。"),
            ("等一下……然后他说——好吧。", "等一下……|然后他说——|好吧。"),
            ("第一行没有标点\n第二行。", "第一行没有标点|第二行。"),
            ("他说“你好”就走了。", "他说“你好”就走了。"),
            ("，。", ""),
        };

        var passed = 0;
        foreach (var (text, expected) in cases)
        {
            var clauses = ClauseMap.Split(text);
            var parts = new List<string>();
            foreach (var clause in clauses)
            {
                parts.Add(text.Substring(clause.Start, clause.Length));
            }

            var actual = string.Join("|", parts);
            var ok = actual == expected;
            if (ok)
            {
                passed++;
            }

            Log.Info($"selftest clause | 通过={ok} | 期望=[{expected}] | 实际=[{actual}]");
        }

        var sample = "第一句。第二句，第三句；第四句！";
        var list = ClauseMap.Split(sample);
        var monotonic = true;
        var covered = 0;
        var cursor = 0;

        foreach (var clause in list)
        {
            if (clause.Start < cursor || clause.Length <= 0)
            {
                monotonic = false;
            }

            cursor = clause.End;
            covered += clause.Length;
        }

        var dense = covered == sample.Length;
        var indexOk = list.Count >= 2 &&
                      ClauseMap.IndexOf(list, 0) == 0 &&
                      ClauseMap.IndexOf(list, list[1].Start) == 1 &&
                      ClauseMap.IndexOf(list, list[1].Start - 1) == 0;

        Log.Info(
            $"selftest clause 结构 | 共{list.Count}句 | 单调不重叠={monotonic} | 覆盖全文={dense} | 定位={indexOk}");
        Log.Info($"selftest clause 汇总 | {passed}/{cases.Length} 通过");

        Shutdown(0);
    }

    /// <summary>
    /// 故障注入自检：用假播放器 / 假合成器覆盖真实设备上难以稳定复现的路径。
    /// 结果写日志；任何一项失败只记录，不抛出。
    /// </summary>
    private async void RunFaultSelfTest()
    {
        try
        {
            await Core.FaultSelfTest.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Error("selftest fault failed", ex);
        }
        finally
        {
            Shutdown(0);
        }
    }

    private async void RunConfigSelfTest()
    {
        try
        {
            const string secret = "abcdef0123456789abcdef0123456789";
            var protectedText = Dpapi.Protect(secret);
            var roundtrip = Dpapi.Unprotect(protectedText);
            Log.Info(
                $"selftest dpapi | 密文长度={protectedText.Length} | 不是明文={protectedText != secret} | " +
                $"还原一致={roundtrip == secret}");

            var manager = new HotkeyManager();

            var first = manager.TryApply(
                [("toggle-watch", new HotkeyGesture(HotkeyGesture.ModControl | HotkeyGesture.ModAlt, 0x79))],
                out var firstError);
            Log.Info($"selftest hotkey 首次注册={first} | 错误=[{firstError}] | 当前={manager.Describe("toggle-watch")}");

            var rebind = manager.TryApply(
                [("toggle-watch", new HotkeyGesture(HotkeyGesture.ModControl | HotkeyGesture.ModAlt, 0x7A))],
                out var rebindError);
            Log.Info($"selftest hotkey 换绑={rebind} | 错误=[{rebindError}] | 当前={manager.Describe("toggle-watch")}");

            // Ctrl+Alt+Delete 被系统保留，注册必然失败：用来验证"失败后旧热键仍在"
            var conflict = manager.TryApply(
                [("toggle-watch", new HotkeyGesture(HotkeyGesture.ModControl | HotkeyGesture.ModAlt, 0x2E))],
                out var conflictError);
            Log.Info(
                $"selftest hotkey 冲突={conflict} | 错误=[{conflictError}] | " +
                $"回滚后={manager.Describe("toggle-watch")}");

            manager.Dispose();

            // 运行中把两个动作的组合键互换（设置窗口保存就会走这条路径）：
            // A 的新键被 B 的旧键占着、B 的新键被 A 的旧键占着，必须互相让位才能成功。
            var swap = new HotkeyManager();
            var swapBase = swap.TryApply(
                [("toggle-watch", CtrlAlt(0x52)), ("toggle-pause", CtrlAlt(0x78))],
                out _);
            var swapOk = swap.TryApply(
                [("toggle-watch", CtrlAlt(0x78)), ("toggle-pause", CtrlAlt(0x52))],
                out var swapError);
            Log.Info(
                $"selftest hotkey 交换: 建立基础={swapBase} | 交换结果={swapOk} | 错误=[{swapError}] | " +
                $"watch={swap.Describe("toggle-watch")} | pause={swap.Describe("toggle-pause")}");

            // OEM 单字符键（KeyName 显示成 , - = . /）的解析往返：
            // 录完保存没问题，重启后解析不回来就会静默丢成热键默认值。
            foreach (var virtualKey in new uint[] { 0xBB, 0xBD, 0xBC, 0xBE, 0xBF })
            {
                var oem = new HotkeyGesture(HotkeyGesture.ModControl | HotkeyGesture.ModAlt, virtualKey);
                var oemOk = HotkeyGesture.TryParse(oem.ToString(), out var back) && back == oem;
                Log.Info($"selftest hotkey OEM 往返 {oem} = {oemOk}");
            }

            swap.Dispose();

            // 配置往返：验证"设置窗口保存 → 重启读取"这条链路（含 DPAPI 字段）。
            // 这里读写的都是临时配置（AppPaths.UseTemporaryConfig），真实配置不受影响。
            var probe = SettingsStore.Load();
            probe.Engine = "azure";
            probe.AzureRegion = "eastasia";
            probe.AzureKeyProtected = Dpapi.Protect("test-key-123456");
            probe.Hotkeys[HotkeyActions.ToggleWatch.Id] = "Ctrl+Shift+F9";
            _ = SettingsStore.Save(probe);

            var reloaded = SettingsStore.Load();
            var key = Dpapi.Unprotect(reloaded.AzureKeyProtected);
            Log.Info(
                $"selftest config 往返 | 引擎={reloaded.Engine} | 区域={reloaded.AzureRegion} | " +
                $"Key还原一致={key == "test-key-123456"} | 热键={reloaded.Hotkeys.GetValueOrDefault("toggle-watch")}");

            // 坏字段修复：值类型字段写成 null 时整份反序列化会失败，
            // 但绝不能因此把语音/热键/板子位置一起抹成默认值。
            File.WriteAllText(
                AppPaths.ConfigPath,
                "{ \"voice\": \"zh-CN-YunxiNeural\", \"watchEnabled\": true, \"boardVisible\": null, " +
                "\"boardX\": 1685, \"boardY\": 89, \"hotkeys\": { \"toggle-watch\": \"Ctrl+Alt+F10\" } }");

            var repaired = SettingsStore.Load();
            Log.Info(
                $"selftest config 修复 | 语音={repaired.Voice} | 监听={repaired.WatchEnabled} | " +
                $"板=({repaired.BoardX},{repaired.BoardY}) | 可见={repaired.BoardVisible} | " +
                $"热键={repaired.Hotkeys.GetValueOrDefault("toggle-watch")}");

            // 故意用错 Key，确认失败信息是给人看得懂的那种
            var azure = new Tts.AzureTtsClient(reloaded.AzureRegion, key);
            try
            {
                using var buffer = new MemoryStream();
                await azure.StreamAsync(
                    "测试", reloaded.Voice, reloaded.Rate,
                    chunk => buffer.Write(chunk.Span), null, CancellationToken.None);
                Log.Warn("selftest azure 用错误 Key 竟然成功了，需要复查");
            }
            catch (Exception ex)
            {
                Log.Info($"selftest azure 错误 Key 的提示：{ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("selftest config failed", ex);
        }
        finally
        {
            Shutdown(0);
        }
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
            player.MarkFeederDone(generation);

            var waited = 0;
            var samples = new System.Text.StringBuilder();
            var watch = System.Diagnostics.Stopwatch.StartNew();

            while (!player.IsDrained && waited < 60000)
            {
                await Task.Delay(200);
                waited += 200;

                if (waited % 1000 == 0)
                {
                    var pos = player.PositionSeconds;
                    var dbg = player.PositionDebug;
                    samples.Append(
                        $"\n  {watch.ElapsedMilliseconds}ms pos={pos:F3} dev={dbg.Device} est={dbg.Estimate} " +
                        $"play={dbg.Played} rep={dbg.Reported} age={dbg.AnchorAge} buf={dbg.Buffered} wr={dbg.Written}");
                }
            }

            Log.Info($"selftest 位置采样{samples}");

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

        if (_ownsInstanceMutex)
        {
            try
            {
                _instanceMutex?.ReleaseMutex();
            }
            catch
            {
                // 只在自己的线程上持有时才会成功；退出路径不该再抛异常
            }
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
