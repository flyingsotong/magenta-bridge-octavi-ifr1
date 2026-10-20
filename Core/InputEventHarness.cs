using System.IO;
using System.Text;
using MagentaBridge.Native;

namespace MagentaBridge.Core;

/// <summary>
/// Dumps the sim's raw dispatch messages after asking it to enumerate input events.
/// The B-event descriptor layout differs between sim builds, so this records the bytes
/// rather than assuming a structure — the names and hashes are then read off the dump.
/// </summary>
public static class InputEventHarness
{
    public static int Run(string outputPath)
    {
        var lines = new List<string>();
        using var sim = new SimConnectApi();
        var options = new SimConnectOptions { ClientName = "Magenta Bridge Probe" };

        void Flush() => File.WriteAllText(outputPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);

        try
        {
            if (!sim.Load(options)) { lines.Add("FAIL: could not load a client"); Flush(); return 1; }
            lines.Add($"client : {sim.ClientPath} ({sim.ClientVersion})");
            lines.Add($"inputs : {sim.InputEventStatus}");

            if (!sim.Open(options)) { lines.Add($"FAIL: open — {sim.LastError}"); Flush(); return 1; }
            lines.Add("open   : connected");

            var captured = new List<(uint Id, byte[] Raw)>();
            sim.RawRecv += (id, raw) =>
            {
                lock (captured)
                {
                    if (captured.Count < 200) captured.Add((id, raw));
                }
            };

            void Pump(int ms)
            {
                var until = DateTime.UtcNow.AddMilliseconds(ms);
                while (DateTime.UtcNow < until) { sim.CallDispatch(); Thread.Sleep(10); }
            }

            Pump(600);
            lock (captured) captured.Clear();

            sim.EnumerateInputEvents(7);
            Pump(3000);

            List<(uint Id, byte[] Raw)> snapshot;
            lock (captured) snapshot = new(captured);

            // Full payload of the largest message, so the descriptor layout can be parsed offline.
            var biggest = snapshot.OrderByDescending(m => m.Raw.Length).FirstOrDefault();
            if (biggest.Raw != null)
            {
                File.WriteAllBytes(outputPath + ".bin", biggest.Raw);
                lines.Add($"full payload written: {outputPath}.bin ({biggest.Raw.Length} bytes)");
            }

            lines.Add($"captured messages: {snapshot.Count}");
            foreach (var group in snapshot.GroupBy(m => m.Id).OrderBy(g => g.Key))
            {
                var first = group.First().Raw;
                lines.Add($"recv id={group.Key} count={group.Count()} len={first.Length}");
                lines.Add("  hex: " + BitConverter.ToString(first, 0, Math.Min(first.Length, 160)));

                var text = new string(first.Select(b => b >= 32 && b < 127 ? (char)b : '.').ToArray());
                lines.Add("  txt: " + (text.Length > 300 ? text.Substring(0, 300) : text));
            }

            var names = new StringBuilder();
            foreach (var message in snapshot)
            {
                var text = new string(message.Raw.Select(b => b >= 32 && b < 127 ? (char)b : ' ').ToArray());
                foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.Length >= 4 &&
                        (token.StartsWith("AS5") || token.StartsWith("AS4") || token.StartsWith("AS1") ||
                         token.Contains("Knob", StringComparison.OrdinalIgnoreCase) ||
                         token.Contains("_Push", StringComparison.OrdinalIgnoreCase)))
                    {
                        names.AppendLine($"{message.Id}: {token}");
                    }
                }
            }

            lines.Add("");
            lines.Add("--- candidate B-event names ---");
            lines.Add(names.ToString());

            Flush();
            return 0;
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
