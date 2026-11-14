namespace SelectionTts.Core;

public sealed record VoiceInfo(string Id, string Label);

public static class VoiceCatalog
{
    public const string Default = "zh-CN-XiaoxiaoNeural";

    public static readonly IReadOnlyList<VoiceInfo> Voices =
    [
        new("zh-CN-XiaoxiaoNeural", "晓晓（女声，默认）"),
        new("zh-CN-XiaoyiNeural", "晓伊（女声，活泼）"),
        new("zh-CN-YunxiNeural", "云希（男声，年轻）"),
        new("zh-CN-YunjianNeural", "云健（男声，浑厚）"),
    ];

    public static readonly IReadOnlyList<string> Rates = ["-20%", "+0%", "+20%", "+50%"];

    public static bool IsKnown(string voice) => Voices.Any(v => v.Id == voice);

    public static string LabelOf(string voice) =>
        Voices.FirstOrDefault(v => v.Id == voice)?.Label ?? voice;
}
