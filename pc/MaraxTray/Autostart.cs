using Microsoft.Win32;

namespace MaraxTray;

internal sealed class Autostart(string runKeyPath, string executablePath)
{
    public const string WindowsRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string ValueName = "MaraxTray";

    private string Command => $"\"{executablePath}\"";

    public bool Enabled
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(runKeyPath);
            return key?.GetValue(ValueName) as string == Command;
        }
        set
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(runKeyPath);
            if (value)
            {
                key.SetValue(ValueName, Command);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
    }
}
