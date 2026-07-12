using Microsoft.Win32;

namespace DeskFader.Core;

public static class StartupRegistrar
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeskFader";

    public static void SetStartAtLogin(bool enabled, string? applicationPath = null)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? throw new InvalidOperationException("could not open the Windows Run key");
        if (enabled)
        {
            applicationPath ??= Path.Combine(AppContext.BaseDirectory, "DeskFader.Settings.exe");
            if (!File.Exists(applicationPath)) throw new FileNotFoundException("DeskFader Settings executable was not found", applicationPath);
            key.SetValue(ValueName, $"\"{applicationPath}\"", RegistryValueKind.String);
        }
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
