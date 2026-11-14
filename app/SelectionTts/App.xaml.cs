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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.Initialize();
        Log.Initialize(AppPaths.DataDir);

        if (e.Args.Contains("--selftest-drm"))
        {
            foreach (var timestamp in new long[] { 1700000000, 1730000000, 1760000000, 1800000000 })
            {
                Log.Info($"drm {timestamp} {Tts.DrmToken.Generate(timestamp)}");
            }

            Shutdown(0);
            return;
        }

        if (e.Args.Contains("--test-target"))
        {
            RunTestTarget(readAfter: e.Args.Contains("--read-after"));
            return;
        }

        if (e.Args.Contains("--selftest-uia"))
        {
            RunUiaSelfTest();
            return;
        }

        if (e.Args.Contains("--selftest-read"))
        {
            RunReadSelfTest(e.Args.Contains("--speak"));
            return;
        }

        if (e.Args.Contains("--selftest-tts"))
        {
            RunTtsSelfTest();
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

        var settings = SettingsStore.Load();
        _reader = new ReaderService(settings);
        _tray = new TrayIconManager(settings);
        _hotkeys = new HotkeyManager();

        _tray.ReadRequested += () => _reader.ReadSelection();
        _tray.StopRequested += () => _reader.Stop();
        _tray.ExitRequested += Shutdown;

        _reader.StatusChanged += status => Dispatcher.Invoke(() =>
        {
            Log.Info($"status: {status}");
            _tray.SetStatus(status);
        });
        _reader.Failed += message => Dispatcher.Invoke(() => _tray.Notify(message));

        _hotkeys.ReadRequested += () => _reader.ReadSelection();
        _hotkeys.TogglePopupRequested += () => _tray.ToggleAutoPopup();

        if (!_hotkeys.RegisterDefaults())
        {
            _tray.Notify("快捷键注册失败，可能已被其他程序占用");
        }

        Log.Info("初始化完成");
    }

    private void RunTestTarget(bool readAfter)
    {
        var box = new System.Windows.Controls.TextBox
        {
            Text = "划词朗读测试窗口。第一段用来验证 UIA TextPattern 取词。" +
                   "Selection TTS should read this English sentence too." +
                   "第三段包含数字 12345 和标点符号，用于检查合成是否完整。",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 18,
            Margin = new Thickness(16),
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
        };

        var window = new Window
        {
            Title = "SelectionTTS 测试窗口",
            Width = 760,
            Height = 320,
            Content = box,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
        };

        window.Loaded += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            ForegroundInfo.ForceForeground(hwnd);
            window.Activate();
            window.Topmost = false;
            box.Focus();
            box.SelectAll();

            // 从后台启动时前台锁可能刚被终端占用，稍后重试两次
            _ = Task.Run(async () =>
            {
                await Task.Delay(700);
                Dispatcher.Invoke(() =>
                {
                    ForegroundInfo.ForceForeground(hwnd);
                    window.Activate();
                    box.Focus();
                });

                await Task.Delay(800);
                Dispatcher.Invoke(() =>
                {
                    ForegroundInfo.ForceForeground(hwnd);
                    window.Activate();
                    box.Focus();
                });
            });

            if (readAfter)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2500);
                    var selection = SelectionReader.Read(
                        preferUia: false,
                        restoreClipboard: true,
                        waitReleaseMs: 0,
                        allowSelf: true);

                    Log.Info(
                        $"selftest clipboard | 方法={selection.Method} 字数={selection.Text.Length} " +
                        $"耗时={selection.ElapsedMs}ms 恢复={selection.ClipboardRestored}");

                    File.WriteAllText(
                        Path.Combine(AppPaths.CacheDir, "selftest-clipboard.txt"),
                        selection.Text);
                });
            }
        };

        window.Show();
    }

    private void RunUiaSelfTest()
    {
        try
        {
            var ok = Platform.StaExecutor.TryRun(() =>
            {
                var element = System.Windows.Automation.AutomationElement.FocusedElement;
                if (element is null)
                {
                    return "focused=none";
                }

                var current = element.Current;
                var pattern = element.TryGetCurrentPattern(
                    System.Windows.Automation.TextPattern.Pattern, out var patternObject)
                    ? patternObject
                    : null;

                var detail =
                    $"focused class={current.ClassName} name={current.Name} " +
                    $"type={current.ControlType?.ProgrammaticName} pid={current.ProcessId} " +
                    $"password={current.IsPassword} textPattern={pattern is not null}";

                if (pattern is System.Windows.Automation.TextPattern textPattern)
                {
                    var ranges = textPattern.GetSelection();
                    detail += $" ranges={ranges.Length} chars={ranges.Sum(r => r.GetText(-1).Length)}";
                }

                return detail;
            }, 3000, out var result);

            Log.Info($"selftest uia | ok={ok} | {result}");
        }
        catch (Exception ex)
        {
            Log.Error("selftest uia failed", ex);
        }
        finally
        {
            Shutdown(0);
        }
    }

    private async void RunReadSelfTest(bool speak)
    {
        try
        {
            var settings = SettingsStore.Load();
            var foreground = ForegroundInfo.Current();
            Log.Info($"selftest read | 前台 class={foreground.ClassName} process={foreground.ProcessName}");

            var selection = SelectionReader.Read(settings.UseUia, settings.ClipboardRestore, waitReleaseMs: 0);
            Log.Info(
                $"selftest read | 方法={selection.Method} 字数={selection.Text.Length} " +
                $"耗时={selection.ElapsedMs}ms 剪贴板恢复={selection.ClipboardRestored}");

            var path = Path.Combine(AppPaths.CacheDir, "selftest-read.txt");
            File.WriteAllText(path, selection.Text);
            Log.Info($"selftest read | 文本已写入 {path}");

            if (!speak || string.IsNullOrWhiteSpace(selection.Text))
            {
                Shutdown(0);
                return;
            }

            // 端到端：取到的文本直接走完整朗读流程（合成 + 播放）
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reader = new ReaderService(settings);
            reader.StatusChanged += status =>
            {
                Log.Info($"e2e status: {status}");
                if (status is "朗读完成" or "朗读失败" or "已停止")
                {
                    done.TrySetResult(status == "朗读完成");
                }
            };
            reader.Failed += message => Log.Error($"e2e failed: {message}");

            reader.ReadText(selection.Text);
            var finished = await Task.WhenAny(done.Task, Task.Delay(60_000));
            Log.Info($"selftest e2e | 完成={finished == done.Task && done.Task.Result}");
        }
        catch (Exception ex)
        {
            Log.Error("selftest read failed", ex);
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

            var player = new Audio.AudioPlayer();
            player.Play(audio.Audio, ok =>
            {
                Log.Info($"selftest play done ok={ok}");
                Dispatcher.Invoke(() => Shutdown(0));
            });
        }
        catch (Exception ex)
        {
            Log.Error("selftest tts failed", ex);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _reader?.Dispose();
        _tray?.Dispose();
        Log.Info("退出");
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
