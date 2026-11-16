namespace SelectionTts.Core;

/// <summary>
/// 朗读板上的一个「标点子句」。Start/Length 是它在全文里的位置，
/// 点击靠它定位到子句开头，高亮也只覆盖这一段的字符。
/// </summary>
public readonly record struct Clause(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// 按标点把全文切成子句。
///
/// 为什么不用「视觉行」：折行只跟板子宽度有关，行尾可能正好断在词中间，
/// 点击就会停在半句话上。标点才是语义上的停顿点，音频里也一定有时间戳。
///
/// 规则（与交互设计一致）：
/// - 断点：句号、逗号、顿号、分号、冒号、问号、叹号、省略号、破折号、换行
/// - 引号/括号/书名号不算断点，紧跟断点之后的收尾符号归前一句
/// - 连续标点（…… —— ！？）合并成一个断点，整体附在前一句末尾
/// - 数字之间的 '.' 和 ':' 不断（3.14 / 12:30）
/// - 纯标点、纯空白不单独成子句
/// </summary>
public static class ClauseMap
{
    private static readonly char[] BreakChars =
    [
        '。', '，', '、', '；', '：', '！', '？', '…', '—',
        ',', '.', ';', ':', '!', '?', '\n',
    ];

    /// <summary>断点之后如果紧跟这些收尾符号，算前一句的一部分。</summary>
    private static readonly char[] TrailingChars =
    [
        '"', '”', '’', '\'', ')', '）', '」', '』', '】', '》',
    ];

    public static List<Clause> Split(string text)
    {
        var result = new List<Clause>();
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        var start = 0;
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] == '\n')
            {
                // 换行是硬边界：段落结束了，即使没有标点也不能跨过去
                AddClause(result, text, start, i);
                i++;
                start = i;
                continue;
            }

            if (!IsBreakAt(text, i))
            {
                i++;
                continue;
            }

            var end = i + 1;

            // 连续标点并成一个断点
            while (end < text.Length && text[end] != '\n' && IsBreakAt(text, end))
            {
                end++;
            }

            // 收尾引号/括号归前一句
            while (end < text.Length && text[end] != '\n' &&
                   Array.IndexOf(TrailingChars, text[end]) >= 0)
            {
                end++;
            }

            AddClause(result, text, start, end);
            start = end;
            i = end;
        }

        AddClause(result, text, start, text.Length);
        return result;
    }

    /// <summary>charIndex 落在哪个子句；落在子句之间的空白/被丢弃的标点上时归前一句。</summary>
    public static int IndexOf(IReadOnlyList<Clause> clauses, int charIndex)
    {
        if (clauses.Count == 0)
        {
            return -1;
        }

        var index = Math.Max(0, charIndex);
        var low = 0;
        var high = clauses.Count - 1;
        var best = 0;

        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (clauses[mid].Start <= index)
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best;
    }

    private static bool IsBreakAt(string text, int i)
    {
        var c = text[i];
        if (Array.IndexOf(BreakChars, c) < 0)
        {
            return false;
        }

        if (c == '\n')
        {
            return true;
        }

        // 数字之间的小数点/冒号不断句：3.14、12:30、12：30
        if (c is '.' or ':' or '：')
        {
            var previous = i > 0 ? text[i - 1] : ' ';
            var next = i + 1 < text.Length ? text[i + 1] : ' ';

            if (char.IsDigit(previous) && char.IsDigit(next))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddClause(List<Clause> result, string text, int start, int end)
    {
        // 前导空白不属于正文
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        if (end <= start)
        {
            return;
        }

        var hasContent = false;
        for (var i = start; i < end; i++)
        {
            if (!char.IsWhiteSpace(text[i]) && Array.IndexOf(BreakChars, text[i]) < 0)
            {
                hasContent = true;
                break;
            }
        }

        if (!hasContent)
        {
            return;
        }

        result.Add(new Clause(start, end - start));
    }
}
