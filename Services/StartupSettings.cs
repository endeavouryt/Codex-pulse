using Microsoft.Win32;
using System.IO;
using System.Text.Json;

namespace CodexPulse.Services;

internal sealed class StartupSettings
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "CodexPulse";
    private readonly string _configPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexPulse",
        "settings.json");

    public bool StartWithWindows { get; private set; }

    public StartupSettings()
    {
        StartWithWindows = ReadConfiguredValue();
    }

    public void EnsureApplied()
    {
        ApplyRegistry(StartWithWindows);
    }

    // This is the future settings hook. No settings panel is needed for the MVP.
    public void SetStartWithWindows(bool enabled)
    {
        StartWithWindows = enabled;
        try
        {
            var directory = Path.GetDirectoryName(_configPath);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                _configPath,
                JsonSerializer.Serialize(
                    new StartupConfig { StartWithWindows = enabled },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    }));
        }
        catch
        {
            // Registry registration below remains best-effort.
        }

        ApplyRegistry(enabled);
    }

    private bool ReadConfiguredValue()
    {
        var environmentValue = Environment.GetEnvironmentVariable("CODEX_PULSE_AUTOSTART");
        if (bool.TryParse(environmentValue, out var environmentEnabled))
        {
            return environmentEnabled;
        }

        if (string.Equals(environmentValue, "1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(environmentValue, "0", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            if (File.Exists(_configPath))
            {
                var config = JsonSerializer.Deserialize<StartupConfig>(
                    File.ReadAllText(_configPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (config is not null)
                {
                    return config.StartWithWindows;
                }
            }
        }
        catch
        {
            // A malformed optional config falls back to the default.
        }

        return true;
    }

    private static void ApplyRegistry(bool enabled)
    {
        try
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (runKey is null)
            {
                return;
            }

            if (!enabled)
            {
                runKey.DeleteValue(RunValueName, false);
                return;
            }

            var executable = ResolveExecutablePath();
            if (executable is null)
            {
                return;
            }

            runKey.SetValue(RunValueName, $"\"{executable}\"", RegistryValueKind.String);
        }
        catch
        {
            // Startup registration must not prevent the widget from opening.
        }
    }

    private static string? ResolveExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            File.Exists(processPath) &&
            !string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        var appHostPath = Path.Combine(AppContext.BaseDirectory, "CodexPulse.exe");
        return File.Exists(appHostPath) ? appHostPath : null;
    }

    private sealed class StartupConfig
    {
        public bool StartWithWindows { get; set; } = true;
    }
}
