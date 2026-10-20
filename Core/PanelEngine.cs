using System.Diagnostics;
using System.IO;
using MagentaBridge.Native;

namespace MagentaBridge.Core;

public sealed class PanelState
{
    public bool DeviceConnected;
    public string DeviceName = "";
    public string DeviceSerial = "";
    public bool SimConnected;
    public string SimClientPath = "";
    public string SimClientVersion = "";
    public string LastError = "";

    public int Mode;
    public string ModeLabel = "";
    public bool Shift;
    public uint Field;
    public int Dial1;
    public int Dial2;

    // From the two SimConnect data definitions.
    public double Com1, Com2, Nav1, Nav2, Heading, Obs1, Obs2;

    /// <summary>Active frequencies and barometer, read for the status panel only.</summary>
    public double Com1Active, Com2Active, Nav1Active, Nav2Active, Baro;
    public double Altitude, VerticalSpeed;
    public int SquawkBcd;
    public bool ApMaster, ApHeading, ApNav1, ApApr, ApAlt, ApVs;

    /// <summary>True when the CDI is following the GPS rather than a NAV radio (VLOC).</summary>
    public bool CdiOnGps;

    /// <summary>Loaded aircraft title, straight from the sim. Empty until the first reply.</summary>
    public string AircraftTitle = "";

    /// <summary>True once this aircraft has been seen to ignore remote vertical speed.</summary>
    public bool VsIgnoredByAircraft;

    /// <summary>Transponder selector position, for the status panel: 0 OFF … 4 ALT.</summary>
    public int TransponderMode;

    public long Reports;
    public long EventsSent;
}

/// <summary>
/// The whole bridge: HID in, SimConnect out. Deliberately two independent threads so a
/// busy sim cannot stall the panel (teardown defect 13), and no transmission without a
/// real event name (defect 4).
/// </summary>
public sealed class PanelEngine : IDisposable
{
    private const byte ReportId = 0x0B;

    private AppConfig _config;
    private readonly SimConnectApi _sim = new();
    private readonly Dictionary<string, uint> _eventIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _mapped = new(StringComparer.Ordinal);
    private readonly ManualResetEventSlim _deviceLost = new(false);
    private readonly object _gate = new();

    private HidConnection? _hid;
    private Thread? _simThread;
    private Thread? _hidThread;
    private volatile bool _stopping;

    private uint _nextEventId = 1;
    private uint _lastField;
    private byte _lastLed = 0xFE;
    private bool _shift;
    private DateTime _lastHeartbeat = DateTime.UtcNow;

    /// <summary>
    /// Switch bounce window. The knob switch was observed toggling twice 824 ms apart from one
    /// press, so the window must exceed that. A deliberate second press within a second is
    /// swallowed, which is the acceptable trade for a latch that stays where you put it.
    /// </summary>
    private const int ShiftDebounceMs = 1000;

    /// <summary>How many times to reopen a handle that is open but delivering no reports.</summary>
    private const int MaxReopenAttempts = 5;
    private int _reopenAttempts;
    private bool _gaveUpReopening;
    private DateTime _lastShiftToggle = DateTime.MinValue;

    /// <summary>Dial burst tracking for encoder acceleration.</summary>
    private DateTime _lastDialAt = DateTime.MinValue;
    private int _dialBurst;

    /// <summary>Inputs seen since the last self-test reset, keyed by a stable name.</summary>
    private readonly HashSet<string> _selfTestSeen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selfTestSeenThisRun = new(StringComparer.Ordinal);

    /// <summary>Pending vertical-speed selection, seeded from the sim when first touched.</summary>
    private double? _vsPending;

    /// <summary>Last VS value we asked the sim for, to detect an aircraft-imposed clamp.</summary>
    private double? _vsSent;

    /// <summary>VS click burst tracking, to notice an aircraft that ignores remote VS.</summary>
    private int _vsClickBurst;
    private double _vsBeforeBurst;
    private DateTime _vsLastClickAt = DateTime.MinValue;

    // NAV standby frequencies are tracked locally: the detents must accumulate, otherwise the
    // handler re-sends the value the sim just reported and the knob does nothing at all.
    private double? _nav1Pending, _nav2Pending;
    private double? _nav1Sent, _nav2Sent;
    private int _transponderMode = 1;   // 0 OFF, 1 STBY, 2 ON, 3 ALT

    public PanelState State { get; } = new();

    /// <summary>
    /// We do not know which context button is active until the panel tells us, and it only
    /// speaks on change. Starting at COM1 was a lie whenever the panel was left in another
    /// mode, so the mode stays unknown (-1) until the first report arrives.
    /// </summary>
    private void SeedState()
    {
        State.Mode = -1;
        State.ModeLabel = "-";
    }
    public AppConfig Config => _config;

    /// <summary>
    /// Swaps in a new configuration. Bindings take effect immediately because every
    /// lookup reads this field; device and SimConnect changes need a restart.
    /// </summary>
    public void ApplyConfig(AppConfig config)
    {
        _config = config;
        Log.Info("engine config replaced; device and SimConnect settings apply on restart");
        Raise();
    }

    public event Action? Changed;
    public event Action<string>? Notice;

    /// <summary>When set, events are captured here instead of sent to the sim (test hook).</summary>
    public ICollection<(string Name, uint Data)>? Capture { get; set; }

    /// <summary>Feeds a report through the real decode path without a device (test hook).</summary>
    public void FeedReport(byte[] report) => HandleReport(report);

    public PanelEngine(AppConfig config)
    {
        _config = config;
        _sim.SimObjectData += OnSimObjectData;
        _sim.ConnectionChanged += connected =>
        {
            State.SimConnected = connected;
            if (!connected) State.LastError = "SimConnect disconnected";
            Raise();
        };
    }

    public void Start()
    {
        Log.Info($"Magenta Bridge starting; config {(_config.Path.Length > 0 ? _config.Path : "(defaults)")}");
        SeedState();

        if (!_sim.Load(_config.SimConnect))
        {
            State.LastError = "no SimConnect client";
        }

        State.SimClientPath = _sim.ClientPath;
        State.SimClientVersion = _sim.ClientVersion;

        _simThread = new Thread(SimLoop) { IsBackground = true, Name = "SimConnect" };
        _simThread.Start();

        _hidThread = new Thread(HidLoop) { IsBackground = true, Name = "HID supervisor" };
        _hidThread.Start();

        Raise();
    }

    // ---- HID ------------------------------------------------------------

    private void HidLoop()
    {
        while (!_stopping)
        {
            HidDeviceInfo? info = null;
            try
            {
                info = Hid.Find(_config.Device);
            }
            catch (Exception ex)
            {
                Log.Error("HID enumeration failed", ex);
            }

            if (info == null)
            {
                if (State.DeviceConnected)
                {
                    State.DeviceConnected = false;
                    State.DeviceName = "";
                    State.DeviceSerial = "";
                    Notice?.Invoke("IFR-1 disconnected");
                    Raise();
                }
                if (_deviceLost.Wait(1000)) _deviceLost.Reset();
                continue;
            }

            try
            {
                using var connection = new HidConnection(info);
                _hid = connection;

                State.DeviceConnected = true;
                State.DeviceName = info.Display;
                State.DeviceSerial = info.Serial;
                State.LastError = "";
                Log.Info($"IFR-1 connected: {info.Display}; in={info.InputReportLength}B out={info.OutputReportLength}B");
                Notice?.Invoke($"IFR-1 connected: {info.Serial}");
                Raise();

                // Repaint the LED state on connect: the device does not retain it.
                _lastLed = 0xFE;
                PushLed();

                connection.ReportReceived += HandleReport;
                connection.Disconnected += reason =>
                {
                    Log.Warn($"IFR-1 disconnected: {reason}");
                    _deviceLost.Set();
                };
                connection.Start();

                // Ask for the current state. Verified against a panel sitting in FMS1: it answers
                // with an all-zero report, so the reply is NOT the device's real state and must
                // not be applied. Log it for diagnosis, then wait for a genuine change report.
                if (connection.TryGetCurrentReport(out var initial))
                {
                    Log.Info($"state query returned {BitConverter.ToString(initial)} " +
                             "(not applied: the panel does not report its real state this way)");
                }

                // The device only speaks when touched, so silence is normal. But silence right
                // after opening means the reports are not reaching us at all, which from the
                // panel's side is indistinguishable from "the control is broken". Reopen instead
                // of waiting for the user to restart the app — a dead-but-connected handle has
                // repeatedly masqueraded as a mapping bug.
                var reportsAtConnect = State.Reports;
                var openedAt = DateTime.UtcNow;
                var warnIssued = false;
                var requestedReopen = false;
                var promptedStatus = false;

                while (!_stopping && !_deviceLost.IsSet)
                {
                    Thread.Sleep(50);

                    var silentFor = (DateTime.UtcNow - openedAt).TotalSeconds;
                    var silent = State.Reports == reportsAtConnect;

                    // The panel only speaks on change, so a panel left idle in FMS1 stays unknown.
                    // Re-sending the LED state is harmless and prompts some firmwares to report.
                    if (!promptedStatus && silent && silentFor >= 1.2)
                    {
                        promptedStatus = true;
                        PushLed();
                        Log.Debug("no report yet - re-sent the LED state to prompt a status report");
                    }

                    if (!silent)
                    {
                        warnIssued = true;
                        _reopenAttempts = 0;    // a working session earns a fresh budget
                        _gaveUpReopening = false;
                        continue;
                    }

                    if (silentFor < 30) continue;

                    if (!warnIssued)
                    {
                        warnIssued = true;
                        Log.Warn("no HID input reports since opening the IFR-1 — the handle is open " +
                                 "but not receiving. Reopening the device.");
                    }

                    if (_reopenAttempts < MaxReopenAttempts)
                    {
                        _reopenAttempts++;
                        requestedReopen = true;
                        Log.Warn($"reopening the IFR-1 (attempt {_reopenAttempts} of {MaxReopenAttempts})");
                        break;
                    }

                    // Give up re-opening; report once and stay alive for a manual restart.
                    if (silentFor > 20 && !_gaveUpReopening)
                    {
                        _gaveUpReopening = true;
                        Log.Error("the IFR-1 is not sending reports after several reopen attempts. " +
                                  "Close the flight (or any other program using the device) and restart " +
                                  "Magenta Bridge.");
                    }
                }

                // If we broke out to reopen, reset so the next session starts a fresh budget.
                if (requestedReopen) Thread.Sleep(300);

                _deviceLost.Reset();
            }
            catch (Exception ex)
            {
                State.LastError = $"device: {ex.Message}";
                Log.Error("HID session failed", ex);
                Thread.Sleep(1000);
            }
            finally
            {
                _hid = null;
                State.DeviceConnected = false;
                Raise();
            }
        }
    }

    private void HandleReport(byte[] report)
    {
        if (report.Length < 8)
        {
            Log.Warn($"short report of {report.Length} bytes ignored");
            return;
        }

        if (report[0] != ReportId)
        {
            Log.Warn($"unexpected report id 0x{report[0]:X2} (expected 0x{ReportId:X2})");
            return;
        }

        var field = BitConverter.ToUInt32(report, 1);
        var dial1 = (sbyte)report[5];
        var dial2 = (sbyte)report[6];
        var mode = report[7] & 0x07;

        uint pressed;
        lock (_gate)
        {
            pressed = ~_lastField & field;
            _lastField = field;
        }

        if (mode != State.Mode)
        {
            State.Mode = mode;
            _shift = false;
            State.Shift = false;
            State.ModeLabel = _config.Mode(mode).Label;
            Log.Info($"mode -> {mode} ({State.ModeLabel}); shift cleared");
        }

        State.Field = field;
        State.Dial1 = dial1;
        State.Dial2 = dial2;
        State.Reports++;

        // Report arrival is itself the diagnostic: if presses produce no reports, nothing
        // downstream can work. Log the first few and then sparsely.
        if (State.Reports <= 5 || State.Reports % 25 == 0)
            Log.Debug($"report #{State.Reports} field=0x{field:X8} dials={dial1}/{dial2} mode={mode}");

        // Buttons: rising edges only.
        for (var bit = 0; bit < 32; bit++)
        {
            var mask = 1u << bit;
            if ((pressed & mask) == 0) continue;

            Observe($"btn 0x{mask:X}");

            var binding = _config.FindButton(mode, mask);
            if (binding == null) continue;

            if (binding.TogglesShift)
            {
                // The knob switch bounces: a single press can arrive as press-release-press,
                // which toggled the latch on and immediately back off. Debounce it.
                // Skipped in test mode, where synthetic presses are microseconds apart.
                var now = DateTime.UtcNow;
                if (Capture == null && (now - _lastShiftToggle).TotalMilliseconds < ShiftDebounceMs)
                {
                    Log.Debug($"shift toggle ignored (bounce, {((now - _lastShiftToggle).TotalMilliseconds):F0} ms)");
                    continue;
                }

                _lastShiftToggle = now;
                _shift = !_shift;
                State.Shift = _shift;
                Log.Info($"shift -> {_shift} (mode {mode})");
            }

            if (binding.Events == null) continue;
            foreach (var name in binding.Events) Transmit(name, 0);
        }

        if (dial1 != 0 || dial2 != 0) HandleDials(mode, dial1, dial2);

        Raise();
    }

    private void HandleDials(int mode, int dial1, int dial2)
    {
        // Encoder acceleration: a fast burst of detents counts for more, so crossing a band does
        // not take twenty clicks. Disabled in test mode, where detents arrive instantly.
        if (Capture == null && (dial1 != 0 || dial2 != 0))
        {
            var now = DateTime.UtcNow;
            if ((now - _lastDialAt).TotalMilliseconds < 120) _dialBurst = Math.Min(_dialBurst + 1, 12);
            else _dialBurst = 0;
            _lastDialAt = now;

            var multiplier = _dialBurst >= 8 ? 8 : _dialBurst >= 5 ? 4 : _dialBurst >= 3 ? 2 : 1;
            if (multiplier > 1)
            {
                dial1 *= multiplier;
                dial2 *= multiplier;
                Log.Debug($"dial acceleration x{multiplier}");
            }
        }

        if (dial1 != 0) Observe(dial1 > 0 ? "dial1+" : "dial1-");
        if (dial2 != 0) Observe(dial2 > 0 ? "dial2+" : "dial2-");

        var modeBinding = _config.Mode(mode);
        var main = _shift ? (modeBinding.ShiftDial1 ?? modeBinding.Dial1) : modeBinding.Dial1;
        // In shift mode use only the shift bindings: falling back to the unshifted dial would
        // fire e.g. a COM fraction step while the same knob is meant to nudge the heading.
        var fine = _shift ? modeBinding.ShiftDial2 : modeBinding.Dial2;

        // Trace every dial event with the state that decided its binding. Without this, a turn
        // that lands in the wrong mode is indistinguishable from a turn that never arrived.
        Log.Info($"dial mode={mode} shift={_shift} d1={dial1} d2={dial2} " +
                 $"main={(main?.Absolute ?? string.Join("|", main?.Inc ?? Array.Empty<string>()))} " +
                 $"fine={(fine?.Absolute ?? string.Join("|", fine?.Inc ?? Array.Empty<string>()))}");

        if (main is { Absolute: not null })
        {
            // If the second knob drives a DIFFERENT absolute action (AP: altitude on the lower
            // knob, vertical speed on the upper), each knob must feed only its own action.
            // Otherwise one turn moves both selected altitude and vertical speed.
            var separate = fine is { Absolute: not null } &&
                           !string.Equals(fine.Absolute, main.Absolute, StringComparison.Ordinal);

            ApplyAbsolute(main, dial1, separate ? 0 : dial2);

            if (dial2 != 0 && separate) ApplyAbsolute(fine!, 0, dial2);
            else if (fine is { Absolute: null } && dial2 != 0) FireRepeated(fine, dial2);
            return;
        }

        if (dial1 != 0 && main != null) FireRepeated(main, dial1);
        if (dial2 != 0 && fine != null) FireRepeated(fine, dial2);
    }

    private void FireRepeated(DialBinding binding, int detents)
    {
        var names = detents > 0 ? binding.Inc : binding.Dec;
        if (names == null || names.Length == 0) return;

        for (var i = 0; i < Math.Abs(detents); i++)
            foreach (var name in names) Transmit(name, (uint)Math.Max(0, binding.Data));
    }

    private void ApplyAbsolute(DialBinding binding, int dial1, int dial2)
    {
        var delta = dial1 * binding.Step + dial2 * binding.FineStep;

        // A knob that did not move must not re-send its value: on a page where the other knob
        // drives a different action, that would transmit an unchanged setting every detent.
        if (delta == 0) return;

        switch (binding.Absolute)
        {
            case "heading":
            {
                var previous = State.Heading;
                var value = Wrap(State.Heading + delta, 360);
                State.Heading = value;
                Log.Debug($"heading {previous:F0} -> {value} (delta {delta:F0}, shift)");
                Transmit("HEADING_BUG_SET", (uint)value);
                break;
            }
            case "obs1":
            {
                var value = Wrap(State.Obs1 + delta, 360);
                State.Obs1 = value;
                Transmit("VOR1_SET", (uint)value);
                break;
            }
            case "obs2":
            {
                var value = Wrap(State.Obs2 + delta, 360);
                State.Obs2 = value;
                Transmit("VOR2_SET", (uint)value);
                break;
            }
            case "vs":
            {
                // Manual 1.1: in AP mode the upper knob is vertical speed, 100 fpm per detent.
                // The sim accepts AP_VS_VAR_SET_ENGLISH (verified 0 -> 700); the original's
                // MobiFlight NOSE_UP/NOSE_DN events do nothing without MobiFlight installed.
                // Do not impose a narrow ceiling here: the sim accepted 3000 fpm in testing, and a
                // tight clamp pins the value so clockwise turns stop doing anything. The reseed
                // above already adopts whatever range the aircraft actually enforces.
                var current = _vsPending ?? State.VerticalSpeed;
                var value = Math.Clamp(current + delta, -9900, 9900);
                _vsPending = value;
                _vsSent = value;
                Log.Info($"vs {current:F0} -> {value:F0} (delta {delta:F0})");
                Transmit("AP_VS_VAR_SET_ENGLISH", (uint)(int)value);
                break;
            }
            case "altitude":
            {
                var value = Math.Max(0, State.Altitude + delta);
                State.Altitude = value;
                Transmit("AP_ALT_VAR_SET_ENGLISH", (uint)value);
                break;
            }
            case "squawk":
            {
                var code = BcdToInt(State.SquawkBcd) + (int)delta;
                if (code < 0) code += 4096;
                code %= 4096;
                State.SquawkBcd = IntToBcd(code);
                Transmit("XPNDR_SET", (uint)State.SquawkBcd);
                break;
            }
            case "transponderMode":
            {
                // This unit's selector has five detents: OFF, SBY, TST, ON, ALT. It is a switch,
                // not a loop — past ALT stays at ALT, below OFF stays at OFF.
                var direction = Math.Sign(delta);
                if (direction == 0) break;

                var next = Math.Clamp(_transponderMode + direction, 0, 4);
                if (next == _transponderMode)
                {
                    Log.Debug($"transponder mode already at {ModeLabel(_transponderMode)}; ignoring further turn");
                    break;
                }

                _transponderMode = next;

                if (Capture != null) Capture.Add(("w:transponder state:1", (uint)_transponderMode));
                else _sim.SetDataInt32(2, _transponderMode);

                Log.Info($"transponder mode -> {_transponderMode} ({ModeLabel(_transponderMode)})");
                break;
            }
            case "nav1":
            case "nav2":
            {
                var isNav1 = binding.Absolute == "nav1";
                var simFrequency = isNav1 ? State.Nav1 : State.Nav2;
                var pending = isNav1 ? _nav1Pending : _nav2Pending;
                var lastSent = isNav1 ? _nav1Sent : _nav2Sent;

                // Seed from the sim on first use, and re-seed when the sim reports a value we
                // did not send (tuned in the cockpit). Otherwise keep our own value so the
                // detents accumulate instead of re-asserting the same frequency.
                var externallyChanged = lastSent.HasValue &&
                                        Math.Abs(simFrequency - lastSent.Value) > 0.0015 &&
                                        (pending == null || Math.Abs(simFrequency - pending.Value) > 0.0015);

                if (pending == null || externallyChanged)
                    pending = simFrequency > 0 ? simFrequency : pending ?? 0;

                var step = dial1 * binding.Step + dial2 * binding.FineStep;
                var updated = Math.Clamp(Math.Round(pending.Value + step, 3), 108.00, 136.99);

                if (isNav1) { _nav1Pending = updated; _nav1Sent = updated; }
                else { _nav2Pending = updated; _nav2Sent = updated; }

                Log.Debug($"nav{(isNav1 ? "1" : "2")} {pending.Value:F3} -> {updated:F3} (step {step:F3})");
                Transmit(isNav1 ? "NAV1_STBY_SET" : "NAV2_STBY_SET", FreqToBcd(updated));
                break;
            }
            default:
                Log.Warn($"unknown absolute action '{binding.Absolute}'");
                break;
        }
    }

    private static int Wrap(double value, int modulo)
    {
        var rounded = (int)Math.Round(value);
        var m = rounded % modulo;
        return m < 0 ? m + modulo : m;
    }

    /// <summary>Selector legend for this transponder unit: five detents, OFF through ALT.</summary>
    private static string ModeLabel(int mode) => mode switch
    {
        0 => "OFF",
        1 => "SBY",
        2 => "TST",
        3 => "ON",
        4 => "ALT",
        _ => $"?({mode})",
    };

    private static int BcdToInt(int bcd)
    {
        var value = 0;
        var factor = 1;
        while (bcd > 0 && factor <= 10000)
        {
            value += (bcd & 0xF) * factor;
            bcd >>= 4;
            factor *= 10;
        }
        return value;
    }

    private static int IntToBcd(int value)
    {
        var bcd = 0;
        var shift = 0;
        while (value > 0)
        {
            bcd |= (value % 10) << shift;
            value /= 10;
            shift += 4;
        }
        return bcd;
    }

    /// <summary>Frequency in MHz to the BCD form the standby-set events expect.</summary>
    private static uint FreqToBcd(double megahertz)
    {
        var hundredths = (int)Math.Round(megahertz * 20) / 20.0 * 100;
        return (uint)IntToBcd((int)Math.Round(hundredths + 0.1));
    }

    // ---- SimConnect -----------------------------------------------------

    private void SimLoop()
    {
        _sim.Load(_config.SimConnect);
        State.SimClientPath = _sim.ClientPath;
        State.SimClientVersion = _sim.ClientVersion;

        var delay = Math.Clamp(_config.SimConnect.ReconnectDelayMs, 250, 30000);

        while (!_stopping)
        {
            if (!_sim.Connected)
            {
                if (_sim.Open(_config.SimConnect))
                {
                    SetupDefinitions();
                    State.LastError = "";
                    Raise();
                }
                else
                {
                    var message = _sim.LastError ?? "not connected";
                    if (!string.Equals(State.LastError, message, StringComparison.Ordinal))
                    {
                        State.LastError = message;
                        Log.Warn($"SimConnect open failed: {message}");
                    }
                    Thread.Sleep(delay);
                    continue;
                }
            }

            var hr = _sim.CallDispatch();
            if (hr < 0)
            {
                var detail = _sim.ReadLastErrorText();
                Log.Warn($"CallDispatch failed (hr=0x{hr:X8}){ (string.IsNullOrWhiteSpace(detail) ? "" : $": {detail}") }");
                _sim.Close();
                Thread.Sleep(delay);
                continue;
            }

            // Heartbeat: proves the process is alive and how much it has moved.
            if ((DateTime.UtcNow - _lastHeartbeat).TotalSeconds >= 60)
            {
                _lastHeartbeat = DateTime.UtcNow;
                Log.Info($"heartbeat: {State.Reports} reports, {State.EventsSent} events, " +
                         $"device={State.DeviceConnected}, mode={State.Mode}");
            }

            Thread.Sleep(10);
        }
    }

    private void SetupDefinitions()
    {
        lock (_gate) { _mapped.Clear(); }

        // Definition 0: seven FLOAT64s.
        _sim.AddToDataDefinition(0, "COM STANDBY FREQUENCY:1", "MHz", SimConnectApi.DatatypeFloat64);
        _sim.AddToDataDefinition(0, "COM STANDBY FREQUENCY:2", "MHz", SimConnectApi.DatatypeFloat64);
        _sim.AddToDataDefinition(0, "NAV STANDBY FREQUENCY:1", "MHz", SimConnectApi.DatatypeFloat64);
        _sim.AddToDataDefinition(0, "NAV STANDBY FREQUENCY:2", "MHz", SimConnectApi.DatatypeFloat64);
        _sim.AddToDataDefinition(0, "AUTOPILOT HEADING LOCK DIR", "degrees", SimConnectApi.DatatypeFloat64);
        _sim.AddToDataDefinition(0, "NAV OBS:1", "degrees", SimConnectApi.DatatypeFloat64);
        _sim.AddToDataDefinition(0, "NAV OBS:2", "degrees", SimConnectApi.DatatypeFloat64);

        // Appended for the status panel: the ACTIVE frequencies and the barometer setting.
        // Indices 7..11, i.e. offsets 56..88.
        _sim.AddToDataDefinition(0, "COM ACTIVE FREQUENCY:1", "MHz", SimConnectApi.DatatypeFloat64);
        _sim.AddToDataDefinition(0, "COM ACTIVE FREQUENCY:2", "MHz", SimConnectApi.DatatypeFloat64);
        _sim.AddToDataDefinition(0, "NAV ACTIVE FREQUENCY:1", "MHz", SimConnectApi.DatatypeFloat64);
        _sim.AddToDataDefinition(0, "NAV ACTIVE FREQUENCY:2", "MHz", SimConnectApi.DatatypeFloat64);
        _sim.AddToDataDefinition(0, "KOHLSMAN SETTING MB", "millibars", SimConnectApi.DatatypeFloat64);

        // Definition 1: altitude, vertical speed, squawk and the six AP booleans.
        _sim.AddToDataDefinition(1, "AUTOPILOT ALTITUDE LOCK VAR", "feet", SimConnectApi.DatatypeFloat64);
        _sim.AddToDataDefinition(1, "AUTOPILOT VERTICAL HOLD VAR", "feet/minute", SimConnectApi.DatatypeFloat64);
        _sim.AddToDataDefinition(1, "TRANSPONDER CODE:1", "BCO16", SimConnectApi.DatatypeInt32);
        _sim.AddToDataDefinition(1, "AUTOPILOT MASTER", "Bool", SimConnectApi.DatatypeInt32);
        _sim.AddToDataDefinition(1, "AUTOPILOT HEADING LOCK", "Bool", SimConnectApi.DatatypeInt32);
        _sim.AddToDataDefinition(1, "AUTOPILOT NAV1 LOCK", "Bool", SimConnectApi.DatatypeInt32);
        _sim.AddToDataDefinition(1, "AUTOPILOT APPROACH HOLD", "Bool", SimConnectApi.DatatypeInt32);
        _sim.AddToDataDefinition(1, "AUTOPILOT ALTITUDE LOCK", "Bool", SimConnectApi.DatatypeInt32);
        _sim.AddToDataDefinition(1, "AUTOPILOT VERTICAL HOLD", "Bool", SimConnectApi.DatatypeInt32);

        // Definition 2 is written, never read: transponder mode is a writable SimVar.
        _sim.AddToDataDefinition(2, "TRANSPONDER STATE:1", "Enum", SimConnectApi.DatatypeInt32);

        // Also read it back, so the mode counter starts where the aircraft actually is
        // instead of assuming STBY. Appended last, so it lands at offset 44 of definition 1.
        _sim.AddToDataDefinition(1, "TRANSPONDER STATE:1", "Enum", SimConnectApi.DatatypeInt32);

        // CDI source: AP NAV hold cannot latch while the CDI follows the GPS (offset 48).
        _sim.AddToDataDefinition(1, "GPS DRIVES NAV1", "Bool", SimConnectApi.DatatypeInt32);

        // Which aircraft is loaded (offset 52, 256 bytes). The window otherwise cannot say what
        // it is flying, which matters because the autopilot's behaviour differs per aircraft.
        _sim.AddToDataDefinition(1, "TITLE", null!, SimConnectApi.DatatypeString256);

        _sim.RequestDataOnSimObject(0, 0);
        _sim.RequestDataOnSimObject(1, 1);
        Log.Info("SimConnect data definitions requested (1 Hz)");
    }

    private void OnSimObjectData(uint defineId, byte[] payload)
    {
        try
        {
            if (defineId == 0 && payload.Length >= 56)
            {
                State.Com1 = BitConverter.ToDouble(payload, 0);
                State.Com2 = BitConverter.ToDouble(payload, 8);
                State.Nav1 = BitConverter.ToDouble(payload, 16);
                State.Nav2 = BitConverter.ToDouble(payload, 24);
                State.Heading = BitConverter.ToDouble(payload, 32);
                State.Obs1 = BitConverter.ToDouble(payload, 40);
                State.Obs2 = BitConverter.ToDouble(payload, 48);

                if (payload.Length >= 96)
                {
                    State.Com1Active = BitConverter.ToDouble(payload, 56);
                    State.Com2Active = BitConverter.ToDouble(payload, 64);
                    State.Nav1Active = BitConverter.ToDouble(payload, 72);
                    State.Nav2Active = BitConverter.ToDouble(payload, 80);
                    State.Baro = BitConverter.ToDouble(payload, 88);
                }
            }
            else if (defineId == 1 && payload.Length >= 44)
            {
                State.Altitude = BitConverter.ToDouble(payload, 0);
                State.VerticalSpeed = BitConverter.ToDouble(payload, 8);
                State.SquawkBcd = BitConverter.ToInt32(payload, 16);
                State.ApMaster = BitConverter.ToInt32(payload, 20) != 0;
                State.ApHeading = BitConverter.ToInt32(payload, 24) != 0;
                State.ApNav1 = BitConverter.ToInt32(payload, 28) != 0;
                State.ApApr = BitConverter.ToInt32(payload, 32) != 0;
                State.ApAlt = BitConverter.ToInt32(payload, 36) != 0;
                State.ApVs = BitConverter.ToInt32(payload, 40) != 0;
                if (payload.Length >= 48)
                {
                    _transponderMode = BitConverter.ToInt32(payload, 44);
                    State.TransponderMode = _transponderMode;
                }
                if (payload.Length >= 52) State.CdiOnGps = BitConverter.ToInt32(payload, 48) != 0;

                if (payload.Length >= 308)
                {
                    var title = System.Text.Encoding.UTF8
                        .GetString(payload, 52, 256)
                        .TrimEnd('\0', ' ');
                    if (title != State.AircraftTitle)
                    {
                        State.AircraftTitle = title;
                        if (title.Length > 0) Log.Info($"aircraft: {title}");
                    }
                }

                // Reseed the VS selection from the sim whenever it differs from what we last
                // sent: an aircraft clamps the target to its own range, and if we keep
                // accumulating locally the panel drifts away from the cockpit for good.
                if (_vsSent is null || Math.Abs(State.VerticalSpeed - _vsSent.Value) > 1)
                    _vsPending = State.VerticalSpeed;

                // Three clicks and the sim still reports the same VS: this aircraft is not
                // accepting remote vertical speed. Say so once, rather than leaving a dead knob.
                if (_vsClickBurst >= 3 && !State.VsIgnoredByAircraft
                    && (DateTime.UtcNow - _vsLastClickAt).TotalSeconds < 5
                    && Math.Abs(State.VerticalSpeed - _vsBeforeBurst) < 1)
                {
                    State.VsIgnoredByAircraft = true;
                    Log.Warn("vertical speed is not accepted by this aircraft: the knob sends " +
                             "AP_VS_VAR_INC/DEC and the sim holds the same value. Set VS with the " +
                             "autopilot's own UP/DN buttons instead.");
                }
                else if (Math.Abs(State.VerticalSpeed - _vsBeforeBurst) >= 1)
                {
                    _vsClickBurst = 0;   // the aircraft is responding; reset the watch
                }
                PushLed();
            }

            Log.Debug($"sim data define={defineId} bytes={payload.Length}");

            Raise();
        }
        catch (Exception ex)
        {
            Log.Error("sim data parse failed", ex);
        }
    }

    /// <summary>Sends the six AP states as the device's LED mask, when it changes.</summary>
    private void PushLed()
    {
        var connection = _hid;
        if (connection == null) return;

        var mask = (byte)0;
        if (State.ApMaster) mask |= 0x01;
        if (State.ApHeading) mask |= 0x02;
        if (State.ApNav1) mask |= 0x04;
        if (State.ApApr) mask |= 0x08;
        if (State.ApAlt) mask |= 0x10;
        if (State.ApVs) mask |= 0x20;

        if (mask == _lastLed) return;
        _lastLed = mask;
        connection.Write(new[] { ReportId, mask });
        Log.Debug($"led mask -> 0x{mask:X2} (ap: {(State.ApMaster ? "master " : "")}{(State.ApHeading ? "hdg " : "")}{(State.ApNav1 ? "nav " : "")}{(State.ApApr ? "apr " : "")}{(State.ApAlt ? "alt " : "")}{(State.ApVs ? "vs" : "")})");
    }

    private uint IdFor(string name)
    {
        lock (_gate)
        {
            if (_eventIds.TryGetValue(name, out var id)) return id;
            id = _nextEventId++;
            _eventIds[name] = id;
            return id;
        }
    }

    private void Transmit(string eventName, uint data)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return;   // defect 4: never send unnamed events

        // Explain the one button that cannot work in this configuration, instead of failing quietly.
        if (eventName == "AP_NAV1_HOLD" && State.CdiOnGps)
            Log.Warn("AP NAV ignored by the sim: the CDI is on GPS, so NAV hold cannot capture. " +
                     "Switch the CDI to VLOC (NAV1) and press NAV again.");

        // Watch the VS knob: some autopilots (the C172's included) ignore remote vertical speed.
        // Record the value before a burst of clicks so the data cycle can tell if it moved.
        if (eventName is "AP_VS_VAR_INC" or "AP_VS_VAR_DEC" or "AP_VS_VAR_SET_ENGLISH")
        {
            if (_vsClickBurst == 0) _vsBeforeBurst = State.VerticalSpeed;
            _vsClickBurst++;
            _vsLastClickAt = DateTime.UtcNow;
        }

        if (Capture != null)
        {
            Capture.Add((eventName, data));
            return;
        }

        var id = IdFor(eventName);

        lock (_gate)
        {
            if (!_mapped.Contains(eventName))
            {
                if (!_sim.MapEvent(id, eventName)) return;
                _mapped.Add(eventName);
            }
        }

        _sim.Transmit(id, data);
        Log.Debug($"tx {eventName} id={id} data={data}");
        State.EventsSent++;
    }

    private void Raise()
    {
        try { Changed?.Invoke(); } catch { /* UI listener must not break the engine */ }
    }

    public string Snapshot()
    {
        var s = State;
        return string.Join(Environment.NewLine,
            $"device      : {(s.DeviceConnected ? s.DeviceName : "not connected")}",
            $"simconnect  : {(s.SimConnected ? "connected" : "not connected")}",
            $"client      : {s.SimClientPath} ({s.SimClientVersion})",
            $"mode        : {s.Mode} {s.ModeLabel}  shift={s.Shift}",
            $"field       : 0x{s.Field:X8}",
            $"dials       : {s.Dial1} / {s.Dial2}",
            $"com/nav     : {s.Com1:F3} {s.Com2:F3} / {s.Nav1:F3} {s.Nav2:F3}",
            $"hdg/obs     : {s.Heading:F0} / {s.Obs1:F0} {s.Obs2:F0}",
            $"alt/vs      : {s.Altitude:F0} ft / {s.VerticalSpeed:F0} fpm",
            $"squawk      : {BcdToInt(s.SquawkBcd):D4}",
            $"ap lamps    : {(s.ApMaster ? "M" : "-")}{(s.ApHeading ? "H" : "-")}{(s.ApNav1 ? "N" : "-")}{(s.ApApr ? "A" : "-")}{(s.ApAlt ? "L" : "-")}{(s.ApVs ? "V" : "-")}",
            $"counters    : {s.Reports} reports, {s.EventsSent} events");
    }

    /// <summary>
    /// Headless verification: bind the device, log every decoded report for a few seconds.
    /// Used to prove the engine against real hardware without the UI.
    /// </summary>
    public static int SelfTest(string outputPath, int seconds)
    {
        var config = AppConfig.LoadOrCreate();
        var engine = new PanelEngine(config);
        var lines = new List<string>();
        engine.Changed += () => lines.Add(engine.Snapshot());

        var info = Hid.Find(config.Device);
        if (info == null)
        {
            File.WriteAllText(outputPath, "NO DEVICE FOUND" + Environment.NewLine);
            return 2;
        }

        engine.Start();
        Thread.Sleep(seconds * 1000);
        var final = engine.Snapshot();
        engine.Dispose();

        File.WriteAllText(outputPath, $"device: {info.Display}{Environment.NewLine}{final}{Environment.NewLine}");
        return 0;
    }

    public void Dispose()
    {
        _stopping = true;
        _deviceLost.Set();
        try { _hid?.Dispose(); } catch { /* ignore */ }
        try { _simThread?.Join(1500); } catch { /* ignore */ }
        try { _hidThread?.Join(1500); } catch { /* ignore */ }
        _sim.Dispose();
        Log.Info("Magenta Bridge stopped");
    }

    /// <summary>Inputs the self-test asks the user to demonstrate, in a sensible order.</summary>
    private static readonly (string Key, string Prompt)[] SelfTestInputs =
    {
        ("dial1+", "Turn the LOWER knob clockwise"),
        ("dial1-", "Turn the LOWER knob anticlockwise"),
        ("dial2+", "Turn the UPPER knob clockwise"),
        ("dial2-", "Turn the UPPER knob anticlockwise"),
        ("btn 0x200", "Press the rotary knob switch (shift)"),
        ("btn 0x100", "Press the toggle switch (swap / IDENT)"),
        ("btn 0x4000", "Press the first lower button (AP / CDI)"),
        ("btn 0x8000", "Press the second lower button"),
        ("btn 0x10000", "Press the third lower button"),
        ("btn 0x20000", "Press the fourth lower button"),
        ("btn 0x40000", "Press the fifth lower button"),
        ("btn 0x80000", "Press the sixth lower button"),
    };

    private void Observe(string key) => _selfTestSeenThisRun.Add(key);

    public void ResetSelfTest()
    {
        _selfTestSeenThisRun.Clear();
        Log.Info("self-test started");
    }

    /// <summary>Human-readable pass/fail for every input the panel should be able to produce.</summary>
    public IReadOnlyList<string> SelfTestReport()
    {
        var lines = new List<string>();
        var missing = 0;

        foreach (var (key, prompt) in SelfTestInputs)
        {
            var seen = _selfTestSeenThisRun.Contains(key);
            if (!seen) missing++;
            lines.Add($"{(seen ? "PASS" : "MISS")}  {prompt}");
        }

        lines.Add("");
        lines.Add(missing == 0
            ? "All 12 inputs received from the panel."
            : $"{missing} of {SelfTestInputs.Length} inputs not seen yet — keep going, or the control may be faulty.");
        return lines;
    }
}
