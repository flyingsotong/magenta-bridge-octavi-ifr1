using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MagentaBridge.Core;
using MagentaBridge.Native;

namespace MagentaBridge;

public partial class MainWindow : Window
{
    private readonly PanelEngine _engine;
    private readonly Queue<string> _logLines = new();

    public MainWindow(PanelEngine engine)
    {
        _engine = engine;
        InitializeComponent();

        // Alt-Tab, the shell and accessibility all read this; keep it in one place.
        Title = Core.ProductInfo.WindowTitle;
        DeviceSubtitle.Text = Core.ProductInfo.DeviceName;

        // Show just the filename; the full path belongs in a tooltip, not in the footer.
        VersionText.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(2) ?? "0.1");

        _engine.Changed += () => Dispatcher.BeginInvoke(new Action(Refresh));
        Log.Line += OnLogLine;
        SyncStartupButton();
        SyncThemeButton();
        SyncTopmostButton();
        BuildPips();

        WelcomeStrip.Visibility = _engine.Config.WelcomeDismissed ? Visibility.Collapsed : Visibility.Visible;
        ApplyAccentButtonText();
        ChevronDiagnosticsRotate.Angle = -90; // collapsed: chevron points right
        // Grow the window for the first run so the extra cards never introduce a scrollbar.
        // Sized from the measured content (621 at 144 DPI) plus chrome and slack.
        if (WelcomeStrip.Visibility == Visibility.Visible) Height = 860;
        DetectVendorPlugin();

        // The window keeps its real icon for the taskbar and Alt-Tab; the custom caption draws
        // no icon or title, so nothing is duplicated on screen.

        Closed += (_, _) => Log.Line -= OnLogLine;

        // Proves the window is tall enough, and fixes it if not: content changes as cards appear
        // (welcome, plug-in conflict, expanded diagnostics), so a fixed height always drifts.
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            FitHeight();
            Log.Info(
                $"layout: content={BodyScroll.ExtentHeight:F0} viewport={BodyScroll.ViewportHeight:F0} " +
                $"scrollable={(BodyScroll.ExtentHeight > BodyScroll.ViewportHeight ? "YES" : "no")}");
        }));

        Refresh();
        AppendLog("Magenta Bridge window ready.");
    }

    private void ApplyWin11Frame()
    {
        var helper = new WindowInteropHelper(this);
        // Follow the *chosen* theme, not just the Windows setting, or the frame stays dark
        // while the content turns light.
        var light = _engine.Config.Theme switch
        {
            "light" => true,
            "dark" => false,
            _ => Dwm.IsLightTheme(),
        };
        Dwm.ApplyModernFrame(helper.Handle, !light);

        // Remove the small icon from the caption. At 16px the mark reads as a smudge, and the
        // window identifies itself with its own header instead.
        Native.Win32.SendMessage(helper.Handle, 0x0080 /* WM_SETICON */, IntPtr.Zero, IntPtr.Zero);

        // The window paints its own surface, so Mica is intentionally not requested:
        // mixing it with WPF's opaque composition produces a muddy, unreadable result.
    }

    private void OnLogLine(string line)
    {
        Dispatcher.BeginInvoke(new Action(() => AppendLog(line)));
    }

    private void AppendLog(string line)
    {
        _logLines.Enqueue(line);
        while (_logLines.Count > 200) _logLines.Dequeue();
        LogText.Text = string.Join(Environment.NewLine, _logLines);
        LogScroll.ScrollToEnd();
    }

    private void Refresh()
    {
        var s = _engine.State;

        DeviceDot.Fill = (Brush)FindResource(s.DeviceConnected ? "Brush.Good" : "Brush.Bad");
        DeviceText.Text = s.DeviceConnected
            ? (string.IsNullOrEmpty(s.DeviceSerial) ? "IFR-1" : $"IFR-1 {s.DeviceSerial}")
            : "IFR-1";

        SimDot.Fill = (Brush)FindResource(s.SimConnected ? "Brush.Good" : "Brush.Bad");
        SimText.Text = s.SimConnected ? "MSFS connected" : "MSFS";

        ModeText.Text = s.Mode >= 0 ? $"mode {s.Mode + 1} of 8" : "press any button";
        ModeLabelText.Text = s.Mode >= 0 ? s.ModeLabel : string.Empty;
        // The chip is always present so the feature is discoverable; it only lights when armed.
        // When armed it names the shifted function, so "COM1  > HDG" says what you now get.
        var shiftTo = s.Mode >= 0 ? ShiftFunction(s.Mode) : string.Empty;
        ShiftChip.Text = s.Shift
            ? (shiftTo.Length > 0 ? $"> {shiftTo}" : "SHIFT")
            : (shiftTo.Length > 0 ? "SHIFT" : "SHIFT");
        ShiftChip.Opacity = s.Shift ? 1.0 : 0.6;
        ShiftChip.Foreground = (Brush)FindResource(s.Shift ? "Brush.Accent" : "Brush.Muted");
        // The dial readout stays put: hiding it until a turn made the header jump vertically.
        Dial1Text.Text = s.Dial1.ToString();
        Dial2Text.Text = s.Dial2.ToString();

        // A report arrived: pulse the activity dot, but only while the sim is connected —
        // a pulsing dot beside a red MSFS dot otherwise implies a link that does not exist.
        if (s.Reports != _lastReports)
        {
            _lastReports = s.Reports;
            if (s.SimConnected)
                ActivityDot.BeginAnimation(OpacityProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.18, TimeSpan.FromMilliseconds(260)));
        }

        SetLamp(LampMaster, LampMasterText, s.ApMaster);
        SetLamp(LampHdg, LampHdgText, s.ApHeading);
        SetLamp(LampNav, LampNavText, s.ApNav1);
        SetLamp(LampApr, LampAprText, s.ApApr);
        SetLamp(LampAlt, LampAltText, s.ApAlt);
        SetLamp(LampVs, LampVsText, s.ApVs);

        // ALT/VS live in the autopilot card; hidden entirely when the sim is silent.
        ApValues.Visibility = s.SimConnected ? Visibility.Visible : Visibility.Collapsed;
        AltText.Text = s.SimConnected
            ? s.Altitude.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " ft"
            : "-";
        VsText.Text = s.SimConnected
            ? "VS " + s.VerticalSpeed.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
            : "-";

        ClientText.Text = string.IsNullOrEmpty(s.SimClientPath)
            ? "SimConnect client: not found"
            : $"SimConnect client: {s.SimClientPath}  ({s.SimClientVersion})";

        // Give the AP targets somewhere visible: manual 3.5 notes some autopilots give no
        // vertical-speed feedback at all, so the panel would otherwise look dead.
        var ap = s.Mode == 6
            ? $" · AP ALT {s.Altitude:F0} ft · VS {(s.VerticalSpeed >= 0 ? "+" : "")}{s.VerticalSpeed:F0} fpm" +
              (s.VsIgnoredByAircraft ? " (set VS with the AP's UP/DN — this aircraft ignores remote VS)" : "")
            : string.Empty;

        CounterText.Text = $"{s.Reports} inputs · CDI {(s.CdiOnGps ? "GPS" : "VLOC")}" +
                           (s.EventsSent > 0 ? $" · {s.EventsSent} sent" : "") + ap;
        // One sentence answering "is it working?" without making the user decode two dots.
        // The aircraft belongs here: it is the thing being flown, and it explains why the
        // autopilot behaves differently from the last one.
        var aircraft = s.SimConnected && !string.IsNullOrWhiteSpace(s.AircraftTitle)
            ? " · " + TrimTitle(s.AircraftTitle)
            : "";
        StatusLine.Text = (s.DeviceConnected ? "Panel connected" : "Panel not connected") +
                          " · " +
                          (s.SimConnected ? "simulator connected" : "simulator not running") +
                          aircraft;

        SetStack(s);
        UpdatePips(s);
        ErrorText.Text = s.LastError;
        ErrorText.Visibility = string.IsNullOrWhiteSpace(s.LastError) ? Visibility.Collapsed : Visibility.Visible;

        // Content changes after load too (self-test report, diagnostics, AP values), so re-fit
        // once the layout has settled rather than only at startup.
        if (!_fitQueued)
        {
            _fitQueued = true;
            Dispatcher.BeginInvoke(new Action(() => { _fitQueued = false; FitHeight(); }),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private readonly System.Windows.Shapes.Ellipse[] _pips = new System.Windows.Shapes.Ellipse[8];

    /// <summary>Eight pips showing where you are in the context-button ring.</summary>
    private void BuildPips()
    {
        for (var i = 0; i < _pips.Length; i++)
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 5,
                Height = 5,
                Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0),
                Fill = (Brush)FindResource("Brush.Stroke"),
            };
            _pips[i] = dot;
            ModePips.Children.Add(dot);
        }
    }

    private void UpdatePips(PanelState s)
    {
        // Muted at partial opacity reads on both palettes; the stroke colour disappears
        // against the light theme's near-white background.
        for (var i = 0; i < _pips.Length; i++)
        {
            var active = i == s.Mode;
            _pips[i].Fill = (Brush)FindResource(active ? "Brush.Accent" : "Brush.Muted");
            _pips[i].Opacity = active ? 1.0 : 0.45;
        }
    }

    /// <summary>
    /// The radio stack: active frequency emphasised, standby muted, COM/NAV/navigation in
    /// visually separate groups. Numbers use tabular figures so the columns stay aligned.
    /// </summary>
    private void SetStack(PanelState s)
    {
        // Invariant formatting: a German or French locale would otherwise render 124,850 and
        // break both the readout and its matching with the simulator's dot-decimal values.
        static string Mhz(double mhz) => mhz > 0
            ? mhz.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
            : "---";
        static string Deg(double d) =>
            d.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "\u00B0";

        var live = s.SimConnected;

        // One calm line instead of six rows of dashes when the simulator is silent.
        StackGrid.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        if (!live)
        {
            EmptyText.Text = s.DeviceConnected
                ? "Panel connected. Start Microsoft Flight Simulator and load a flight."
                : "Waiting for the panel. Check the USB cable.";
        }

        Com1A.Text = live ? Mhz(s.Com1Active) : "---";
        Com1S.Text = live ? Mhz(s.Com1) : "---";
        Com2A.Text = live ? Mhz(s.Com2Active) : "---";
        Com2S.Text = live ? Mhz(s.Com2) : "---";
        Nav1A.Text = live ? Mhz(s.Nav1Active) : "---";
        Nav1S.Text = live ? Mhz(s.Nav1) : "---";
        Nav2A.Text = live ? Mhz(s.Nav2Active) : "---";
        Nav2S.Text = live ? Mhz(s.Nav2) : "---";

        Nav1X.Text = live ? $"OBS {Deg(s.Obs1)}" : string.Empty;
        Nav2X.Text = live ? $"OBS {Deg(s.Obs2)}" : string.Empty;
        HdgV.Text = live ? Deg(s.Heading) : "---";
        BaroV.Text = live ? $"BARO {s.Baro:F1}" : string.Empty;
        XpdrCode.Text = live ? Bcd(s.SquawkBcd) : "----";

        // Colour carries meaning: OFF is inert, ALT is the armed state.
        var mode = s.TransponderMode;
        XpdrMode.Text = live ? mode switch
        {
            0 => "OFF",
            1 => "SBY",
            2 => "TST",
            3 => "ON",
            4 => "ALT",
            _ => "?",
        } : string.Empty;

        XpdrMode.Foreground = (Brush)FindResource(
            !live ? "Brush.Muted" : mode == 4 ? "Brush.Good" : mode == 2 ? "Brush.Warn" : "Brush.Muted");
    }

    private static string Bcd(int bcd)
    {
        var value = 0;
        var factor = 1;
        while (bcd > 0 && factor <= 10000)
        {
            value += (bcd & 0xF) * factor;
            bcd >>= 4;
            factor *= 10;
        }
        return value.ToString("D4");
    }

    /// <summary>An indicator dot plus its label: lit dots use the accent, unlit stay muted.</summary>
    private void SetLamp(System.Windows.Shapes.Ellipse dot, TextBlock text, bool on)
    {
        // SetResourceReference, not FindResource: an assigned brush instance does not follow a
        // theme change, which left the lamps in the previous palette until the next input.
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, on ? "Brush.Good" : "Brush.Stroke");
        text.Foreground = (Brush)FindResource(on ? "Brush.Text" : "Brush.Muted");
        text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
    }

    /// <summary>Diagnostics start collapsed so the window fits without scrolling.</summary>
    private void OnToggleDiagnostics(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var open = DiagnosticsBody.Visibility != Visibility.Visible;
        DiagnosticsBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ChevronDiagnosticsRotate.Angle = open ? 0 : -90;
        FitHeight();
    }

    /// <summary>Settings live in a full-window overlay, as in Trim Zero.</summary>
    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        SyncStartupButton();
        SyncThemeButton();
        SyncTopmostButton();
        RefreshSettingsPanel();
        SettingsOverlay.Visibility = Visibility.Visible;
        SettingsOverlay.Focus();
        Log.Info("settings opened");

        // Size the window to the settings content rather than letting it scroll.
        Dispatcher.BeginInvoke(new Action(FitToSettings), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Grows the window to the settings content. Measured after layout, because the sections
    /// only have a desired size once they are visible.
    /// </summary>
    private void FitToSettings()
    {
        var width = SettingsContent.ActualWidth > 0
            ? SettingsContent.ActualWidth
            : Math.Max(320, ActualWidth - 88);

        // Measure unconstrained: inside a star-sized row the panel only reports the space it was
        // given, which is exactly the clipped height we are trying to avoid.
        SettingsContent.Measure(new Size(width, double.PositiveInfinity));
        var contentHeight = SettingsContent.DesiredSize.Height;
        if (contentHeight <= 0) return;

        const int caption = 36, pageMargins = 40, overlayPadding = 38, headerRow = 36, footerRow = 42;
        var needed = caption + pageMargins + overlayPadding + headerRow + contentHeight + footerRow + 16;
        var limit = SystemParameters.WorkArea.Height - 40;
        Height = Math.Min(needed, limit);

        Log.Debug($"settings fit: content={contentHeight:F0} needed={needed:F0} height={Height:F0}");
    }

    private void OnCloseSettings(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        Log.Info("settings closed");
        FitHeight();
    }

    /// <summary>Test from inside settings: close the overlay first so the prompts are visible.</summary>
    private void OnTestFromSettings(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        OnVerifyPanel(sender, e);
    }

    private void OnMagenta(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OpenUrl("https://magentadebrief.com");

    private void OnDonate(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OpenUrl("https://magentadebrief.com/donate");

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"could not open {url}: {ex.Message}");
        }
    }

    /// <summary>
    /// Renders a path relative to %APPDATA% or the profile. Shorter, and it avoids printing the
    /// Windows username into the UI — and therefore into every screenshot people share.
    /// </summary>
    private static string ShortenPath(string path)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData) &&
            path.StartsWith(appData, StringComparison.OrdinalIgnoreCase))
            return "%APPDATA%" + path.Substring(appData.Length);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile) &&
            path.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
            return "%USERPROFILE%" + path.Substring(profile.Length);

        return path;
    }

    /// <summary>
    /// Sim titles carry a lot of packaging noise ("Asobo Cessna Skyhawk G1000 Asobo"). Keep the
    /// readable middle: drop a leading vendor word and a trailing duplicate.
    /// </summary>
    private static string TrimTitle(string title)
    {
        var t = title.Trim();
        foreach (var noise in new[] { "Asobo ", "Microsoft ", "asobo ", "microsoft " })
            if (t.StartsWith(noise, StringComparison.OrdinalIgnoreCase) && t.Length > noise.Length)
            { t = t.Substring(noise.Length); break; }

        var half = t.Length / 2;
        var tail = t.Substring(t.Length - half);
        if (half > 3 && t.Substring(0, half).TrimEnd().EndsWith(tail.Trim(), StringComparison.OrdinalIgnoreCase))
            t = t.Substring(0, t.Length - half).TrimEnd();

        return t.Length > 48 ? t.Substring(0, 47) + "…" : t;
    }

    /// <summary>Everything in the overlay that reflects current state rather than a setting.</summary>
    private void RefreshSettingsPanel()
    {
        var s = _engine.State;

        SettingsDeviceText.Text = s.DeviceConnected
            ? (string.IsNullOrEmpty(s.DeviceSerial) ? "IFR-1 connected" : $"IFR-1 connected  -  {s.DeviceSerial}")
            : "IFR-1 not connected";

        SettingsDeviceHint.Text = s.DeviceConnected
            ? "Press any button on the panel to teach the bridge where you are."
            : "Check the USB cable. Only one program can use the panel at a time.";

        SettingsClientText.Text = string.IsNullOrEmpty(s.SimClientPath)
            ? "SimConnect client: not found"
            : $"SimConnect client: {System.IO.Path.GetFileName(s.SimClientPath)}  ({s.SimClientVersion})";

        var config = _engine.Config.Path;
        SettingsConfigText.Text = string.IsNullOrEmpty(config)
            ? "Config: built-in defaults"
            : $"Config: {ShortenPath(config)}";

        SettingsVersionText.Text = VersionText.Text;

        // Theme-appropriate lockup: the supplied artwork is white-on-dark and dark-on-light.
        var light = _engine.Config.Theme switch
        {
            "light" => true,
            "dark" => false,
            _ => Native.Dwm.IsLightTheme(),
        };
        var file = light ? "magenta-lockup-light.png" : "magenta-lockup-dark.png";
        MagentaLockup.Source = new System.Windows.Media.Imaging.BitmapImage(
            new Uri($"pack://application:,,,/Assets/{file}"));
    }

    private System.Windows.Threading.DispatcherTimer? _selfTestTimer;
    private long _lastReports = -1;
    private bool _fitQueued;
    private bool _selfTestActive;

    /// <summary>Same state-naming pattern as the other settings buttons.</summary>
    private void SyncTopmostButton()
    {
        var on = _engine.Config.AlwaysOnTop;
        Topmost = on;
        TopmostButton.Content = on ? "On" : "Off";
        TopmostButton.ToolTip = on
            ? "Click to let other windows cover the bridge"
            : "Click to keep the bridge above other windows";
    }

    private void OnToggleTopmost(object sender, RoutedEventArgs e)
    {
        _engine.Config.AlwaysOnTop = !_engine.Config.AlwaysOnTop;
        try { _engine.Config.Save(); }
        catch (Exception ex) { Log.Warn($"could not save the config: {ex.Message}"); }

        SyncTopmostButton();
        Log.Info($"always on top: {_engine.Config.AlwaysOnTop}");
    }

    /// <summary>Same pattern as the theme button: names the current state, click to switch.</summary>
    private void SyncStartupButton()
    {
        var on = Startup.IsEnabled;
        StartupButton.Content = on ? "On" : "Off";
        StartupButton.ToolTip = on
            ? "Click to stop launching at sign-in"
            : "Click to launch at sign-in, minimised to the tray";
    }

    /// <summary>
    /// A single button that names the CURRENT theme. "Light theme" as a checkbox was ambiguous:
    /// it read as both a state and an action, depending on whether it was ticked.
    /// </summary>
    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        var light = _engine.Config.Theme switch
        {
            "light" => true,
            "dark" => false,
            _ => Native.Dwm.IsLightTheme(),
        };

        _engine.Config.Theme = light ? "dark" : "light";
        try { _engine.Config.Save(); }
        catch (Exception ex) { Log.Warn($"could not save the config: {ex.Message}"); }

        (Application.Current as App)?.ReapplyTheme();
        ApplyWin11Frame();
        ApplyAccentButtonText();
        SyncThemeButton();
        Refresh();          // repaint anything holding a resolved brush

        // The Magenta lockup is a bitmap with a light and a dark variant; it only swaps inside
        // RefreshSettingsPanel, so a toggle with the overlay open left white-on-white.
        if (SettingsOverlay.Visibility == Visibility.Visible) RefreshSettingsPanel();
    }

    private void SyncThemeButton()
    {
        var light = _engine.Config.Theme switch
        {
            "light" => true,
            "dark" => false,
            _ => Native.Dwm.IsLightTheme(),
        };

        ThemeButton.Content = light ? "Light" : "Dark";
        ThemeButton.ToolTip = light ? "Switch to the dark theme" : "Switch to the light theme";
    }

    private void OnToggleStartup(object sender, RoutedEventArgs e)
    {
        var want = !Startup.IsEnabled;
        if (!Startup.Apply(want))
        {
            SyncStartupButton();   // the registry refused; do not claim otherwise
            return;
        }

        _engine.Config.StartWithWindows = want;
        try
        {
            _engine.Config.Save();
        }
        catch (Exception ex)
        {
            Log.Warn($"could not save the config: {ex.Message}");
        }

        SyncStartupButton();
    }

    /// <summary>
    /// Guided check: ask the user to exercise each control and tick them off as reports arrive.
    /// Turns "the panel doesn't work" into "these inputs were never received".
    /// </summary>
    private void OnVerifyPanel(object sender, RoutedEventArgs e)
    {
        if (_selfTestActive)
        {
            _selfTestActive = false;
            _selfTestTimer?.Stop();
            SelfTestText.Text = string.Join(Environment.NewLine, _engine.SelfTestReport());
            return;
        }

        _selfTestActive = true;
        _engine.ResetSelfTest();
        SelfTestText.Visibility = Visibility.Visible;

        string Render() => string.Join(Environment.NewLine, _engine.SelfTestReport());

        SelfTestText.Text = "Press each control now — this list ticks off as inputs arrive."
                            + Environment.NewLine + Environment.NewLine + Render();

        _selfTestTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _selfTestTimer.Tick += (_, _) =>
        {
            SelfTestText.Text = "Press each control now — press Verify panel again to finish."
                                + Environment.NewLine + Environment.NewLine + Render();
        };
        _selfTestTimer.Start();
    }

    /// <summary>Esc hides to tray; Ctrl+L copies the log for a support report.</summary>
    /// <summary>
    /// Accent buttons carry their own text colour: white on the light palette (which darkens the
    /// accent) and near-black on the dark palette (which lightens it). Set in code because a
    /// dynamic-resource lookup here kept resolving to the theme's body text colour.
    /// </summary>
    private void ApplyAccentButtonText()
    {
        var light = _engine.Config.Theme switch
        {
            "light" => true,
            "dark" => false,
            _ => Native.Dwm.IsLightTheme(),
        };

        var onAccent = (Brush)FindResource("Brush.OnAccent");
        PrimaryActionText.Foreground = onAccent;
        if (PrimaryWelcomeText != null) PrimaryWelcomeText.Foreground = onAccent;
        Log.Debug("accent button text follows Brush.OnAccent");
    }

    /// <summary>The function shift gives this mode, named as the panel labels it.</summary>
    private string ShiftFunction(int mode)
    {
        var binding = _engine.Config.Mode(mode).ShiftDial1;
        if (binding == null) return string.Empty;

        if (binding.Inc is { Length: > 0 } inc && inc.Any(e => e.Contains("KOHLSMAN", StringComparison.OrdinalIgnoreCase)))
            return "BARO";

        return binding.Absolute switch
        {
            "heading" => "HDG",
            "obs1" => "CRS1",
            "obs2" => "CRS2",
            "transponderMode" => "MODE",
            null => string.Empty,
            var other => other.ToUpperInvariant(),
        };
    }

    /// <summary>Two programs cannot share the panel; the vendor plug-in holds it exclusively.</summary>
    private void DetectVendorPlugin()
    {
        try
        {
            var running = System.Diagnostics.Process.GetProcesses()
                .Any(p => p.ProcessName.Contains("Octavi_MSFS", StringComparison.OrdinalIgnoreCase));

            ConflictStrip.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (running) Log.Warn("Octavi's own plug-in is running; it will hold the panel");
        }
        catch
        {
            ConflictStrip.Visibility = Visibility.Collapsed;
        }
    }

    private void OnRecheckPlugin(object sender, RoutedEventArgs e) => DetectVendorPlugin();

    /// <summary>
    /// Closes the vendor plug-in on the user's behalf. It has no documented exit API, so this
    /// asks it to close and, failing that, terminates it — the card exists because the two
    /// programs cannot share the panel.
    /// </summary>
    private void OnCloseVendorPlugin(object sender, RoutedEventArgs e)
    {
        var closed = 0;

        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            if (!process.ProcessName.Contains("Octavi_MSFS", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                if (process.CloseMainWindow())
                {
                    if (!process.WaitForExit(2500)) process.Kill(entireProcessTree: true);
                }
                else
                {
                    process.Kill(entireProcessTree: true);
                }

                closed++;
                Log.Info($"closed the Octavi plug-in (pid {process.Id})");
            }
            catch (Exception ex)
            {
                Log.Warn($"could not close the Octavi plug-in: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        if (closed == 0) Log.Info("no Octavi plug-in process was found to close");
        DetectVendorPlugin();

        // The panel may now be free; the engine reopens a silent handle by itself.
        Log.Info("panel should be free — controls will resume if the plug-in was holding it");
    }

    /// <summary>
    /// Sizes the window to its content so the body never scrolls. Measured, not guessed: the
    /// difference between window height and body viewport is the chrome (header, footer, margins).
    /// </summary>
    private void FitHeight()
    {
        if (BodyScroll.ViewportHeight <= 0) return;

        var chrome = ActualHeight - BodyScroll.ViewportHeight;
        var needed = BodyScroll.ExtentHeight + chrome + 12;   // slack so rounding never shows a bar
        var limit = SystemParameters.WorkArea.Height - 40;

        if (needed > limit) needed = limit;   // never exceed the desktop
        if (Math.Abs(Height - needed) > 2) Height = needed;
    }

    private void OnDismissWelcome(object sender, RoutedEventArgs e)
    {
        WelcomeStrip.Visibility = Visibility.Collapsed;
        Height = 606;
        _engine.Config.WelcomeDismissed = true;        try { _engine.Config.Save(); }
        catch (Exception ex) { Log.Warn($"could not save the config: {ex.Message}"); }
    }

    /// <summary>Opens the user's mail client with a pre-filled subject, so reports arrive useful.</summary>
    private void OnSendFeedback(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = $"mailto:alan@magentadebrief.com?subject=Magenta%20Bridge%20feedback%20(v{version})",
                UseShellExecute = true,
            });
            Log.Info("opened the mail client for feedback");
        }
        catch (Exception ex)
        {
            Log.Warn($"could not open a mail client: {ex.Message}");
        }
    }

    private void OnMinimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximise(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) =>
        Application.Current.Shutdown();

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            // Escape steps back one level: close the overlay first, hide only from the main view.
            if (SettingsOverlay.Visibility == Visibility.Visible)
                OnCloseSettings(this, new RoutedEventArgs());
            else
                OnHide(this, new RoutedEventArgs());

            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.L && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, _logLines));
                Log.Info("log copied to the clipboard");
            }
            catch (Exception ex)
            {
                Log.Warn($"could not copy the log: {ex.Message}");
            }

            e.Handled = true;
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Log.Directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("could not open the log folder", ex);
        }
    }

    private void OnReloadConfig(object sender, RoutedEventArgs e)
    {
        Log.Info("config reload requested from the window");
        ((App)Application.Current).ReloadConfig();
    }

    private void OnHide(object sender, RoutedEventArgs e) => Hide();
}
