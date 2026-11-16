namespace SelectionTts.Core;

/// <summary>
/// 一段待合成的文本。Start/Length 是它在整篇文本里的位置，
/// 朗读板靠这两个值把"读到第几个字"映射回高亮行。
/// </summary>
public readonly record struct TextSegment(int Index, int Start, int Length, string Text)
{
    public int End => Start + Length;
}

/// <summary>
/// 按"句"切分合成单位。
///
/// 为什么是句子而不是 250 字：只有让每个合成段都从句子开头开始，
/// 点某一行时才能"从这一句开头播"，而不需要音频时间戳（Azure 没有）。
/// 段 = 音频缓存的最小单位，seek 命中即可秒开。
/// </summary>
public static class TextSegmenter
{
    /// <summary>单句超过这个长度就按逗号等降级再切，避免一次请求过大。</summary>
    public const int SentenceMaxChars = 150;

    private static readonly char[] HardBreakChars =
        ['。', '！', '？', '；', '!', '?', ';', '\n'];

    private static readonly char[] SoftBreakChars =
        ['，', '、', '：', ',', ':', '—', ' '];

    private static readonly char[] TrailingChars =
        ['"', '”', '’', ')', '）', '」', '』', '】', '》'];

    /// <summary>一句一段。整篇文本切完后，拼起来等于原文，位置不会错位。</summary>
    public static List<TextSegment> SplitSentences(string text)
    {
        var result = new List<TextSegment>();
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        foreach (var (start, end) in FindSentenceBounds(text))
        {
            if (end - start > SentenceMaxChars)
            {
                foreach (var (pieceStart, pieceEnd) in SplitOversized(text, start, end, SentenceMaxChars))
                {
                    AddSlice(result, text, pieceStart, pieceEnd);
                }

                continue;
            }

            AddSlice(result, text, start, end);
        }

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
