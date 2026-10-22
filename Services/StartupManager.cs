using System;
using Microsoft.Win32;

namespace TranslucentTodo.Services
{
    /// <summary>Registers the app under HKCU\...\Run so it launches at sign-in.</summary>
    public static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "TranslucentTodo";

        private static string Command => $"\"{Environment.ProcessPath}\"";

        public static void Apply(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                if (enabled)
                {
                    if (!string.Equals(key.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase))
                        key.SetValue(ValueName, Command);
                }
                else if (key.GetValue(ValueName) != null)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
            catch (Exception)
            {
                // Registry access can be blocked by policy; the app still works without autostart.
            }
        }
    }
}
