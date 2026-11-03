using Microsoft.Win32;
using Xunit;

namespace MaraxTray.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "MaraxTrayTests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(folder, "sub", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void CreatesTheFileWithDefaults()
    {
        TraySettings settings = TraySettings.Load(SettingsPath);

        Assert.Equal(4210, settings.Port);
        Assert.Equal(90, settings.ReadyHxTemp);
        Assert.Contains("\"readyHxTemp\": 90", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void ReadsChangedValuesAndKeepsTheOthers()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{ "readyHxTemp": 86 }""");

        TraySettings settings = TraySettings.Load(SettingsPath);

        Assert.Equal(86, settings.ReadyHxTemp);
        Assert.Equal(4210, settings.Port);
    }

    [Fact]
    public void BrokenFileFallsBackToDefaultsAndIsNotOverwritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "{ not json");

        TraySettings settings = TraySettings.Load(SettingsPath);

        Assert.Equal(4210, settings.Port);
        Assert.Equal("{ not json", File.ReadAllText(SettingsPath));
    }
}

public sealed class AutostartTests : IDisposable
{
    private readonly string keyPath = $@"Software\MaraxTrayTests\{Guid.NewGuid():N}";

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\MaraxTrayTests", throwOnMissingSubKey: false);
    }

    [Fact]
    public void IsOffByDefault()
    {
        Assert.False(new Autostart(keyPath, @"C:\a\tray.exe").Enabled);
    }

    [Fact]
    public void CanBeSwitchedOnAndOff()
    {
        var autostart = new Autostart(keyPath, @"C:\a b\tray.exe");

        autostart.Enabled = true;
        Assert.True(autostart.Enabled);
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath)!)
        {
            Assert.Equal("\"C:\\a b\\tray.exe\"", key.GetValue("MaraxTray"));
        }

        autostart.Enabled = false;
        Assert.False(autostart.Enabled);
    }

    [Fact]
    public void AnOldPathDoesNotCountAsEnabled()
    {
        new Autostart(keyPath, @"C:\old\tray.exe").Enabled = true;

        Assert.False(new Autostart(keyPath, @"C:\new\tray.exe").Enabled);
    }
}
