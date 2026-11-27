using Microsoft.Win32;

namespace Isle.Core;

/// <summary>"Start with Windows" via HKCU\...\Run (no admin needed).</summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Isle";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string s && s.Length > 0;
            }
            catch { return false; }
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                if (Environment.ProcessPath is not { } app) return;
                key.SetValue(ValueName, $"\"{app}\"");
            }
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) { Logger.Error("autostart", ex); }
    }
}
