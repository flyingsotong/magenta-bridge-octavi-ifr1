using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MagentaBridge.Core;

/// <summary>
/// Finds, reports and installs the FMS module: the small WASM package that lets this app call
/// the GNS530's own buttons. It ships inside the exe as an embedded resource, so installing it
/// is one button and never needs a download or a second program.
///
/// Deliberately plain: three files in one folder under the sim's Community folder. Uninstalling
/// is deleting that folder.
/// </summary>
public static class ModuleInstaller
{
    /// <summary>Package folder name inside Community.</summary>
    public const string FolderName = "magenta-bridge-module";

    /// <summary>File name inside the package's modules folder.</summary>
    public const string WasmFileName = "MagentaBridgeModule.wasm";

    /// <summary>Version of the module shipped in this build.</summary>
    public const string Version = "0.1.0";

    /// <summary>Package title, as the sim and the presence check see it.</summary>
    public const string Title = "Magenta Bridge Module";

    private const string ResourceName = "MagentaBridge.Modules.MagentaBridgeModule.wasm";

    /// <summary>
    /// Candidate Community folders when the sim's own config cannot be read. Kept short and
    /// in the order most likely to be right on this machine's layouts.
    /// </summary>
    private static readonly string[] FallbackRoots =
    {
        @"D:\MSFS Content",
        @"C:\MSFS Content",
        @"D:\Xbox\Microsoft Flight Simulator 2024",
        @"C:\Xbox\Microsoft Flight Simulator 2024",
        @"D:\XboxGames\Microsoft Flight Simulator 2024",
        @"C:\XboxGames\Microsoft Flight Simulator 2024",
        @"D:\XboxGames\Microsoft Flight Simulator",
        @"C:\XboxGames\Microsoft Flight Simulator",
    };

    /// <summary>Package families whose LocalCache\UserCfg.opt names the package folder.</summary>
    private static readonly string[] PackageFamilies =
    {
        "Microsoft.Limitless_8wekyb3d8bbwe",         // MSFS 2024
        "Microsoft.FlightSimulator_8wekyb3d8bbwe",   // MSFS 2020, Store
    };

    private static string? _cachedCommunity;

    /// <summary>The Community folder the sim actually reads, or null when none was found.</summary>
    public static string? CommunityFolder()
    {
        if (_cachedCommunity != null) return _cachedCommunity;

        foreach (var root in RootsFromSimConfig().Concat(FallbackRoots))
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            foreach (var name in new[] { "Community", "Community2024" })
            {
                var candidate = Path.Combine(root, name);
                if (Directory.Exists(candidate))
                {
                    _cachedCommunity = candidate;
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Reads InstalledPackagesPath out of the sim's own UserCfg.opt, if it is there.</summary>
    private static IEnumerable<string> RootsFromSimConfig()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var family in PackageFamilies)
        {
            string? root = null;
            var opt = Path.Combine(local, "Packages", family, "LocalCache", "UserCfg.opt");

            try
            {
                if (!File.Exists(opt)) continue;

                // One line matters: InstalledPackagesPath "D:\MSFS Content"
                foreach (var line in File.ReadLines(opt))
                {
                    if (!line.TrimStart().StartsWith("InstalledPackagesPath", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var match = Regex.Match(line, "\"([^\"]+)\"");
                    if (match.Success) root = match.Groups[1].Value;
                    break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"could not read the sim's UserCfg.opt: {ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(root)) yield return root!;
        }
    }

    /// <summary>Where this module would be installed, or null when no Community folder exists.</summary>
    public static string? InstallPath()
    {
        var community = CommunityFolder();
        return community == null ? null : Path.Combine(community, FolderName);
    }

    public readonly record struct Presence(bool Installed, string Status, string? Path);

    // The status is shown in the window and refreshed on every engine update, so reading the
    // Community folder each time would hit the disk dozens of times a second.
    private static Presence _cachedPresence;
    private static DateTime _cachedAt;
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(3);

    /// <summary>Drops the cached answer, after an install or a removal.</summary>
    public static void Invalidate()
    {
        _cachedAt = default;
    }

    /// <summary>
    /// Looks for an installed copy. Reports a stale one as stale rather than installed, so an
    /// upgrade is offered instead of silently skipped.
    /// </summary>
    public static Presence Check()
    {
        if (DateTime.UtcNow - _cachedAt < CacheFor) return _cachedPresence;

        _cachedPresence = CheckUncached();
        _cachedAt = DateTime.UtcNow;
        return _cachedPresence;
    }

    /// <summary>One line for the diagnostics panel: installed where, or what to do about it.</summary>
    public static string StatusLine()
    {
        var presence = Check();
        if (presence.Installed) return $"GPS add-on: installed and ready, at {presence.Path}";

        return presence.Path == null
            ? "GPS add-on: not installed, and the simulator's folder was not found - start MSFS once, then press Check"
            : "GPS add-on: not installed - the GNS530/430 buttons (FMS1/FMS2) will do nothing until it is";
    }

    private static Presence CheckUncached()
    {
        var folder = InstallPath();
        if (folder == null)
            return new Presence(false, "no Community folder found", null);

        var manifest = Path.Combine(folder, "manifest.json");
        var wasm = Path.Combine(folder, "modules", WasmFileName);

        if (!File.Exists(manifest) || !File.Exists(wasm))
            return new Presence(false, "not installed", folder);

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
            var version = doc.RootElement.TryGetProperty("package_version", out var v) ? v.GetString() : null;

            if (!string.Equals(title, Title, StringComparison.OrdinalIgnoreCase))
                return new Presence(false, "a different package is using that folder", folder);

            if (!string.Equals(version, Version, StringComparison.OrdinalIgnoreCase))
                return new Presence(false, $"installed {version}, this build carries {Version}", folder);

            return new Presence(true, $"installed {Version}", folder);
        }
        catch (Exception ex)
        {
            return new Presence(false, $"unreadable: {ex.Message}", folder);
        }
    }

    public static bool IsInstalled() => Check().Installed;

    /// <summary>
    /// True when this build carries the module bytes. False means the package was built
    /// without them, which the self-test reports rather than leaving to the first installer.
    /// </summary>
    public static bool HasEmbeddedModule =>
        Assembly.GetExecutingAssembly().GetManifestResourceInfo(ResourceName) != null;

    /// <summary>
    /// Writes the package. Returns null on success, or a sentence explaining what stopped it.
    /// The app is stopped and the sim may be running; both are fine, since only Community is
    /// touched and the sim reads it at load.
    /// </summary>
    public static string? Install()
    {
        var folder = InstallPath();
        if (folder == null)
            return "Could not find the simulator's Community folder. Start MSFS once, then try again.";

        try
        {
            var modules = Path.Combine(folder, "modules");
            Directory.CreateDirectory(modules);

            var wasmTarget = Path.Combine(modules, WasmFileName);
            if (!WriteResource(wasmTarget))
                return "This build has no module inside it, which means it was packaged wrong.";

            // Version and title are written from code so they can never drift from this build.
            var manifest = new Dictionary<string, object>
            {
                ["dependencies"] = Array.Empty<string>(),
                ["content_type"] = "MISC",
                ["title"] = Title,
                ["manufacturer"] = "",
                ["creator"] = "Magenta Bridge",
                ["package_version"] = Version,
                ["minimum_game_version"] = "1.19.8",
                ["release_notes"] = new Dictionary<string, object>
                {
                    ["neutral"] = new Dictionary<string, object>
                    {
                        ["LastUpdate"] = "Calls the simulator's own gauge events on request from Magenta Bridge.",
                    },
                },
            };

            var manifestPath = Path.Combine(folder, "manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest,
                new JsonSerializerOptions { WriteIndented = true }));

            WriteLayout(folder, manifestPath, wasmTarget);

            Log.Info($"FMS module installed to {folder}");
            _cachedCommunity = null;
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("could not install the FMS module", ex);
            return ex.Message;
        }
    }

    /// <summary>Removes the package. Returns null on success, or the reason it could not.</summary>
    public static string? Uninstall()
    {
        var folder = InstallPath();
        if (folder == null || !Directory.Exists(folder)) return null;

        try
        {
            Directory.Delete(folder, recursive: true);
            Log.Info($"FMS module removed from {folder}");
            Invalidate();
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("could not remove the FMS module", ex);
            return ex.Message;
        }
    }

    private static bool WriteResource(string target)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream == null)
        {
            Log.Error($"embedded resource {ResourceName} is missing; the module cannot be installed");
            return false;
        }

        using var file = File.Create(target);
        stream.CopyTo(file);
        return true;
    }

    /// <summary>
    /// layout.json tells the sim what is in the package and how big each file is. It lists
    /// itself, so it is written twice: once to learn its own size, once with that size in it.
    /// </summary>
    private static void WriteLayout(string folder, string manifestPath, string wasmPath)
    {
        var layoutPath = Path.Combine(folder, "layout.json");
        var selfSize = 0L;

        for (var pass = 0; pass < 3; pass++)
        {
            var files = new List<Dictionary<string, object>>
            {
                Entry("manifest.json", manifestPath),
                Entry($"modules/{WasmFileName}", wasmPath),
                new()
                {
                    ["path"] = "layout.json",
                    ["size"] = selfSize,
                    ["date"] = NowSeconds(),
                },
            };

            var json = JsonSerializer.Serialize(new Dictionary<string, object> { ["content"] = files },
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(layoutPath, json);

            var actual = new FileInfo(layoutPath).Length;
            if (actual == selfSize) break;
            selfSize = actual;
        }
    }

    private static Dictionary<string, object> Entry(string relative, string fullPath)
    {
        var info = new FileInfo(fullPath);
        return new Dictionary<string, object>
        {
            ["path"] = relative,
            ["size"] = info.Length,
            ["date"] = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(),
        };
    }

    private static long NowSeconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
