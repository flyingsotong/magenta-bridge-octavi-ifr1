using System.IO;

namespace MagentaBridge.Core;

/// <summary>
/// Console form of the module installer, so the same code path the button uses can be run,
/// inspected and verified without opening the window.
/// </summary>
public static class ModuleHarness
{
    public static int Run(string action, string outputPath)
    {
        var lines = new List<string>();
        var code = 0;
        var verb = (action ?? "status").Trim().ToLowerInvariant();

        var presence = ModuleInstaller.Check();
        var community = ModuleInstaller.CommunityFolder();
        var installPath = ModuleInstaller.InstallPath();

        lines.Add($"action          : {verb}");
        lines.Add($"community       : {community ?? "(not found)"}");
        lines.Add($"install path    : {installPath ?? "(not found)"}");
        lines.Add($"installed       : {presence.Installed}");
        lines.Add($"status          : {presence.Status}");
        lines.Add($"module in exe   : {(ModuleInstaller.HasEmbeddedModule ? "yes" : "NO - this build would install nothing")}");
        lines.Add("");

        switch (verb)
        {
            case "install":
            {
                var error = ModuleInstaller.Install();
                if (error == null)
                {
                    presence = ModuleInstaller.Check();
                    lines.Add($"installed       : {presence.Installed}");
                    lines.Add($"status          : {presence.Status}");
                    lines.Add($"path            : {presence.Path}");
                }
                else
                {
                    lines.Add($"error           : {error}");
                    code = 1;
                }
                break;
            }

            case "remove":
            {
                var error = ModuleInstaller.Uninstall();
                lines.Add(error == null ? "removed" : $"error           : {error}");
                if (error != null) code = 1;
                break;
            }

            case "status":
                break;

            default:
                lines.Add($"unknown action \"{verb}\"; use status, install or remove");
                code = 2;
                break;
        }

        lines.Add("");
        lines.Add("Files in the package:");
        if (installPath != null && Directory.Exists(installPath))
        {
            foreach (var file in Directory.GetFiles(installPath, "*", SearchOption.AllDirectories))
                lines.Add($"  {file}  ({new FileInfo(file).Length} bytes)");
        }
        else
        {
            lines.Add("  (nothing installed)");
        }

        try { File.WriteAllText(outputPath, string.Join(Environment.NewLine, lines) + Environment.NewLine); }
        catch (Exception ex) { Log.Warn($"could not write {outputPath}: {ex.Message}"); }

        foreach (var line in lines) Console.WriteLine(line);
        return code;
    }
}
