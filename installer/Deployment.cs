using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace WEAM.Revit.AI.Setup;

static class Deployment
{
    public const string Version = "0.9.9";
    const string AddinId = "F0BB1CE2-6DA1-4F67-AB04-4C05F06397C4";
    const string RegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WEAM.Revit.AI";
    static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WEAM.Revit.AI");
    static readonly string App = Path.Combine(Data, "Application");
    static readonly string Release = Path.Combine(App, "versions", Version);
    static readonly string Manifest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", "2026", "WEAM.Revit.AI.addin");
    static readonly Encoding Utf8 = new UTF8Encoding(false);

    public static void Log(string message)
    {
        Directory.CreateDirectory(Data);
        File.AppendAllText(Path.Combine(Data, "setup.log"), DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine, Utf8);
    }

    public static string? FindRevit()
    {
        var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk", "Revit 2026");
        if (File.Exists(Path.Combine(standard, "Revit.exe"))) return standard;
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Autodesk\Revit\2026");
        foreach (var name in new[] { "InstallationLocation", "InstallLocation", "InstallPath" })
            if (key?.GetValue(name) is string location && File.Exists(Path.Combine(location, "Revit.exe"))) return location;
        return null;
    }

    static void CheckRevitClosed()
    {
        if (Process.GetProcessesByName("Revit").Length != 0)
            throw new InvalidOperationException("Сохраните проекты и закройте все окна Revit, затем повторите установку или удаление.");
    }

    public static string ReadLicense()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WEAM.Payload.zip")!;
        using var archive = new ZipArchive(stream);
        using var reader = new StreamReader(archive.GetEntry("LICENSE")!.Open());
        return reader.ReadToEnd();
    }

    public static void Install(bool codex, bool claude, bool auto, string? revitDir)
    {
        CheckRevitClosed();
        revitDir ??= FindRevit();
        if (revitDir is null || !File.Exists(Path.Combine(revitDir, "Revit.exe")) || !File.Exists(Path.Combine(revitDir, "RevitAPI.dll")))
            throw new InvalidOperationException("Не найден Revit 2026. Укажите его папку установки.");
        if (FileVersionInfo.GetVersionInfo(Path.Combine(revitDir, "RevitAPI.dll")).FileMajorPart != 26)
            throw new InvalidOperationException("Этот установщик поддерживает Revit 2026. Выбрана другая версия.");
        var machineManifest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "Revit", "Addins", "2026", "WEAM.Revit.AI.addin");
        if (File.Exists(machineManifest) && File.ReadAllText(machineManifest).Contains(AddinId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Обнаружена установка WEAM для всех пользователей. Удалите её перед установкой для текущего пользователя, чтобы не загрузить плагин дважды.");

        Directory.CreateDirectory(Release);
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WEAM.Payload.zip")!)
        using (var archive = new ZipArchive(stream))
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                var destination = Path.GetFullPath(Path.Combine(Release, entry.FullName));
                RequireChild(destination, Release);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open();
                using var memory = new MemoryStream(); input.CopyTo(memory);
                var bytes = memory.ToArray();
                // Same-version repair works even when a client still holds the identical node.exe.
                if (File.Exists(destination) && SHA256.HashData(File.ReadAllBytes(destination)).SequenceEqual(SHA256.HashData(bytes))) continue;
                File.WriteAllBytes(destination, bytes);
            }
        }
        RunNode(Release, "--examples");
        var clients = new List<string>();
        if (codex) clients.Add("--codex"); if (claude) clients.Add("--claude");
        if (clients.Count != 0) RunNode(Release, clients.ToArray());

        var assembly = SecurityElement.Escape(Path.Combine(Release, "plugin", "WEAM.Revit.AI.dll"));
        var xml = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><RevitAddIns><AddIn Type=\"Application\"><Name>WEAM.Revit.AI</Name><Assembly>{assembly}</Assembly><AddInId>{AddinId}</AddInId><FullClassName>WEAM.Revit.AI.PluginApplication</FullClassName><VendorId>WEAM</VendorId><VendorDescription>WEAM.Revit.AI local MCP bridge</VendorDescription></AddIn></RevitAddIns>";
        AtomicWithBackup(Manifest, xml);
        if (auto)
        {
            var settings = Path.Combine(Data, "settings.json");
            var json = File.Exists(settings) ? JsonNode.Parse(File.ReadAllText(settings)) as JsonObject : new JsonObject();
            if (json is null) throw new InvalidOperationException("Некорректный settings.json. Данные не будут перезаписаны.");
            json["autoConfirm"] = true;
            AtomicWithBackup(settings, json.ToJsonString());
        }
        var setupCopy = Path.Combine(App, "WEAM.Revit.AI.Setup.exe");
        if (!string.Equals(Environment.ProcessPath, setupCopy, StringComparison.OrdinalIgnoreCase)) File.Copy(Environment.ProcessPath!, setupCopy, true);
        using var uninstall = Registry.CurrentUser.CreateSubKey(RegistryKey);
        uninstall.SetValue("DisplayName", "WEAM.Revit.AI для Revit 2026");
        uninstall.SetValue("DisplayVersion", Version); uninstall.SetValue("Publisher", "WEAM");
        uninstall.SetValue("InstallLocation", App); uninstall.SetValue("DisplayIcon", setupCopy);
        uninstall.SetValue("UninstallString", '"' + setupCopy + "\" --uninstall");
        uninstall.SetValue("QuietUninstallString", '"' + setupCopy + "\" --uninstall --silent");
        uninstall.SetValue("NoModify", 1); uninstall.SetValue("NoRepair", 1);
        var state = new JsonObject { ["version"] = Version, ["release"] = Release, ["installedUtc"] = DateTimeOffset.UtcNow.ToString("O") };
        File.WriteAllText(Path.Combine(App, "installation.json"), state.ToJsonString(), Utf8);
        Log("Installed " + Version + "; MCP client configuration updated: " + string.Join(",", clients));
    }

    public static void Uninstall()
    {
        CheckRevitClosed();
        if (!File.Exists(Path.Combine(App, "installation.json"))) throw new InvalidOperationException("Не найдена установка WEAM.Revit.AI, управляемая установщиком.");
        foreach (var process in Process.GetProcessesByName("node"))
        {
            try
            {
                if (process.MainModule?.FileName?.StartsWith(App + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true)
                    throw new InvalidOperationException("Закройте AI-клиент, использующий MCP WEAM, перед удалением.");
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        var state = JsonNode.Parse(File.ReadAllText(Path.Combine(App, "installation.json")))!;
        var release = state["release"]!.GetValue<string>(); RequireChild(release, App);
        RunNode(release, "--remove", "--codex", "--claude");
        if (File.Exists(Manifest))
        {
            var xml = System.Xml.Linq.XDocument.Load(Manifest);
            var owns = xml.Descendants("AddIn").Any(a => string.Equals(a.Element("AddInId")?.Value, AddinId, StringComparison.OrdinalIgnoreCase)
                && a.Element("Assembly")?.Value.StartsWith(App + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true);
            if (owns) { Backup(Manifest); File.Delete(Manifest); }
        }
        Registry.CurrentUser.DeleteSubKeyTree(RegistryKey, false);
        RequireChild(App, Data);
        Directory.Delete(App, true);
        Log("Uninstalled; settings, snapshots, models and configuration backups preserved.");
    }

    static void RunNode(string release, params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(release, "node", "node.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        info.ArgumentList.Add(Path.Combine(release, "configure-mcp.mjs"));
        info.ArgumentList.Add("--backup-dir=" + Path.Combine(Data, "backups"));
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(); throw new TimeoutException("Не завершена настройка MCP."); }
        if (process.ExitCode != 0) throw new InvalidOperationException("Не удалось настроить MCP: " + stderr.GetAwaiter().GetResult());
        Log(stdout.GetAwaiter().GetResult().Trim());
    }

    static void RequireChild(string path, string root)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Путь выходит за пределы каталога приложения.");
    }

    static void Backup(string file)
    {
        if (!File.Exists(file)) return;
        var dir = Path.Combine(Data, "backups"); Directory.CreateDirectory(dir);
        File.Copy(file, Path.Combine(dir, Path.GetFileName(file) + "." + Guid.NewGuid().ToString("N") + ".bak"));
    }

    static void AtomicWithBackup(string file, string text)
    {
        if (File.Exists(file) && File.ReadAllText(file) == text) return;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!); Backup(file);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, text, Utf8); File.Move(temp, file, true);
    }
}
