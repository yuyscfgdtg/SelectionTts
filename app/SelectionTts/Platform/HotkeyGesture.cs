using System.Windows.Input;

namespace SelectionTts.Platform;

/// <summary>
/// 一个热键组合。Modifiers 用 Win32 的 MOD_* 常量，VirtualKey 用虚拟键码，
/// 这样和 RegisterHotKey 之间不需要再转换。
/// </summary>
public readonly record struct HotkeyGesture(uint Modifiers, uint VirtualKey)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    public const uint ModNoRepeat = 0x4000;

    public bool HasModifier =>
        (Modifiers & (ModAlt | ModControl | ModShift | ModWin)) != 0;

    /// <summary>
    /// 至少要有一个修饰键：否则 Ctrl+C 这种日常操作会被抢走。
    /// </summary>
    public bool IsValid => HasModifier && VirtualKey != 0;

    public override string ToString()
    {
        var parts = new List<string>(4);
        if ((Modifiers & ModControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((Modifiers & ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((Modifiers & ModShift) != 0)
        {
            parts.Add("Shift");
        }

        if ((Modifiers & ModWin) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(KeyName(VirtualKey));
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        uint modifiers = 0;
        uint virtualKey = 0;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win" or "windows":
                    modifiers |= ModWin;
                    break;
                default:
                    var key = VirtualKeyOf(raw);
                    if (key == 0)
                    {
                        return false;
                    }

                    virtualKey = key;
                    break;
            }
        }

        var parsed = new HotkeyGesture(modifiers, virtualKey);
        if (!parsed.IsValid)
        {
            return false;
        }

        gesture = parsed;
        return true;
    }

    /// <summary>从 WPF 的按键事件构造组合键；只按修饰键时返回 false。</summary>
    public static bool TryFromKeyboard(ModifierKeys modifiers, Key key, out HotkeyGesture gesture)
    {
        gesture = default;

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            return false;
        }

        // 单独按修饰键不算组合
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return false;
        }

        uint bits = 0;
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            bits |= ModControl;
        }

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            bits |= ModAlt;
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            bits |= ModShift;
        }

        if ((modifiers & ModifierKeys.Windows) != 0)
        {
            bits |= ModWin;
        }

        var candidate = new HotkeyGesture(bits, virtualKey);
        if (!candidate.IsValid)
        {
            return false;
        }

        gesture = candidate;
        return true;
    }

    private static string KeyName(uint virtualKey)
    {
        var key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        return key switch
        {
            Key.None => virtualKey.ToString(),
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            _ => key.ToString(),
        };
    }

    private static uint VirtualKeyOf(string name)
    {
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return c;
            }
        }

        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') &&
            int.TryParse(name[1..], out var number) && number is >= 1 and <= 24)
        {
            return (uint)(0x6F + number);
        }

        if (Enum.TryParse<Key>(name, true, out var key))
        {
            var virtualKey = KeyInterop.VirtualKeyFromKey(key);
            return (uint)Math.Max(0, virtualKey);
        }

        return 0;
    }
}
