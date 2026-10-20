using System.IO;
using MagentaBridge.Native;

namespace MagentaBridge.Core;

/// <summary>
/// Works out which descriptor field is the B-event hash, by driving one event whose effect is
/// readable as a SimVar. AS530_COMSWAP swaps COM1 standby/active, so a single attempt tells
/// us both whether the Input Events path works and which DWORD the sim accepts.
/// </summary>
public static class BEventHarness
{
    private const int Stride = 76;
    // SIMCONNECT_RECV_LIST_TEMPLATE header: dwSize, dwVersion, dwID, dwRequestID, dwArraySize.
    private const int HeaderSize = 20;
    private const int RecvIdEnumerate = 34;

    public static int Run(string outputPath, string targetName)
    {
        var lines = new List<string>();
        using var sim = new SimConnectApi();
        var options = new SimConnectOptions { ClientName = "Magenta Bridge Probe" };

        void Flush() => File.WriteAllText(outputPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);

        try
        {
            if (!sim.Load(options) || !sim.Open(options))
            {
                lines.Add($"FAIL: client/open — {sim.LastError}");
                Flush();
                return 1;
            }

            lines.Add($"client : {sim.ClientPath}");
            lines.Add($"inputs : {sim.InputEventStatus}");

            var standby = 0.0;
            var active = 0.0;
            byte[]? descriptorBlock = null;

            var useTransponder = targetName.Contains("TRANSPONDER", StringComparison.OrdinalIgnoreCase);
            var transponderState = 0;

            sim.SimObjectData += (defineId, payload) =>
            {
                if (defineId == 0 && payload.Length >= 16)
                {
                    standby = BitConverter.ToDouble(payload, 0);
                    active = BitConverter.ToDouble(payload, 8);
                }
                else if (defineId == 1 && payload.Length >= 4)
                {
                    transponderState = BitConverter.ToInt32(payload, 0);
                }
            };
            var otherRecvs = new List<(uint Id, string Text)>();
            sim.RawRecv += (id, raw) =>
            {
                if (id == RecvIdEnumerate && descriptorBlock == null) { descriptorBlock = raw; return; }

                // Everything else is dumped verbatim: a params reply carries Hash + a text value,
                // which is what tells us whether this event is parameter-driven.
                var sb = new System.Text.StringBuilder();
                foreach (var b in raw) sb.Append(b >= 32 && b < 127 ? (char)b : '.');
                otherRecvs.Add((id, sb.ToString()));
            };

            void Pump(int ms)
            {
                var until = DateTime.UtcNow.AddMilliseconds(ms);
                while (DateTime.UtcNow < until) { sim.CallDispatch(); Thread.Sleep(10); }
            }

            sim.EnumerateInputEvents(11);
            Pump(3000);

            if (descriptorBlock == null)
            {
                lines.Add("FAIL: no enumerate response (recv id 34)");
                Flush();
                return 1;
            }

            var count = BitConverter.ToUInt32(descriptorBlock, 16);
            lines.Add($"descriptors: {count}");

            var nameOffset = -1;
            for (var i = 0; i < count; i++)
            {
                var offset = HeaderSize + i * Stride;
                if (offset + Stride > descriptorBlock.Length) break;

                var name = System.Text.Encoding.ASCII
                    .GetString(descriptorBlock, offset + 8, 64)
                    .Split('\0')[0];

                if (!string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase)) continue;

                nameOffset = offset + 8;
                lines.Add($"found {name} at block offset {nameOffset}");
                break;
            }

            if (nameOffset < 0)
            {
                lines.Add($"FAIL: {targetName} not found in the enumeration");
                Flush();
                return 1;
            }

            sim.AddToDataDefinition(0, "COM STANDBY FREQUENCY:1", "MHz", SimConnectApi.DatatypeFloat64);
            sim.AddToDataDefinition(0, "COM ACTIVE FREQUENCY:1", "MHz", SimConnectApi.DatatypeFloat64);
            sim.RequestDataOnSimObject(0, 0);

            // Transponder events live on a unit independent of the GNS, so they separate
            // "the B-event plumbing is broken" from "the GNS is not powered".
            if (useTransponder)
            {
                sim.AddToDataDefinition(1, "TRANSPONDER STATE:1", "Enum", SimConnectApi.DatatypeInt32);
                sim.RequestDataOnSimObject(1, 1);
            }

            Pump(1500);
            var baseTransponder = transponderState;

            // The oracle only means anything when standby and active differ. If they match, nudge
            // the standby aside with plain K-events first, and put it back at the end.
            var nudged = 0;
            if (!useTransponder && Math.Abs(standby - active) < 0.001)
            {
                const uint wholeInc = 9;
                sim.MapEvent(wholeInc, "COM_RADIO_WHOLE_INC");
                for (var i = 0; i < 3; i++) { sim.Transmit(wholeInc, 1); Pump(400); }
                Pump(1200);
                nudged = 3;
                lines.Add($"nudged standby by +{nudged} MHz so the oracle is meaningful");
            }

            var baseStandby = standby;
            var baseActive = active;
            lines.Add($"before : standby={standby:F3} active={active:F3} " +
                      (Math.Abs(baseStandby - baseActive) > 0.001 ? "(distinct — oracle valid)" : "(STILL EQUAL — inconclusive)"));

            // The SDK header settles the layout: Name[64], UINT64 Hash, INPUT_EVENT_TYPE, and no
            // NodeNames in this build — 76 bytes, which is the stride we measured. AS530_COMSWAP
            // visibly swaps COM1, so it is the oracle.
            ulong? winner = null;
            var hashOffset = nameOffset + 64;
            ulong hash = 0;
            if (hashOffset + 8 <= descriptorBlock.Length)
            {
                hash = BitConverter.ToUInt64(descriptorBlock, hashOffset);
                var eType = BitConverter.ToInt32(descriptorBlock, nameOffset + 72);
                lines.Add($"hash 0x{hash:X16}  eType={eType}");

                sim.SubscribeInputEvent(hash);
                Pump(120);

                // eType 0 = SIMCONNECT_INPUT_EVENT_TYPE_DOUBLE, so send a double with unit size 8.
                // Hold the press across several frames so the sim samples it as down.
                sim.SetInputEventDouble(hash, 1.0);
                Pump(250);
                sim.SetInputEventDouble(hash, 0.0);
                Pump(1600);

                // Only a genuine exchange counts: the two values must have been distinct first.
                var swapped = useTransponder
                    ? transponderState != baseTransponder
                    : Math.Abs(baseStandby - baseActive) > 0.001 &&
                      Math.Abs(standby - baseActive) < 0.001 &&
                      Math.Abs(active - baseStandby) < 0.001;

                lines.Add(useTransponder
                    ? $"after  : transponderState={transponderState} (was {baseTransponder})" +
                      (swapped ? "   *** CHANGED ***" : "")
                    : $"after  : standby={standby:F3} active={active:F3}" +
                      (swapped ? "   *** SWAPPED ***" : ""));

                if (swapped)
                {
                    winner = hash;
                    sim.SetInputEventDouble(hash, 1.0);
                    Pump(250);
                    sim.SetInputEventDouble(hash, 0.0);   // put the radio back
                    Pump(1200);
                    lines.Add($"restored: standby={standby:F3} active={active:F3}");
                }
            }

            // A text value here means the event is parameter-driven, not a simple numeric input,
            // which would explain why SetInputEvent has no effect on it.
            if (hash != 0)
            {
                otherRecvs.Clear();
                sim.EnumerateInputEventParams(hash);
                Pump(2500);

                if (otherRecvs.Count == 0) lines.Add("params : no reply (event takes no parameters)");
                else foreach (var (rid, text) in otherRecvs.Take(6))
                    lines.Add($"params : id={rid} {text[..Math.Min(text.Length, 180)]}");
            }

            if (nudged > 0)
            {
                const uint wholeDec = 10;
                sim.MapEvent(wholeDec, "COM_RADIO_WHOLE_DEC");
                for (var i = 0; i < nudged; i++) { sim.Transmit(wholeDec, 1); Pump(300); }
                Pump(800);
                lines.Add($"standby nudged back; now standby={standby:F3} active={active:F3}");
            }

            var didSwap = winner.HasValue;
            lines.Add("");
            lines.Add(didSwap
                ? $"HASH CONFIRMED: 0x{winner:X16} drove the event"
                : "the 64-bit hash did not drive the event");

            lines.Add("");
            lines.Add(didSwap ? "VERDICT: Input Events path WORKS." : "VERDICT: accepted but inert for this sweep.");

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
