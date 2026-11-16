using Microsoft.Win32;

namespace Clipt.Services;

/// <summary>Per-user "start with Windows" via the HKCU Run key (no admin rights needed).</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Clipt";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;
        var exe = Environment.ProcessPath;
        if (enabled && exe is not null) key.SetValue(Name, $"\"{exe}\" --background");
        else if (key.GetValue(Name) is not null) key.DeleteValue(Name, false);
    }
}
