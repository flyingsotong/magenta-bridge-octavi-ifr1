using System.IO;

namespace MagentaBridge.Core;

/// <summary>
/// Deterministic verification of the decode path without a device or a running sim.
/// Feeds synthetic 8-byte reports through the real handler and asserts exactly which
/// events would be transmitted. This is how defects 4 and 5 are proven rather than claimed.
/// </summary>
public static class ReplayHarness
{
    private const byte Id = 0x0B;

    private static byte[] Report(uint field, sbyte dial1, sbyte dial2, byte mode)
    {
        var bytes = new byte[8];
        bytes[0] = Id;
        BitConverter.GetBytes(field).CopyTo(bytes, 1);
        bytes[5] = (byte)dial1;
        bytes[6] = (byte)dial2;
        bytes[7] = mode;
        return bytes;
    }

    public static int Run(string outputPath)
    {
        var config = AppConfig.CreateDefault();
        var engine = new PanelEngine(config);
        var capture = new List<(string Name, uint Data)>();
        engine.Capture = capture;

        var lines = new List<string>();
        var failures = 0;

        void Step(string label, byte[] report, string[] expected)
        {
            engine.FeedReport(report);
            var actual = capture.Select(c => $"{c.Name}={c.Data}").ToArray();
            capture.Clear();

            var ok = actual.SequenceEqual(expected, StringComparer.Ordinal);
            if (!ok) failures++;

            lines.Add($"{(ok ? "PASS" : "FAIL")} — {label}");
            lines.Add($"       expected: {Fmt(expected)}");
            if (!ok) lines.Add($"       actual  : {Fmt(actual)}");
        }

        Step("button: AP master (mode 0, bit 0x4000)",
            Report(0x4000, 0, 0, 0),
            new[] { "AP_MASTER=0" });

        Step("release sends nothing",
            Report(0, 0, 0, 0),
            Array.Empty<string>());

        Step("defect 5: fourth softkey is ENT (mode 4, bit 0x80)",
            Report(0x80, 0, 0, 4),
            new[] { "H:AS530_ENT_Push=0", "H:AS1000_PFD_ENT_Push=0" });

        Step("release (mode 4)",
            Report(0, 0, 0, 4),
            Array.Empty<string>());

        Step("defect 4: unbound bit transmits no unnamed event (mode 0, bit 0x08)",
            Report(0x08, 0, 0, 0),
            Array.Empty<string>());

        Step("dial 1: three detents clockwise (mode 0)",
            Report(0, 3, 0, 0),
            new[] { "COM_RADIO_WHOLE_INC=1", "COM_RADIO_WHOLE_INC=1", "COM_RADIO_WHOLE_INC=1" });

        Step("dial 1: one detent anticlockwise",
            Report(0, -1, 0, 0),
            new[] { "COM_RADIO_WHOLE_DEC=1" });

        Step("dial 2: one detent clockwise",
            Report(0, 0, 1, 0),
            new[] { "COM_RADIO_FRACT_INC=1" });

        Step("shift button toggles the latch and sends nothing",
            Report(0x200, 0, 0, 0),
            Array.Empty<string>());

        Step("release after shift",
            Report(0, 0, 0, 0),
            Array.Empty<string>());

        Step("shifted dial writes an absolute heading (0 + 1x10 degrees)",
            Report(0, 1, 0, 0),
            new[] { "HEADING_BUG_SET=10" });

        // Manual 3.3: COM1 -> HDG (both knobs), COM2 -> BARO (both knobs). The upper knob on the
        // heading page must nudge the bug by 1 degree, not step the COM fraction.
        Step("COM1 shift, upper knob nudges heading by 1 degree",
            Report(0, 0, 1, 0),
            new[] { "HEADING_BUG_SET=11" });

        Step("mode change clears shift, sends nothing",
            Report(0, 0, 0, 1),
            Array.Empty<string>());

        Step("COM2 shift arms on the knob press",
            Report(0x200, 0, 0, 1),
            Array.Empty<string>());

        Step("COM2 shift, lower knob steps the barometer",
            Report(0, 1, 0, 1),
            new[] { "KOHLSMAN_INC=1" });

        Step("COM2 shift, upper knob also steps the barometer",
            Report(0, 0, 1, 1),
            new[] { "KOHLSMAN_INC=1" });

        Step("COM2 shift released",
            Report(0x200, 0, 0, 1),
            Array.Empty<string>());

        Step("mode 1 dial 1 drives COM2",
            Report(0, 1, 0, 1),
            new[] { "COM2_RADIO_WHOLE_INC=1" });

        Step("GPS mode sends both GNS and G1000 knob events through the module (mode 4, dial 1)",
            Report(0, 1, 0, 4),
            new[] { "H:AS530_RightLargeKnob_Right=1", "H:AS1000_PFD_FMS_Lower_INC=1" });

        Step("altitude mode adds 100 ft per detent (mode 6)",
            Report(0, 1, 0, 6),
            new[] { "AP_ALT_VAR_SET_ENGLISH=100" });

        Step("squawk mode adds 1 to the low digit (mode 7, dial 2)",
            Report(0, 0, 1, 7),
            new[] { "XPNDR_SET=1" });

        // Manual 3.6: lower knob = first two digits (+100), upper = last two (+1).
        // Code is 1 at this point, so +100 -> 101 -> BCD 0x101 = 257.
        Step("XPDR lower knob steps the first two digits (mode 7, dial 1)",
            Report(0, 1, 0, 7),
            new[] { "XPNDR_SET=257" });

        // Manual 3.6: IDENT is on the toggle button.
        Step("FMS1 CDI button uses the K-event that actually exists (mode 4, bit 0x4000)",
            Report(0x4000, 0, 0, 4),
            new[] { "TOGGLE_GPS_DRIVES_NAV1=0" });

        Step("AP upper knob steps vertical speed by 100 fpm (mode 6)",
            Report(0, 0, 1, 6),
            new[] { "AP_VS_VAR_INC=1" });

        Step("XPDR toggle button sends IDENT (mode 7, bit 0x100)",            Report(0x100, 0, 0, 7),
            new[] { "XPNDR_IDENT_ON=0" });

        // Manual 1.1: shift on the transponder page is the mode. It is a writable SimVar, not an
        // event, so a write is captured instead of a transmitted event.
        Step("XPDR shift button arms shift mode (mode 7, bit 0x200)",
            Report(0x200, 0, 0, 7),
            Array.Empty<string>());

        Step("shift + dial steps the transponder mode (SBY 1 -> TST 2)",
            Report(0, 1, 0, 7),
            new[] { "w:transponder state:1=2" });

        // Five detents (OFF/SBY/TST/ON/ALT) and no wrap: past ALT it stays, below OFF it stays.
        Step("another detent reaches ON (2 -> 3)",
            Report(0, 1, 0, 7),
            new[] { "w:transponder state:1=3" });

        Step("another detent reaches ALT (3 -> 4)",
            Report(0, 1, 0, 7),
            new[] { "w:transponder state:1=4" });

        Step("turning past ALT stops there (no write)",
            Report(0, 1, 0, 7),
            Array.Empty<string>());

        Step("turning back from ALT steps down (4 -> 3)",
            Report(0, -1, 0, 7),
            new[] { "w:transponder state:1=3" });

        // NAV: the detents must accumulate. BCD of 108.00 is 0x10800 = 67584, 109.00 is 67840,
        // 109.05 is 67845. Re-sending the sim value (the original's bug) would repeat one value.
        Step("NAV1 first detent clamps up to 108.00",
            Report(0, 1, 0, 2),
            new[] { "NAV1_STBY_SET=67584" });

        Step("NAV1 second detent accumulates to 109.00",
            Report(0, 1, 0, 2),
            new[] { "NAV1_STBY_SET=67840" });

        Step("NAV1 fine detent steps 50 kHz to 109.05",
            Report(0, 0, 1, 2),
            new[] { "NAV1_STBY_SET=67845" });

        Step("NAV2 is tracked independently of NAV1",
            Report(0, 1, 0, 3),
            new[] { "NAV2_STBY_SET=67584" });

        Step("NAV2 anticlockwise backs down a MHz",
            Report(0, -1, 0, 3),
            new[] { "NAV2_STBY_SET=67584" });

        // Manual 5.3.4: "press and hold the toggle button (<->) while turning the rotary knob
        // to zoom in/out". The knob press is the GNS knob push in FMS modes, so the swap key
        // is what arms the range.
        Step("enter FMS2 for the range test",
            Report(0, 0, 0, 5),
            Array.Empty<string>());

        Step("holding the toggle button arms range and sends nothing itself",
            Report(0x100, 0, 0, 5),
            Array.Empty<string>());

        Step("held shift, lower knob zooms the map in",
            Report(0x100, 1, 0, 5),
            new[] { "H:AS430_RNG_Zoom=1", "H:AS1000_MFD_RANGE_INC=1" });

        Step("held shift, upper knob zooms the map out",
            Report(0x100, 0, -1, 5),
            new[] { "H:AS430_RNG_Dezoom=1", "H:AS1000_MFD_RANGE_DEC=1" });

        Step("releasing the toggle button drops the shift",
            Report(0, 0, 0, 5),
            Array.Empty<string>());

        Step("released, the lower knob is the GNS large knob again",
            Report(0, 1, 0, 5),
            new[] { "H:AS430_RightLargeKnob_Right=1", "H:AS1000_MFD_FMS_Lower_INC=1" });

        // Contract checks: no device, no sim and no Community folder needed. These are the
        // things a packaging mistake would break silently.
        void Check(string label, bool ok, string detail)
        {
            if (!ok) failures++;
            lines.Add($"{(ok ? "PASS" : "FAIL")} — {label}");
            if (!ok) lines.Add($"       {detail}");
        }

        static IEnumerable<string> Names(ModeBinding mode)
        {
            foreach (var dial in new[] { mode.Dial1, mode.Dial2, mode.ShiftDial1, mode.ShiftDial2 })
            {
                if (dial == null) continue;
                foreach (var n in dial.Inc ?? Array.Empty<string>()) yield return n;
                foreach (var n in dial.Dec ?? Array.Empty<string>()) yield return n;
            }

            foreach (var button in mode.Buttons.Values)
                foreach (var n in button.Events ?? Array.Empty<string>()) yield return n;
        }

        var fmsNames = new[] { "4", "5" }
            .Where(config.Modes.ContainsKey)
            .SelectMany(k => Names(config.Modes[k]))
            .ToArray();

        Check($"every FMS binding goes through the module ({fmsNames.Length} of them)",
            fmsNames.All(n => ModuleLink.IsModuleEvent(n) || n == "TOGGLE_GPS_DRIVES_NAV1"),
            "one still names a plain sim event, so it would never reach the GNS: " +
            string.Join(", ", fmsNames.Where(n => !ModuleLink.IsModuleEvent(n) && n != "TOGGLE_GPS_DRIVES_NAV1")));

        var everyName = config.Modes.SelectMany(m => Names(m.Value))
            .Concat(config.Buttons.Values.SelectMany(b => b.Events ?? Array.Empty<string>()))
            .ToArray();

        Check("no binding still refers to MobiFlight",
            !everyName.Any(n => n.Contains("MobiFlight", StringComparison.OrdinalIgnoreCase)),
            "a binding still carries a MobiFlight name, which needs their module installed");

        Check("the FMS module ships inside the exe",
            ModuleInstaller.HasEmbeddedModule,
            "the embedded module resource is missing - this build would install nothing");

        Check($"config version {AppConfig.CurrentVersion} carries the new bindings",
            AppConfig.CurrentVersion >= 7,
            "a stale config would keep the old MobiFlight names and FMS mode would stay dead");

        // Regenerating across a version bump must not cost the user their preferences. This is
        // written as a file test because that is the only way to exercise the load path.
        var scratch = Path.Combine(Path.GetTempPath(), $"mb-config-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(scratch,
                "{ \"version\": 6, \"theme\": \"dark\", \"alwaysOnTop\": true, " +
                "\"startWithWindows\": true, \"welcomeDismissed\": true }");

            var reloaded = AppConfig.LoadOrCreate(scratch);

            Check("a version bump keeps theme, topmost, startup and the welcome state",
                reloaded.Theme == "dark" && reloaded.AlwaysOnTop &&
                reloaded.StartWithWindows && reloaded.WelcomeDismissed &&
                reloaded.Version == AppConfig.CurrentVersion,
                $"theme={reloaded.Theme} topmost={reloaded.AlwaysOnTop} " +
                $"startWithWindows={reloaded.StartWithWindows} welcome={reloaded.WelcomeDismissed} " +
                $"version={reloaded.Version}");
        }
        finally
        {
            try { File.Delete(scratch); } catch { }
            try { File.Delete(scratch + ".v6.bak"); } catch { }
        }

        var moduleLine = ModuleInstaller.StatusLine();
        Check("the module status line always says something actionable",
            moduleLine.StartsWith("FMS module: ", StringComparison.Ordinal) && moduleLine.Length > 20,
            moduleLine);

        // The status is read on every UI refresh, so the cached answer must be stable and fast.
        Check("the module status is cached and repeatable",
            ModuleInstaller.StatusLine() == moduleLine,
            $"first \"{moduleLine}\", second \"{ModuleInstaller.StatusLine()}\"");
        var summary = failures == 0
            ? $"ALL {lines.Count(l => l.StartsWith("PASS"))} CHECKS PASSED"
            : $"{failures} CHECK(S) FAILED";

        lines.Insert(0, summary);
        lines.Insert(1, "");

        File.WriteAllText(outputPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        return failures == 0 ? 0 : 1;

        static string Fmt(string[] items) => items.Length == 0 ? "(nothing)" : string.Join(", ", items);
    }
}
