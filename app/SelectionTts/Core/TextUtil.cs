using System.Text;
using System.Text.RegularExpressions;

namespace SelectionTts.Core;

public static partial class TextUtil
{
    [GeneratedRegex(@"(?<=[A-Za-z])-\n(?=[A-Za-z])")]
    private static partial Regex HyphenBreak();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex RunsOfSpaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            // 零宽字符会让 TTS 读出错音，直接去掉
            if (ch is '\u200b' or '\u200c' or '\u200d' or '\ufeff')
            {
                continue;
            }

            builder.Append(ch);
        }

        var normalized = HyphenBreak().Replace(builder.ToString(), "");
        normalized = RunsOfSpaces().Replace(normalized, " ");
        normalized = BlankLines().Replace(normalized, "\n\n");
        return normalized.Trim();
    }

    public static (string Text, bool Truncated) Truncate(string text, int maxChars)
    {
        if (maxChars <= 0 || text.Length <= maxChars)
        {
            return (text, false);
        }

        return (text[..maxChars], true);
    }
}
