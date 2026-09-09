using System.Text.Json;
using System.Windows.Forms;

namespace Jerboa;

/// <summary>Everything the settings panel can change, stored under %APPDATA%\Jerboa.</summary>
public sealed class Settings
{
    public string Folder { get; set; } = DefaultFolder;
    public string? PlaybackDeviceId { get; set; }
    public string? MicrophoneDeviceId { get; set; }
    public bool ShortcutEnabled { get; set; } = true;
    public Keys ShortcutKey { get; set; } = Keys.R;
    public bool ShortcutControl { get; set; } = true;
    public bool ShortcutShift { get; set; } = true;
    public bool ShortcutAlt { get; set; }
    public bool AlwaysOnTop { get; set; }

    /// <summary>Minutes of recorded audio after which a recording ends by itself. 0 means never.</summary>
    public int AutoStopMinutes { get; set; }

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Recordings");

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jerboa", "settings.json");

    /// <summary>Derived from the parts above, so it is never written to the file.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ShortcutText
    {
        get
        {
            var parts = new List<string>();
            if (ShortcutControl) parts.Add("Ctrl");
            if (ShortcutShift) parts.Add("Shift");
            if (ShortcutAlt) parts.Add("Alt");
            parts.Add(ShortcutKey.ToString());
            return string.Join(" + ", parts);
        }
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* a damaged file falls back to defaults rather than blocking the app */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* never let a failed save interrupt a recording */ }
    }
}
