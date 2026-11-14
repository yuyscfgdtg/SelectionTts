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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, @"Local\SelectionTts.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Log.Warn("已有实例在运行，本次启动退出");
            Shutdown();
            return;
        }

        AppPaths.Initialize();
        Log.Initialize(AppPaths.DataDir);
        Log.Info($"启动 SelectionTts {typeof(App).Assembly.GetName().Version} | 数据目录 {AppPaths.DataDir}");

        var settings = SettingsStore.Load();
        _tray = new TrayIconManager(settings);
        _hotkeys = new HotkeyManager();
        _hotkeys.ReadRequested += () => _tray.Notify("热键：朗读当前选区（功能开发中）");
        _hotkeys.TogglePopupRequested += () => _tray.ToggleAutoPopup();
        _tray.ExitRequested += Shutdown;

        if (!_hotkeys.RegisterDefaults())
        {
            _tray.Notify("快捷键注册失败，可能已被其他程序占用");
        }

        Log.Info("初始化完成");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _tray?.Dispose();
        Log.Info("退出");
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
