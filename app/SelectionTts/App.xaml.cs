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

        _tray.ToggleWatchRequested += () => SetWatching(!_watching, persist: true);
        _tray.StopRequested += () => _reader.Stop();
        _tray.ExitRequested += Shutdown;

        _hotkeys.ToggleWatchRequested += () => Dispatcher.Invoke(() => SetWatching(!_watching, persist: true));

        _reader.StatusChanged += status => Dispatcher.Invoke(() =>
        {
            Log.Info($"status: {status}");
            _tray.SetStatus(status);
        });

        _reader.Failed += message => Dispatcher.Invoke(() => _tray.Notify(message));

        // 复制即读：新内容直接顶掉正在朗读的旧内容
        _watcher.TextCopied += text => _reader.Speak(text);

        if (!_hotkeys.RegisterDefaults())
        {
            _tray.Notify("快捷键注册失败，可能已被其他程序占用");
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
        _tray.SetWatching(on);

        if (!on)
        {
            _reader?.Stop();
        }

        if (persist && _settings is not null)
        {
            _settings.WatchEnabled = on;
            SettingsStore.Save(_settings);
            _tray.Notify(on ? "已开启监听：复制文本即朗读" : "已关闭监听");
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
        _watcher?.Dispose();
        _hotkeys?.Dispose();
        _reader?.Dispose();
        _tray?.Dispose();
        Log.Info("退出");
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
