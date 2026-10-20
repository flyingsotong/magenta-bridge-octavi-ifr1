using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MagentaBridge.Core;

/// <summary>
/// What one physical button does. <see cref="Events"/> are SimConnect client event
/// names transmitted in order on the rising edge. An empty or null list means the
/// button does nothing — the original instead transmitted an event mapped to the
/// empty string, which is defect 4 in the teardown.
/// </summary>
public sealed class ButtonBinding
{
    public string[]? Events { get; set; }
    public bool TogglesShift { get; set; }
}

/// <summary>
/// One dial (encoder). Either a pair of increment/decrement events, or an
/// <see cref="Absolute"/> action that reads a SimVar and writes back an absolute value
/// (the original did this for heading, altitude, OBS and squawk).
/// </summary>
public sealed class DialBinding
{
    /// <summary>Events fired per detent clockwise. Several = several events per click.</summary>
    public string[]? Inc { get; set; }
    /// <summary>Events fired per detent anticlockwise.</summary>
    public string[]? Dec { get; set; }

    /// <summary>
    /// Built-in absolute action: heading, altitude, obs1, obs2, squawk, nav1, nav2.
    /// When set, Inc/Dec are ignored.
    /// </summary>
    public string? Absolute { get; set; }

    /// <summary>Units per detent for absolute actions (degrees, feet, or 1 for whole digits).</summary>
    public double Step { get; set; } = 1;

    /// <summary>dwData passed with Inc/Dec events.</summary>
    public int Data { get; set; } = 1;

    /// <summary>How much the second detent byte contributes, for absolute actions.</summary>
    public double FineStep { get; set; } = 1;
}

public sealed class ModeBinding
{
    public string Label { get; set; } = "";
    public DialBinding? Dial1 { get; set; }
    public DialBinding? Dial2 { get; set; }
    public DialBinding? ShiftDial1 { get; set; }
    public DialBinding? ShiftDial2 { get; set; }
    /// <summary>Per-mode button overrides, keyed by hex bit such as "0x4000".</summary>
    public Dictionary<string, ButtonBinding> Buttons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DeviceOptions
{
    /// <summary>Null means "first IFR-1 found". Set to a serial to pin one unit.</summary>
    public string? Serial { get; set; }
    public int VendorId { get; set; } = 0x04D8;
    public int ProductId { get; set; } = 0xE6D6;
}

public sealed class LogOptions
{
    public string Level { get; set; } = "Info";
    public long MaxBytes { get; set; } = 5L * 1024 * 1024;
    public int MaxFiles { get; set; } = 5;
}

public sealed class SimConnectOptions
{
    /// <summary>
    /// Optional explicit path to a SimConnect client DLL. When null the app probes the
    /// sim install, the SDK and its own folder, in that order, instead of trusting a
    /// five-year-old bundled copy (teardown defect 17).
    /// </summary>
    public string? ClientDllPath { get; set; }
    public int ReconnectDelayMs { get; set; } = 2000;
    /// <summary>Label the sim shows for this app.</summary>
    public string ClientName { get; set; } = ProductInfo.SimClientName;
}

public sealed class AppConfig
{
    /// <summary>
    /// Bumped whenever the built-in bindings change in a way a stale file would not pick up.
    /// A config written by an older build is archived and regenerated rather than half-merged,
    /// which is what previously left mode 7 without its transponder-mode shift binding.
    /// </summary>
    public const int CurrentVersion = 6;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Start with Windows (per-user Run key), minimised to the tray.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>"system" follows Windows; "dark" or "light" pin the palette.</summary>
    public string Theme { get; set; } = "system";

    /// <summary>True once the first-run welcome card has been dismissed.</summary>
    public bool WelcomeDismissed { get; set; }

    /// <summary>Keep the window above other windows, useful when flying windowed.</summary>
    public bool AlwaysOnTop { get; set; }
    public DeviceOptions Device { get; set; } = new();
    public LogOptions Logging { get; set; } = new();
    public SimConnectOptions SimConnect { get; set; } = new();

    /// <summary>Global button bindings, keyed by hex bit such as "0x4000".</summary>
    public Dictionary<string, ButtonBinding> Buttons { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mode table, keyed "0".."7".</summary>
    public Dictionary<string, ModeBinding> Modes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public string Path { get; private set; } = "";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DefaultDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ProductInfo.FamilyFolder, ProductInfo.DeviceFolder);

    public static string DefaultPath => System.IO.Path.Combine(DefaultDirectory, "config.json");

    /// <summary>
    /// Where this product's config lived before the name changed, newest first. Each product used
    /// a different path at some point: %APPDATA%\OctaviBridge, then %APPDATA%\MagentaBridge, and
    /// now the per-device folder. Copy the first one found forward, so a rename or split never
    /// silently resets bindings, theme or the startup preference.
    /// </summary>
    /// <summary>True when this run adopted a config from the pre-rename folder.</summary>
    public static bool AdoptedLegacyConfig { get; private set; }

    /// <summary>Where the adopted config came from, for the log line.</summary>
    public static string? AdoptedFrom { get; private set; }

    private static IEnumerable<string> LegacyConfigPaths()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return System.IO.Path.Combine(appData, ProductInfo.FamilyFolder, "config.json");
        yield return System.IO.Path.Combine(appData, "OctaviBridge", "config.json");
    }

    private static void AdoptLegacyFolder(string target)
    {
        try
        {
            if (File.Exists(target)) return;

            var legacy = LegacyConfigPaths().FirstOrDefault(File.Exists);
            if (legacy == null) return;

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(legacy, target);
            AdoptedLegacyConfig = true;   // Log is not listening yet here; the caller reports it
            AdoptedFrom = legacy;
        }
        catch (Exception ex)
        {
            // A failed migration must not stop the app; a fresh config is still a working app.
            Log.Warn($"could not adopt the old config: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads the config, creating a fully populated default one on first run. The written
    /// file lists every binding the app uses, so it doubles as the reference for remapping.
    /// </summary>
    public static AppConfig LoadOrCreate(string? path = null)
    {
        var target = path ?? DefaultPath;
        if (path == null) AdoptLegacyFolder(target);

        if (File.Exists(target))
        {
            try
            {
                var text = File.ReadAllText(target);
                var loaded = JsonSerializer.Deserialize<AppConfig>(text, ReadOptions);
                if (loaded != null)
                {
                    loaded.Path = target;

                    if (loaded.Version < CurrentVersion)
                    {
                        // A newer build has bindings this file cannot know about. Archive it so the
                        // user can diff, then fall through to a freshly generated default.
                        try { File.Copy(target, target + $".v{loaded.Version}.bak", overwrite: true); }
                        catch { /* best effort */ }

                        Log.Warn($"config version {loaded.Version} is older than {CurrentVersion}; " +
                                 "archived and regenerated with current defaults");
                    }
                    else
                    {
                        loaded.MergeWithDefaults();

                        // The client name is what MSFS lists in its own logs. A config written
                        // before the rename would keep announcing the old name forever.
                        if (loaded.SimConnect.ClientName is "Octavi Bridge" or "Magenta Bridge"
                            or "Octavi IFR-1 Bridge")
                        {
                            loaded.SimConnect.ClientName = ProductInfo.SimClientName;
                            try { loaded.Save(); }   // persist, or the file keeps the old name
                            catch (Exception ex) { Log.Warn($"could not save the config: {ex.Message}"); }
                        }

                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                // A broken config must not brick the app: fall back to defaults and keep
                // the bad file beside it so the user can diff it.
                try
                {
                    File.Copy(target, target + ".invalid", overwrite: true);
                }
                catch { /* best effort */ }

                Log.Error($"config at {target} could not be read; using defaults", ex);
            }
        }

        var created = CreateDefault();
        created.Path = target;
        try { created.Save(); }
        catch (Exception ex) { Log.Error("could not write default config", ex); }
        return created;
    }

    public void Save(string? path = null)
    {
        var target = path ?? (Path.Length > 0 ? Path : DefaultPath);
        var dir = System.IO.Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        File.WriteAllText(target, JsonSerializer.Serialize(this, WriteOptions));
    }

    public ButtonBinding? FindButton(int mode, uint bit)
    {
        var key = $"0x{bit:X}";
        if (Modes.TryGetValue(mode.ToString(), out var modeBinding) &&
            modeBinding.Buttons.TryGetValue(key, out var perMode))
        {
            return perMode;
        }
        return Buttons.TryGetValue(key, out var global) ? global : null;
    }

    public ModeBinding Mode(int mode)
    {
        var key = mode.ToString();
        if (Modes.TryGetValue(key, out var existing)) return existing;
        return new ModeBinding { Label = $"Mode {mode}" };
    }

    /// <summary>Fills in any section missing from a hand-edited file.</summary>
    private void MergeWithDefaults()
    {
        var defaults = CreateDefault();
        if (Buttons.Count == 0) Buttons = defaults.Buttons;
        if (Modes.Count == 0) Modes = defaults.Modes;
        foreach (var (key, mode) in defaults.Modes)
        {
            if (!Modes.ContainsKey(key)) Modes[key] = mode;
        }
        Device ??= new DeviceOptions();
        Logging ??= new LogOptions();
        SimConnect ??= new SimConnectOptions();
    }

    private static ButtonBinding B(params string[] events) => new() { Events = events };

    private static DialBinding IncDec(string inc, string dec, int data = 1) => new()
    {
        Inc = new[] { inc },
        Dec = new[] { dec },
        Data = data,
    };

    private static DialBinding Paired(string inc1, string inc2, string dec1, string dec2) => new()
    {
        Inc = new[] { inc1, inc2 },
        Dec = new[] { dec1, dec2 },
        Data = 1,
    };

    private static DialBinding Abs(string action, double step, double fineStep) => new()
    {
        Absolute = action,
        Step = step,
        FineStep = fineStep,
    };

    /// <summary>
    /// The original plug-in's behaviour, expressed as data, with the teardown's fixes
    /// applied: no event mapped to the empty string (defect 4) and the fourth softkey
    /// wired to ENT (defect 5).
    /// </summary>
    public static AppConfig CreateDefault()
    {
        var cfg = new AppConfig();

        cfg.Buttons = new Dictionary<string, ButtonBinding>(StringComparer.OrdinalIgnoreCase)
        {
            // The six large buttons: autopilot modes on every non-GPS mode.
            ["0x4000"] = B("AP_MASTER"),
            ["0x8000"] = B("AP_PANEL_HEADING_HOLD"),
            ["0x10000"] = B("AP_NAV1_HOLD"),
            ["0x20000"] = B("AP_APR_HOLD"),
            ["0x40000"] = B("AP_PANEL_ALTITUDE_HOLD"),
            ["0x80000"] = B("AP_PANEL_VS_HOLD"),

            // Swap button.
            ["0x100"] = B("COM_STBY_RADIO_SWAP"),

            // Shift / transfer.
            ["0x200"] = new ButtonBinding { TogglesShift = true },
        };

        cfg.Modes = new Dictionary<string, ModeBinding>(StringComparer.OrdinalIgnoreCase);

        // Mode 0 — COM1
        cfg.Modes["0"] = new ModeBinding
        {
            Label = "COM1",
            Dial1 = IncDec("COM_RADIO_WHOLE_INC", "COM_RADIO_WHOLE_DEC"),
            Dial2 = IncDec("COM_RADIO_FRACT_INC", "COM_RADIO_FRACT_DEC"),
            // Manual 3.3: COM1 -> HDG. Both knobs drive the heading bug (lower 10, upper 1),
            // so there is no separate shift binding for the second knob.
            ShiftDial1 = Abs("heading", 10, 1),
            ShiftDial2 = null,
            Buttons = { ["0x100"] = B("COM_STBY_RADIO_SWAP"), ["0x200"] = new ButtonBinding { TogglesShift = true } },
        };

        // Mode 1 — COM2
        cfg.Modes["1"] = new ModeBinding
        {
            Label = "COM2",
            Dial1 = IncDec("COM2_RADIO_WHOLE_INC", "COM2_RADIO_WHOLE_DEC"),
            Dial2 = IncDec("COM2_RADIO_FRACT_INC", "COM2_RADIO_FRACT_DEC"),
            // Manual 3.3: COM2 -> BARO. Both knobs step the barometer; no heading here.
            ShiftDial1 = IncDec("KOHLSMAN_INC", "KOHLSMAN_DEC"),
            ShiftDial2 = IncDec("KOHLSMAN_INC", "KOHLSMAN_DEC"),
            Buttons =
            {
                ["0x100"] = B("COM2_RADIO_SWAP"),
                ["0x200"] = new ButtonBinding { TogglesShift = true },
            },
        };

        // Mode 2 — NAV1 (frequency set + OBS on shift)
        cfg.Modes["2"] = new ModeBinding
        {
            Label = "NAV1",
            Dial1 = new DialBinding { Absolute = "nav1", Step = 1, FineStep = 0.05 },
            Dial2 = new DialBinding { Absolute = "nav1", Step = 1, FineStep = 0.05 },
            ShiftDial1 = Abs("obs1", 10, 1),
            ShiftDial2 = Abs("obs1", 10, 1),
            Buttons = { ["0x100"] = B("NAV1_RADIO_SWAP"), ["0x200"] = new ButtonBinding { TogglesShift = true } },
        };

        // Mode 3 — NAV2
        cfg.Modes["3"] = new ModeBinding
        {
            Label = "NAV2",
            Dial1 = new DialBinding { Absolute = "nav2", Step = 1, FineStep = 0.05 },
            Dial2 = new DialBinding { Absolute = "nav2", Step = 1, FineStep = 0.05 },
            ShiftDial1 = Abs("obs2", 10, 1),
            ShiftDial2 = Abs("obs2", 10, 1),
            Buttons = { ["0x100"] = B("NAV2_RADIO_SWAP"), ["0x200"] = new ButtonBinding { TogglesShift = true } },
        };

        // Mode 4 — FMS1 (GNS530 / PFD). Manual: 6 lower buttons are CDI - PROC.
        cfg.Modes["4"] = new ModeBinding
        {
            Label = "FMS1",
            Dial1 = Paired("MobiFlight.AS530_RightLargeKnob_Right", "MobiFlight.AS1000_PFD_FMS_Lower_INC",
                           "MobiFlight.AS530_RightLargeKnob_Left", "MobiFlight.AS1000_PFD_FMS_Lower_DEC"),
            Dial2 = Paired("MobiFlight.AS530_RightSmallKnob_Right", "MobiFlight.AS1000_PFD_FMS_Upper_INC",
                           "MobiFlight.AS530_RightSmallKnob_Left", "MobiFlight.AS1000_PFD_FMS_Upper_DEC"),
            ShiftDial1 = Paired("MobiFlight.AS530_RNG_Zoom", "MobiFlight.AS1000_PFD_RANGE_INC",
                                "MobiFlight.AS530_RNG_Dezoom", "MobiFlight.AS1000_PFD_RANGE_DEC"),
            ShiftDial2 = Paired("MobiFlight.AS530_RNG_Zoom", "MobiFlight.AS1000_PFD_RANGE_INC",
                                "MobiFlight.AS530_RNG_Dezoom", "MobiFlight.AS1000_PFD_RANGE_DEC"),
            Buttons =
            {
                ["0x10"] = B("MobiFlight.AS530_DirectTo_Push", "MobiFlight.AS1000_PFD_DIRECTTO"),
                ["0x20"] = B("MobiFlight.AS530_MENU_Push", "MobiFlight.AS1000_PFD_MENU_Push"),
                ["0x40"] = B("MobiFlight.AS530_CLR_Push", "MobiFlight.AS1000_PFD_CLR"),
                ["0x80"] = B("MobiFlight.AS530_ENT_Push", "MobiFlight.AS1000_PFD_ENT_Push"),  // was dead
                ["0x200"] = B("MobiFlight.AS530_RightSmallKnob_Push", "MobiFlight.AS1000_PFD_FMS_Upper_PUSH"),
                ["0x4000"] = B("TOGGLE_GPS_DRIVES_NAV1"),
                ["0x8000"] = B("MobiFlight.AS530_OBS_Push"),
                ["0x10000"] = B("MobiFlight.AS530_MSG_Push"),
                ["0x20000"] = B("MobiFlight.AS530_FPL_Push", "MobiFlight.AS1000_PFD_FPL_Push"),
                ["0x40000"] = B("MobiFlight.AS530_VNAV_Push"),
                ["0x80000"] = B("MobiFlight.AS530_PROC_Push", "MobiFlight.AS1000_PFD_PROC_Push"),
            },
        };

        // Mode 5 — FMS2 (GNS430 / MFD)
        cfg.Modes["5"] = new ModeBinding
        {
            Label = "FMS2",
            Dial1 = Paired("MobiFlight.AS430_RightLargeKnob_Right", "MobiFlight.AS1000_MFD_FMS_Lower_INC",
                           "MobiFlight.AS430_RightLargeKnob_Left", "MobiFlight.AS1000_MFD_FMS_Lower_DEC"),
            Dial2 = Paired("MobiFlight.AS430_RightSmallKnob_Right", "MobiFlight.AS1000_MFD_FMS_Upper_INC",
                           "MobiFlight.AS430_RightSmallKnob_Left", "MobiFlight.AS1000_MFD_FMS_Upper_DEC"),
            ShiftDial1 = Paired("MobiFlight.AS430_RNG_Zoom", "MobiFlight.AS1000_MFD_RANGE_INC",
                                "MobiFlight.AS430_RNG_Dezoom", "MobiFlight.AS1000_MFD_RANGE_DEC"),
            ShiftDial2 = Paired("MobiFlight.AS430_RNG_Zoom", "MobiFlight.AS1000_MFD_RANGE_INC",
                                "MobiFlight.AS430_RNG_Dezoom", "MobiFlight.AS1000_MFD_RANGE_DEC"),
            Buttons =
            {
                ["0x10"] = B("MobiFlight.AS430_DirectTo_Push", "MobiFlight.AS1000_MFD_DIRECTTO"),
                ["0x20"] = B("MobiFlight.AS430_MENU_Push", "MobiFlight.AS1000_MFD_MENU_Push"),
                ["0x40"] = B("MobiFlight.AS430_CLR_Push", "MobiFlight.AS1000_MFD_CLR"),
                ["0x80"] = B("MobiFlight.AS430_ENT_Push", "MobiFlight.AS1000_MFD_ENT_Push"),  // was dead
                ["0x200"] = B("MobiFlight.AS430_RightSmallKnob_Push", "MobiFlight.AS1000_MFD_FMS_Upper_PUSH"),
                ["0x4000"] = B("TOGGLE_GPS_DRIVES_NAV1"),
                ["0x8000"] = B("MobiFlight.AS430_OBS_Push"),
                ["0x10000"] = B("MobiFlight.AS430_MSG_Push"),
                ["0x20000"] = B("MobiFlight.AS430_FPL_Push", "MobiFlight.AS1000_MFD_FPL_Push"),
                ["0x40000"] = B("MobiFlight.AS430_VNAV_Push"),
                ["0x80000"] = B("MobiFlight.AS430_PROC_Push", "MobiFlight.AS1000_MFD_PROC_Push"),
            },
        };

        // Mode 6 — AP (manual: lower knob = target altitude, upper knob = vertical speed)
        cfg.Modes["6"] = new ModeBinding
        {
            Label = "AP",
            // fineStep 0: altitude is driven by the LOWER knob only. Giving it a fine step let it
            // swallow the upper knob's delta, so one VS click also moved the target altitude.
            Dial1 = Abs("altitude", 100, 0),
            // Manual 3.5: upper knob = VS, 100 fpm per detent. These are the AP's own UP/DN
            // buttons (verified: AP_VS_VAR_INC moves AUTOPILOT VERTICAL HOLD VAR by exactly
            // 100), unlike AP_VS_VAR_SET_ENGLISH which some autopilots ignore.
            Dial2 = IncDec("AP_VS_VAR_INC", "AP_VS_VAR_DEC"),
        };

        // Mode 7 — XPDR (manual: lower knob = first two digits, upper knob = last two digits,
        // toggle button = IDENT)
        cfg.Modes["7"] = new ModeBinding
        {
            Label = "XPDR",
            Dial1 = new DialBinding { Absolute = "squawk", Step = 100, FineStep = 1 },
            Dial2 = new DialBinding { Absolute = "squawk", Step = 100, FineStep = 1 },
            // Manual 1.1: the transponder's shift function is the mode (OFF/STBY/ON/ALT).
            ShiftDial1 = new DialBinding { Absolute = "transponderMode", Step = 1, FineStep = 0 },
            ShiftDial2 = new DialBinding { Absolute = "transponderMode", Step = 1, FineStep = 0 },
            Buttons = { ["0x100"] = B("XPNDR_IDENT_ON") },
        };

        // The swap and shift buttons keep working on the modes that did not override them.
        foreach (var (key, mode) in cfg.Modes)
        {
            if (key is "0" or "1" or "2" or "3") continue;
            mode.Buttons.TryAdd("0x100", new ButtonBinding());
        }

        return cfg;
    }
}
