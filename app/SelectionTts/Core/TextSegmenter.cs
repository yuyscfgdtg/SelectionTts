namespace SelectionTts.Core;

/// <summary>
/// 一段待合成的文本。Start/Length 是它在整篇文本里的位置，
/// 朗读板靠这两个值把"读到第几个字"映射回高亮块。
/// </summary>
public readonly record struct TextSegment(int Index, int Start, int Length, string Text)
{
    public int End => Start + Length;
}

/// <summary>
/// 把整篇文本切成合成块。
///
/// 规则：
/// - 优先在句末（。！？；换行）断开，绝不把一句话切成两半；
/// - 第一块用更小的上限（默认 50 字）：首块大小直接决定"多久出声"；
/// - 其余块按 250 字打包；
/// - 单句超过上限才降级到逗号/顿号，再不行才硬切；
/// - 块是原文本的连续切片，拼起来等于原文，位置不会错位。
/// </summary>
public static class TextSegmenter
{
    public const int TargetChars = 250;
    public const int FirstTargetChars = 50;

    /// <summary>朗读板上的高亮块大小（按句子对齐）。</summary>
    public const int DisplayBlockChars = 750;

    /// <summary>只按显示需要切块，不做首块特殊处理。</summary>
    public static List<TextSegment> SplitDisplayBlocks(string text) =>
        Split(text, DisplayBlockChars, 0);

    private static readonly char[] HardBreakChars =
        ['。', '！', '？', '；', '!', '?', ';', '\n'];

    private static readonly char[] SoftBreakChars =
        ['，', '、', '：', ',', ':', '—', ' '];

    private static readonly char[] TrailingChars =
        ['"', '”', '’', ')', '）', '」', '』', '】', '》'];

    public static List<TextSegment> Split(
        string text, int target = TargetChars, int firstTarget = FirstTargetChars)
    {
        var result = new List<TextSegment>();
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        if (target < 50)
        {
            target = 50;
        }

        if (firstTarget <= 0 || firstTarget > target)
        {
            firstTarget = target;
        }

        var pendingStart = -1;
        var pendingEnd = -1;
        var limit = firstTarget;

        void Flush()
        {
            if (pendingStart < 0)
            {
                return;
            }

            AddSlice(result, text, pendingStart, pendingEnd);
            pendingStart = -1;
            pendingEnd = -1;
            // 首块落地后，后续都按正常长度打包
            limit = target;
        }

        foreach (var (start, end) in FindSentenceBounds(text))
        {
            if (end - start > target)
            {
                Flush();
                foreach (var (pieceStart, pieceEnd) in SplitOversized(text, start, end, target))
                {
                    AddSlice(result, text, pieceStart, pieceEnd);
                }

                continue;
            }

            if (pendingStart < 0)
            {
                pendingStart = start;
                pendingEnd = end;
            }
            else if (end - pendingStart <= limit)
            {
                pendingEnd = end;
            }
            else
            {
                Flush();
                pendingStart = start;
                pendingEnd = end;
            }
        }

        Flush();
        return result;
    }

    private static void AddSlice(List<TextSegment> result, string text, int start, int end)
    {
        if (end <= start)
        {
            return;
        }

        var slice = text.Substring(start, end - start);
        if (slice.Trim().Length == 0)
        {
            return;
        }

        result.Add(new TextSegment(result.Count, start, slice.Length, slice));
    }

    private static IEnumerable<(int Start, int End)> FindSentenceBounds(string text)
    {
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var isBreak = Array.IndexOf(HardBreakChars, c) >= 0;

            if (!isBreak && c == '.')
            {
                // 英文句号：后面是空白或行尾才算断句，避免切开 3.14 / file.txt
                var next = i + 1 < text.Length ? text[i + 1] : ' ';
                isBreak = char.IsWhiteSpace(next);
            }

            if (!isBreak)
            {
                continue;
            }

            var end = i + 1;
            while (end < text.Length && Array.IndexOf(TrailingChars, text[end]) >= 0)
            {
                end++;
            }

            yield return (start, end);
            start = end;
            i = end - 1;
        }

        if (start < text.Length)
        {
            yield return (start, text.Length);
        }
    }

    private static IEnumerable<(int Start, int End)> SplitOversized(
        string text, int start, int end, int target)
    {
        var chunkStart = start;
        var lastSoftBreak = -1;

        for (var i = start; i < end; i++)
        {
            if (Array.IndexOf(SoftBreakChars, text[i]) >= 0)
            {
                lastSoftBreak = i + 1;
            }

            if (i - chunkStart + 1 < target)
            {
                continue;
            }

            var cut = lastSoftBreak > chunkStart ? lastSoftBreak : i + 1;
            yield return (chunkStart, cut);
            chunkStart = cut;
            lastSoftBreak = -1;
            i = cut - 1;
        }

        if (chunkStart < end)
        {
            yield return (chunkStart, end);
        }
    }
}
