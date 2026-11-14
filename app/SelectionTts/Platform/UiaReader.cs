using System.Text;
using System.Windows.Automation;
using SelectionTts.Core;

namespace SelectionTts.Platform;

internal static class UiaReader
{
    /// <summary>
    /// 读取焦点控件的 TextPattern 选区。
    /// status 为 "self"/"password" 时调用方不应该再回退到剪贴板。
    /// </summary>
    public static (string? Text, string Status) TryRead()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null)
            {
                return (null, "no-focus");
            }

            if (focused.Current.ProcessId == Environment.ProcessId)
            {
                return (null, "self");
            }

            if (focused.Current.IsPassword)
            {
                return (null, "password");
            }

            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) ||
                patternObject is not TextPattern pattern)
            {
                return (null, "no-pattern");
            }

            var ranges = pattern.GetSelection();
            if (ranges.Length == 0)
            {
                return (null, "empty");
            }

            var builder = new StringBuilder();
            foreach (var range in ranges)
            {
                builder.Append(range.GetText(-1));
            }

            var text = builder.ToString();
            return string.IsNullOrWhiteSpace(text) ? (null, "empty") : (text, "ok");
        }
        catch (ElementNotAvailableException)
        {
            return (null, "unavailable");
        }
        catch (InvalidOperationException)
        {
            return (null, "invalid");
        }
        catch (Exception ex)
        {
            Log.Error("UIA 取词异常", ex);
            return (null, "error");
        }
    }
}
