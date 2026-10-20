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
            new[] { "MobiFlight.AS530_ENT_Push=0", "MobiFlight.AS1000_PFD_ENT_Push=0" });

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

        Step("GPS mode pairs GNS + G1000 knob events (mode 4, dial 1)",
            Report(0, 1, 0, 4),
            new[] { "MobiFlight.AS530_RightLargeKnob_Right=1", "MobiFlight.AS1000_PFD_FMS_Lower_INC=1" });

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
