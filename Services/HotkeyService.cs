using System.Windows.Input;
using Clipt.Native;

namespace Clipt.Services;

public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
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

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = ModifierKeys.None;
        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win" or "windows": mods |= ModifierKeys.Windows; break;
                default:
                    var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    name = name switch { "," => "OemComma", "." => "OemPeriod", "/" => "OemQuestion", ";" => "OemSemicolon", "`" => "Oem3", _ => name };
                    if (Enum.TryParse<Key>(name, true, out var k)) key = k; else return false;
                    break;
            }
        }
        if (key is null || mods == ModifierKeys.None) return false;
        hotkey = new Hotkey(mods, key.Value);
        return true;
    }
}

/// <summary>Registers the system-wide launcher hotkey via RegisterHotKey.</summary>
public sealed class HotkeyService
{
    private const int Id = 0xC11;
    private readonly IntPtr _hwnd;
    private bool _registered;

    public HotkeyService(IntPtr messageWindow) => _hwnd = messageWindow;

    public bool Register(Hotkey hk)
    {
        Unregister();
        uint mods = Win32.MOD_NOREPEAT;
        if (hk.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= Win32.MOD_ALT;
        if (hk.Modifiers.HasFlag(ModifierKeys.Control)) mods |= Win32.MOD_CONTROL;
        if (hk.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= Win32.MOD_SHIFT;
        if (hk.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= Win32.MOD_WIN;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(hk.Key);
        _registered = Win32.RegisterHotKey(_hwnd, Id, mods, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered) Win32.UnregisterHotKey(_hwnd, Id);
        _registered = false;
    }
}
