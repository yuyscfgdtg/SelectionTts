using System.Globalization;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;

namespace SelectionTts.Ui;

/// <summary>朗读板上的一行：Start/Length 是它在全文里的位置。</summary>
public readonly record struct BoardLine(int Start, int Length, string Text);

/// <summary>
/// 按朗读板的实际宽度把文本折成"视觉行"。
///
/// 为什么要自己折行：WPF 让 TextBlock 自动折行的话，程序不知道哪几个字落在第几行，
/// 就没法"读到哪行亮哪行"，也没法"点第几行从第几行读"。
/// 自己折完，一行就是一个单元格，两者都成立。
/// </summary>
public static class BoardTextLayout
{
    public static List<BoardLine> Build(
        string text, double maxWidth, Typeface typeface, double fontSize, double pixelsPerDip)
    {
        var lines = new List<BoardLine>();
        if (string.IsNullOrEmpty(text) || maxWidth <= 24)
        {
            return lines;
        }

        // 留一点余量，避免测量误差把最后一个字挤出边界
        var width = maxWidth - 4;
        var index = 0;

        while (index <= text.Length)
        {
            var newline = text.IndexOf('\n', index);
            var end = newline < 0 ? text.Length : newline;

            if (end == index)
            {
                // 空行保留成一条零宽行，当作段落间距
                lines.Add(new BoardLine(index, 0, ""));
                if (newline < 0)
                {
                    break;
                }

                index = end + 1;
                continue;
            }

            var cursor = index;
            while (cursor < end)
            {
                var take = Fit(text, cursor, end, width, typeface, fontSize, pixelsPerDip);
                lines.Add(new BoardLine(cursor, take, text.Substring(cursor, take)));
                cursor += take;

                while (cursor < end && text[cursor] == ' ')
                {
                    cursor++;
                }
            }

            if (newline < 0)
            {
                break;
            }

            index = end + 1;
        }

        return lines;
    }

    private static int Fit(
        string text, int start, int end, double maxWidth, Typeface typeface, double fontSize, double pixelsPerDip)
    {
        var available = end - start;
        if (Measure(text, start, available, typeface, fontSize, pixelsPerDip) <= maxWidth)
        {
            return available;
        }

        var low = 1;
        var high = available;
        var best = 1;

        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (Measure(text, start, mid, typeface, fontSize, pixelsPerDip) <= maxWidth)
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        // 英文尽量不切断单词：回退到这一行最后一个空格
        if (best > 1 && best < available &&
            char.IsLetterOrDigit(text[start + best - 1]) && char.IsLetterOrDigit(text[start + best]))
        {
            for (var i = start + best - 1; i > start; i--)
            {
                if (text[i] == ' ')
                {
                    best = i - start;
                    break;
                }
            }
        }

        return Math.Max(1, best);
    }

    private static double Measure(
        string text, int start, int count, Typeface typeface, double fontSize, double pixelsPerDip)
    {
        var formatted = new FormattedText(
            text.Substring(start, count),
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Black,
            pixelsPerDip);

        return formatted.WidthIncludingTrailingWhitespace;
    }
}
