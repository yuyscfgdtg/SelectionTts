using SelectionTts.Core;

namespace SelectionTts.Platform;

/// <summary>
/// 开机自启 = 在当前用户的「启动」文件夹里放一个快捷方式。
///
/// 用快捷方式而不是注册表 Run 项：用户可以自己删、任务管理器里看得见，
/// 而且和安装包用的是同一套机制（同一个文件名），不会出现两份自启。
/// </summary>
public static class AutoStart
{
    private const string ShortcutName = "剪贴板朗读.lnk";

    public static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName);

    public static bool IsEnabled => File.Exists(ShortcutPath);

    public static void Apply(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (File.Exists(ShortcutPath))
                {
                    File.Delete(ShortcutPath);
                }

                Log.Info("开机自启：已关闭");
                return;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return;
            }

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                Log.Warn("系统里没有 WScript.Shell，无法创建开机自启快捷方式");
                return;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(ShortcutPath);
            shortcut.TargetPath = exe;
            shortcut.WorkingDirectory = AppContext.BaseDirectory;
            shortcut.Description = "剪贴板朗读";
            shortcut.IconLocation = exe;
            shortcut.Save();

            Log.Info($"开机自启：已开启 → {ShortcutPath}");
        }
        catch (Exception ex)
        {
            Log.Error("设置开机自启失败", ex);
        }
    }
}
