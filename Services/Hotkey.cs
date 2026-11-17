using Avalonia.Input;
using Clipt.Platform;

namespace Clipt.Services;

/// <summary>A global shortcut such as "Alt+Shift+V". Registered by the platform layer.</summary>
public readonly record struct Hotkey(KeyModifiers Modifiers, Key Key)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Meta)) parts.Add(PlatformDefaults.SuperKeyName);
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    public static string KeyName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (k - Key.D0))).ToString(),
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.Oem3 => "`",
        Key.Oem5 => "\\",
        _ => k.ToString(),
    };

    /// <summary>Parses "Ctrl+Alt+V". "Win", "Super" and "Meta" all mean the logo key, so settings move between OSes.</summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = KeyModifiers.None;
        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= KeyModifiers.Control; break;
                case "alt": mods |= KeyModifiers.Alt; break;
                case "shift": mods |= KeyModifiers.Shift; break;
                case "win" or "windows" or "super" or "meta": mods |= KeyModifiers.Meta; break;
                default:
                    var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    name = name switch
                    {
                        "," => "OemComma", "." => "OemPeriod", "/" => "OemQuestion", ";" => "OemSemicolon", "`" => "Oem3",
                        "'" => "OemQuotes", "[" => "OemOpenBrackets", "]" => "OemCloseBrackets", "-" => "OemMinus",
                        "=" => "OemPlus", "\\" => "Oem5", _ => name,
                    };
                    if (Enum.TryParse<Key>(name, true, out var k) && !int.TryParse(name, out _)) key = k; else return false;
                    break;
            }
        }
        if (key is null || mods == KeyModifiers.None) return false;
        hotkey = new Hotkey(mods, key.Value);
        return true;
    }
}
