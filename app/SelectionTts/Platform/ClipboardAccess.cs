using System.Runtime.InteropServices;
using System.Windows.Forms;
using SelectionTts.Core;

namespace SelectionTts.Platform;

/// <summary>
/// 只读剪贴板文本。不再做快照/还原——用户主动复制的内容不应该被改回去。
/// </summary>
internal static class ClipboardAccess
{
    // 密码管理器和部分浏览器复制敏感内容时打这两个标记，要求剪贴板工具不要处理。
    private const string ExcludeFromMonitor = "ExcludeClipboardContentFromMonitorProcessing";
    private const string IncludeInHistory = "CanIncludeInClipboardHistory";

    /// <summary>
    /// Retry 只在剪贴板被别的进程占用时返回 true；
    /// 没有文本格式（图片、文件）属于正常结果，不重试。
    /// </summary>
    public static (string? Text, bool Retry) Read()
    {
        try
        {
            if (IsExcluded())
            {
                return (null, false);
            }

            return (ReadText(), false);
        }
        catch (ExternalException)
        {
            return (null, true);
        }
        catch (Exception ex)
        {
            Log.Error("读取剪贴板失败", ex);
            return (null, false);
        }
    }

    private static string? ReadText()
    {
        if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
        {
            return Clipboard.GetText(TextDataFormat.UnicodeText);
        }

        return Clipboard.ContainsText() ? Clipboard.GetText() : null;
    }

    private static bool IsExcluded()
    {
        if (Clipboard.ContainsData(ExcludeFromMonitor))
        {
            return true;
        }

        var data = Clipboard.GetDataObject();
        if (data is null || !data.GetDataPresent(IncludeInHistory))
        {
            return false;
        }

        return data.GetData(IncludeInHistory) switch
        {
            MemoryStream stream when stream.Length >= 4 => ReadDword(stream) == 0,
            byte[] bytes when bytes.Length >= 4 => BitConverter.ToUInt32(bytes, 0) == 0,
            int value => value == 0,
            _ => false,
        };
    }

    private static uint ReadDword(MemoryStream stream)
    {
        var position = stream.Position;
        stream.Position = 0;
        var buffer = new byte[4];
        var read = stream.Read(buffer, 0, 4);
        stream.Position = position;
        return read < 4 ? 1 : BitConverter.ToUInt32(buffer, 0);
    }
}
