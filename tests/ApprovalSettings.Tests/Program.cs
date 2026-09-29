using WEAM.Revit.AI;
using System.Text.Json;

var directory = Path.Combine(Path.GetTempPath(), "weam-settings-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var path = Path.Combine(directory, "settings.json");
var checks = 0;
void Check(bool success, string message)
{
    if (!success) throw new Exception(message);
    checks++;
}
try
{
    Check(!ApprovalSettings.ReadAutoConfirm(path), "Absent settings must require confirmation");
    foreach (var content in new[] { "{}", "null", "[]", "broken", "{\"autoConfirm\":\"true\"}", "{\"autoConfirm\":false}" })
    {
        File.WriteAllText(path, content);
        Check(!ApprovalSettings.ReadAutoConfirm(path), "Invalid or disabled settings must require confirmation: " + content);
    }
    File.WriteAllText(path, "{\"language\":\"ru\",\"autoConfirm\":false}");
    ApprovalSettings.WriteAutoConfirm(path, true);
    Check(ApprovalSettings.ReadAutoConfirm(path), "Enabling must take effect without restart");
    using (var json = JsonDocument.Parse(File.ReadAllText(path)))
        Check(json.RootElement.GetProperty("language").GetString() == "ru", "Preserve unrelated settings");
    ApprovalSettings.WriteAutoConfirm(path, false);
    Check(!ApprovalSettings.ReadAutoConfirm(path), "Disabling must take effect without restart");
    Check(Directory.GetFiles(directory).Length == 1, "Atomic writes must clean temporary files");
    Console.WriteLine($"PASS: {checks} approval settings checks");
}
finally
{
    File.Delete(path);
    Directory.Delete(directory);
}
