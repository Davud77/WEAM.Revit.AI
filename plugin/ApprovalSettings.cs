using System.Text.Json;
using System.Text.Json.Nodes;

namespace WEAM.Revit.AI;

internal static class ApprovalSettings
{
    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WEAM.Revit.AI", "settings.json");

    // Read for each operation so switching mode never requires a restart.
    public static bool AutoConfirm => ReadAutoConfirm(SettingsPath);

    internal static bool ReadAutoConfirm(string path)
    {
        try
        {
            using var settings = JsonDocument.Parse(File.ReadAllText(path));
            return settings.RootElement.ValueKind == JsonValueKind.Object
                && settings.RootElement.TryGetProperty("autoConfirm", out var flag)
                && flag.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public static void SetAutoConfirm(bool enabled) => WriteAutoConfirm(SettingsPath, enabled);

    internal static void WriteAutoConfirm(string path, bool enabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var settings = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
        settings["autoConfirm"] = enabled;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
