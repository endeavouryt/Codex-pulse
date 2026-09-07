using Microsoft.Win32;
using System.IO;

namespace CodexPulse.Services;

internal static class StartupSettings
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "CodexPulse";

    // The registry is the source of truth; launching Pulse never registers it.
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(RunValueName) is string value && !string.IsNullOrWhiteSpace(value);
            }
            catch { return false; }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            existing?.DeleteValue(RunValueName, false);
            return;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) ||
            string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            executable = Path.Combine(AppContext.BaseDirectory, "CodexPulse.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("未找到 CodexPulse.exe。", executable);
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath)
            ?? throw new IOException("无法打开开机启动注册项。");
        key.SetValue(RunValueName, $"\"{executable}\"", RegistryValueKind.String);
    }
}
