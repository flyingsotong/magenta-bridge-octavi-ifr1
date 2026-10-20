using System.IO;
using MagentaBridge.Native;

namespace MagentaBridge.Core;

/// <summary>
/// Verifies the writable-SimVar path used for transponder mode: read a SimVar, write a new
/// value with SetDataOnSimObject, read it back, then restore. This is the mechanism the
/// B-event investigation could not replace, so it must be proven against the live sim.
/// </summary>
public static class WriteHarness
{
    public static int Run(string outputPath, string simVar, string units)
    {
        var lines = new List<string>();
        using var sim = new SimConnectApi();
        var options = new SimConnectOptions { ClientName = "Magenta Bridge Probe" };

        void Flush() => File.WriteAllText(outputPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);

        try
        {
            if (!sim.Load(options) || !sim.Open(options))
            {
                lines.Add($"FAIL: {sim.LastError}");
                Flush();
                return 1;
            }

            var value = -1;
            var updates = 0;
            sim.SimObjectData += (defineId, payload) =>
            {
                if (defineId != 0 || payload.Length < 4) return;
                value = BitConverter.ToInt32(payload, 0);
                updates++;
            };

            // Definition 0 is read back; definition 2 is the one the engine writes with.
            sim.AddToDataDefinition(0, simVar, units, SimConnectApi.DatatypeInt32);
            sim.AddToDataDefinition(2, simVar, units, SimConnectApi.DatatypeInt32);
            sim.RequestDataOnSimObject(0, 0);

            void Pump(int ms)
            {
                var until = DateTime.UtcNow.AddMilliseconds(ms);
                while (DateTime.UtcNow < until) { sim.CallDispatch(); Thread.Sleep(10); }
            }

            Pump(1500);
            var original = value;
            lines.Add($"{simVar}: read {original} after {updates} updates");
            if (updates == 0) { lines.Add("FAIL: no data"); Flush(); return 2; }

            // Sweep every plausible value: the unit has five positions (OFF/SBY/TST/ON/ALT),
            // so find out which values the sim accepts and round-trips.
            lines.Add($"sweep from {original}:");
            var accepted = new List<int>();
            for (var trial = 0; trial <= 5; trial++)
            {
                var ok = sim.SetDataInt32(2, trial);
                Pump(1200);
                var roundTrip = value == trial;
                if (roundTrip) accepted.Add(trial);
                lines.Add($"  write {trial} -> accepted={ok}, readback={value}, roundTrip={roundTrip}");
            }

            lines.Add($"values that round-trip: {string.Join(", ", accepted)}");

            sim.SetDataInt32(2, original);
            Pump(1500);
            lines.Add($"restored to {original}; readback {value}");

            lines.Add("");
            lines.Add(accepted.Count > 0
                ? $"VERDICT: writable SimVars WORK — {accepted.Count} distinct states available."
                : "VERDICT: the write was rejected or ignored.");
            Flush();
            return accepted.Count > 0 ? 0 : 3;
        }
        catch (Exception ex)
        {
            lines.Add($"EXCEPTION {ex.GetType().Name}: {ex.Message}");
            Flush();
            return 1;
        }
        finally
        {
            sim.Close();
        }
    }
}
