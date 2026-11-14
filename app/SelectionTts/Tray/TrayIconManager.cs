using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using SelectionTts.Core;

namespace SelectionTts.Tray;

public sealed class TrayIconManager : IDisposable
{
    private readonly AppSettings _settings;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _autoPopupItem;
    private readonly ToolStripMenuItem _fullscreenItem;
    private readonly Dictionary<string, ToolStripMenuItem> _voiceItems = [];
    private readonly Dictionary<string, ToolStripMenuItem> _rateItems = [];

    public event Action? ExitRequested;
    public event Action? ReadRequested;
    public event Action? StopRequested;

    public TrayIconManager(AppSettings settings)
    {
        _settings = settings;

        _autoPopupItem = new ToolStripMenuItem("自动弹窗    Ctrl+Alt+Shift+P")
        {
            Checked = settings.AutoPopup,
            CheckOnClick = false,
        };
        _autoPopupItem.Click += (_, _) =>
        {
            Log.Info("托盘菜单：点击自动弹窗");
            ToggleAutoPopup();
        };

        _fullscreenItem = new ToolStripMenuItem("全屏/游戏时暂停自动弹窗")
        {
            Checked = settings.PauseInFullscreen,
        };
        _fullscreenItem.Click += (_, _) => ToggleFullscreenPause();

        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add(new ToolStripMenuItem("朗读当前选区    Ctrl+Alt+R", null, (_, _) => ReadRequested?.Invoke()));
        menu.Items.Add(_autoPopupItem);
        menu.Items.Add(_fullscreenItem);
        menu.Items.Add(new ToolStripMenuItem("停止朗读", null, (_, _) => StopRequested?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(BuildVoiceMenu());
        menu.Items.Add(BuildRateMenu());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("打开数据目录", null, (_, _) => OpenDataDir()));
        menu.Items.Add(new ToolStripMenuItem("打开日志", null, (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitRequested?.Invoke()));

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "划词朗读",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    public void Notify(string message) =>
        _icon.ShowBalloonTip(2200, "划词朗读", message, ToolTipIcon.None);

    /// <summary>托盘悬停提示，最多 63 个字符（Win32 限制）。</summary>
    public void SetStatus(string status)
    {
        var text = "划词朗读 · " + status;
        _icon.Text = text.Length <= 63 ? text : text[..63];
    }

    public void ToggleAutoPopup()
    {
        _settings.AutoPopup = !_settings.AutoPopup;
        _autoPopupItem.Checked = _settings.AutoPopup;
        SettingsStore.Save(_settings);
        Log.Info($"自动弹窗：{(_settings.AutoPopup ? "开启" : "关闭")}");
        Notify(_settings.AutoPopup ? "自动弹窗已开启" : "自动弹窗已关闭（快捷键仍可用）");
    }

    private void ToggleFullscreenPause()
    {
        _settings.PauseInFullscreen = !_settings.PauseInFullscreen;
        _fullscreenItem.Checked = _settings.PauseInFullscreen;
        SettingsStore.Save(_settings);
        Log.Info($"全屏暂停：{(_settings.PauseInFullscreen ? "开启" : "关闭")}");
        Notify(_settings.PauseInFullscreen ? "全屏/游戏时已暂停自动弹窗" : "全屏/游戏时也会自动弹窗");
    }

    private ToolStripMenuItem BuildVoiceMenu()
    {
        var root = new ToolStripMenuItem("音色");
        foreach (var voice in VoiceCatalog.Voices)
        {
            var item = new ToolStripMenuItem(voice.Label)
            {
                Checked = voice.Id == _settings.Voice,
                Tag = voice.Id,
            };
            item.Click += (sender, _) =>
            {
                var id = (string)((ToolStripMenuItem)sender!).Tag!;
                _settings.Voice = id;
                SettingsStore.Save(_settings);
                UpdateChecks(_voiceItems, id);
                Log.Info($"切换音色 {id}");
                Notify($"音色：{VoiceCatalog.LabelOf(id)}");
            };
            _voiceItems[voice.Id] = item;
            root.DropDownItems.Add(item);
        }

        return root;
    }

    private ToolStripMenuItem BuildRateMenu()
    {
        var root = new ToolStripMenuItem("语速");
        var labels = new Dictionary<string, string>
        {
            ["-20%"] = "慢（-20%）",
            ["+0%"] = "正常",
            ["+20%"] = "快（+20%）",
            ["+50%"] = "很快（+50%）",
        };

        foreach (var rate in VoiceCatalog.Rates)
        {
            var item = new ToolStripMenuItem(labels[rate])
            {
                Checked = rate == _settings.Rate,
                Tag = rate,
            };
            item.Click += (sender, _) =>
            {
                var value = (string)((ToolStripMenuItem)sender!).Tag!;
                _settings.Rate = value;
                SettingsStore.Save(_settings);
                UpdateChecks(_rateItems, value);
                Log.Info($"切换语速 {value}");
                Notify($"语速：{labels[value]}");
            };
            _rateItems[rate] = item;
            root.DropDownItems.Add(item);
        }

        return root;
    }

    private static void UpdateChecks(Dictionary<string, ToolStripMenuItem> items, string selected)
    {
        foreach (var (key, item) in items)
        {
            item.Checked = key == selected;
        }
    }

    private static void OpenDataDir()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.DataDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("打开数据目录失败", ex);
        }
    }

    private static void OpenLog()
    {
        try
        {
            var path = Path.Combine(AppPaths.LogDir, "app.log");
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Error("打开日志失败", ex);
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            if (icon is not null)
            {
                return icon;
            }
        }
        catch
        {
            // ignore
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
