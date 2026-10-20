using System.IO;
using MagentaBridge.Native;

namespace MagentaBridge.Core;

/// <summary>
/// Generic end-to-end test of ONE event: read a SimVar, transmit the event, read it again,
/// then transmit once more to undo it. Proves whether a given event name actually acts on
/// the running sim, which is the only way to tell a working binding from a plausible one.
/// </summary>
public static class ProbeHarness
{
    public static int Run(string outputPath, string eventName, string simVar, string units, bool int32, uint data = 0, string preEvent = "")
    {
        var lines = new List<string>();
        var options = new SimConnectOptions { ClientName = "Magenta Bridge Probe" };
        using var sim = new SimConnectApi();

        void Flush() => File.WriteAllText(outputPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);

        try
        {
            lines.Add($"event  : {eventName}");
            lines.Add($"simvar : {simVar} ({units}, {(int32 ? "INT32" : "FLOAT64")})");

            if (!sim.Load(options)) { lines.Add("FAIL: no SimConnect client"); Flush(); return 1; }
            lines.Add($"client : {sim.ClientPath} ({sim.ClientVersion})");

            if (!sim.Open(options)) { lines.Add($"FAIL: Open — {sim.LastError}"); Flush(); return 1; }

            double value = 0;
            var updates = 0;

            sim.SimObjectData += (defineId, payload) =>
            {
                if (defineId != 0) return;
                if (int32)
                {
                    if (payload.Length < 4) return;
                    value = BitConverter.ToInt32(payload, 0);
                }
                else
                {
                    if (payload.Length < 8) return;
                    value = BitConverter.ToDouble(payload, 0);
                }
                updates++;
            };

            sim.AddToDataDefinition(0, simVar, units,
                int32 ? SimConnectApi.DatatypeInt32 : SimConnectApi.DatatypeFloat64);
            sim.RequestDataOnSimObject(0, 0);

            void Pump(int ms)
            {
                var until = DateTime.UtcNow.AddMilliseconds(ms);
                while (DateTime.UtcNow < until) { sim.CallDispatch(); Thread.Sleep(10); }
            }

            Pump(1500);
            var before = value;
            lines.Add($"before : {before}");
            if (updates == 0) { lines.Add("FAIL: the sim is not answering data requests"); Flush(); return 2; }

            const uint id = 1;

            // Optional precondition event, sent once and left in effect (e.g. AP_MASTER to
            // engage the autopilot before testing whether NAV hold can capture).
            const uint preId = 5;
            if (!string.IsNullOrWhiteSpace(preEvent))
            {
                var preMapped = sim.MapEvent(preId, preEvent);
                sim.Transmit(preId, 0);
                Pump(1600);
                lines.Add($"pre    : {preEvent} sent (mapped={preMapped})");
            }

            lines.Add($"mapped : {sim.MapEvent(id, eventName)}");

            sim.Transmit(id, data);
            Pump(1800);
            var after = value;
            lines.Add($"after  : {after}");

            // Restore: for a toggle this reverses one step; for an absolute set it is a no-op.
            sim.Transmit(id, data);
            Pump(1500);

            // Leave the precondition as we found it (the AP toggle is symmetric).
            if (!string.IsNullOrWhiteSpace(preEvent))
            {
                sim.Transmit(preId, 0);
                Pump(1000);
                lines.Add($"pre    : {preEvent} restored");
            }
            lines.Add($"undone : {value}");

            var changed = Math.Abs(after - before) > 0.0001;
            lines.Add("");
            lines.Add(changed
                ? "VERDICT: event WORKS — the sim acted on it."
                : "VERDICT: event IGNORED — accepted by the client, no effect in the sim.");

            Flush();
            return changed ? 0 : 3;
        }
        catch (Exception ex)
        {
            lines.Add($"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            Flush();
            return 1;
        }
        finally
        {
            sim.Close();
        }
    }
}
