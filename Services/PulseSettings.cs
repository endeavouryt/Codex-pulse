using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexPulse.Services;

internal sealed class PulseSettings
{
    private readonly string _path;
    public bool ShowFiveHour { get; private set; } = true;

    public PulseSettings(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexPulse", "settings.json");
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(_path));
            ShowFiveHour = json?["showFiveHour"]?.GetValue<bool>() ?? true;
        }
        catch { /* Missing or malformed settings use the display default. */ }
    }

    public void SetShowFiveHour(bool enabled)
    {
        JsonObject? json = null;
        try { json = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject; }
        catch { /* Replace an unreadable optional configuration. */ }
        json ??= new JsonObject();
        json["showFiveHour"] = enabled;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, true);
        ShowFiveHour = enabled;
    }
}
