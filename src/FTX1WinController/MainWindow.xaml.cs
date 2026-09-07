using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FTX1WinController.ViewModels;

namespace FTX1WinController;

public partial class MainWindow : Window
{
    // Bump by 1 each time a build goes out for the user to test (2026-09-07:
    // "start using version numbers... G1INU v(incremental version
    // numbers)") — G1INU is the user's callsign. Plain hand-maintained
    // integer, not tied to the .csproj/assembly version, since it only
    // needs to track "which build did the user last test," same purpose
    // the build timestamp below already served.
    private const int AppVersion = 1;

    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Closing += (_, _) => _viewModel.Dispose();

        PositionOnSecondaryMonitorIfPresent();

        // The running exe's own last-write time — what's actually loaded,
        // not a hand-maintained version number that has to be remembered on
        // every change. Same idea as the Mac app's window title and
        // FTX1Bridge's startup log line: a stale build should be visible at
        // a glance, not assumed — this is what a rebuild silently not
        // taking effect (e.g. an old process/session still running the
        // previous build) looks like from the title bar. Kept alongside
        // AppVersion above (not replaced by it, per user request
        // 2026-09-07) — the version number identifies which release this
        // is; the timestamp still catches an un-rebuilt/stale exe.
        //
        // Uses Process.MainModule.FileName rather than
        // Assembly.GetExecutingAssembly().Location: under PublishSingleFile
        // the assembly is embedded in the exe and Location always returns
        // "" (silently falling back to "unknown build" every time) — the
        // process's own module path still resolves correctly either way.
        var location = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        var buildTime = System.IO.File.Exists(location)
            ? System.IO.File.GetLastWriteTime(location).ToString("yyyy-MM-dd HH:mm")
            : "unknown build";
        Title = $"G1INU v{AppVersion} — build {buildTime}";

        // UTC clock in the top-right (see CLAUDE.md layout row 1) — purely
        // local display, not bridge-synced, so it lives here rather than in
        // MainViewModel.
        ClockText.Text = DateTime.UtcNow.ToString("HH:mm:ss");
        var clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clockTimer.Tick += (_, _) => ClockText.Text = DateTime.UtcNow.ToString("HH:mm:ss");
        clockTimer.Start();
    }

    // Multi-monitor placement (laptop + a larger attached monitor is the
    // user's real setup, confirmed 2026-09-05): the app always launched on
    // whichever screen the OS put it on and had to be dragged onto the
    // larger one by hand every time — and a laptop screen short enough to
    // clip the window's own title bar made even that drag impossible.
    // Enumerated directly via user32 (EnumDisplayMonitors/GetMonitorInfo)
    // rather than System.Windows.Forms.Screen — adding a WinForms
    // reference just for this pulls in a parallel set of Color/Point/
    // Brush/Application/MouseEventArgs types that collide with WPF's own
    // across the whole project (confirmed: 12 CS0104 ambiguous-reference
    // errors project-wide).
    //
    // Picks the monitor with the LARGEST working area, not "whichever
    // isn't primary" — this user's external monitor is actually their
    // Windows-designated PRIMARY display (confirmed 2026-09-05), so
    // "non-primary" picked their small laptop screen instead, the exact
    // opposite of the goal. Area is a proxy for "the one you'd actually
    // want to use," independent of whichever one Windows happens to flag
    // primary. Sized/centered within THAT monitor's working area rather
    // than the XAML-declared Height/Width, which assumed a single,
    // adequately tall screen.
    private void PositionOnSecondaryMonitorIfPresent()
    {
        var monitors = new List<NativeMethods.MONITORINFO>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref NativeMethods.RECT _, IntPtr _) =>
        {
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (NativeMethods.GetMonitorInfo(hMonitor, ref info)) monitors.Add(info);
            return true;
        }, IntPtr.Zero);
        if (monitors.Count == 0) return;

        var work = monitors[0].rcWork;
        var bestArea = (long)(work.Right - work.Left) * (work.Bottom - work.Top);
        foreach (var info in monitors)
        {
            var area = (long)(info.rcWork.Right - info.rcWork.Left) * (info.rcWork.Bottom - info.rcWork.Top);
            if (area > bestArea)
            {
                bestArea = area;
                work = info.rcWork;
            }
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var left = work.Left / dpi.DpiScaleX;
        var top = work.Top / dpi.DpiScaleY;
        var availWidth = (work.Right - work.Left) / dpi.DpiScaleX;
        var availHeight = (work.Bottom - work.Top) / dpi.DpiScaleY;

        if (Width > availWidth) Width = availWidth - 20;
        if (Height > availHeight) Height = availHeight - 20;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left + (availWidth - Width) / 2;
        Top = top + (availHeight - Height) / 2;
    }

    // FUNC-grid choice cells (IPO/AGC/ANT — FuncChoiceCellTemplate in
    // MainWindow.xaml): picking an option should close the popover, same
    // as the real radio's own touchscreen behavior. The option buttons
    // live inside a Popup, so there's no ElementName path back to the
    // ToggleButton that opened it — walk up the visual tree instead.
    private void ChoiceOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject start && FindAncestor<Popup>(start) is { } popup)
        {
            popup.IsOpen = false;
        }
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        var current = start;
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    // FUNC-grid popup sliders (FuncSliderPopupCellTemplate + the D-Level/RF
    // Power bespoke cells): the SET only fires once on drag release, not on
    // every drag pixel, to avoid flooding the bridge with rapid CAT
    // commands — see the FuncSliderPopupCellTemplate comment in
    // MainWindow.xaml. The popup deliberately stays open afterward (no
    // popup.IsOpen = false here) so multiple drags don't require reopening
    // it each time.
    private async void FuncSlider_DragCompleted(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider { DataContext: IntSettingViewModel vm } slider)
        {
            await vm.SetValueDirectAsync((int)Math.Round(slider.Value));
        }
    }

    private async void DLevelSlider_DragCompleted(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider && DataContext is MainViewModel vm)
        {
            await vm.SetDLevelDirectAsync(slider.Value);
        }
    }

    private async void RfPowerSlider_DragCompleted(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider && DataContext is MainViewModel vm)
        {
            await vm.SetRfPowerDirectAsync((int)Math.Round(slider.Value));
        }
    }

    // VFO dial (VfoDialView, VFO Controls panel): each detent fires this
    // once, +1 clockwise / -1 counter-clockwise. Steps are queued and
    // drained ONE AT A TIME via StepFrequencyAsync (awaited, unlike
    // VfoStepUp/DownCommand's fire-and-forget RelayCommand.Execute) —
    // a fast drag can emit a dozen Stepped events from a single
    // OnMouseMove callback, and firing that many concurrent bridge calls
    // backed up the semaphore badly enough to look like the app had
    // locked up (confirmed on hardware 2026-09-04). New Stepped events
    // that arrive while a step is already in flight just add to the
    // pending count instead of starting another concurrent call.
    private int _pendingVfoSteps;
    private bool _vfoStepInFlight;

    private async void VfoDial_Stepped(object? sender, int direction)
    {
        if (DataContext is not MainViewModel vm) return;
        _pendingVfoSteps += direction;
        if (_vfoStepInFlight) return;

        _vfoStepInFlight = true;
        try
        {
            while (_pendingVfoSteps != 0)
            {
                var step = Math.Sign(_pendingVfoSteps);
                _pendingVfoSteps -= step;
                await vm.StepFrequencyAsync(step);
            }
        }
        finally
        {
            _vfoStepInFlight = false;
        }
    }

    // QMB quadrant (VFO Controls dial cluster): tap = Recall, press-and-
    // hold = Store — a plain Border with manual mouse handling rather than
    // a Button, since ButtonBase only exposes one Click gesture and this
    // needs to distinguish two. CaptureMouse so a release outside the
    // Border's bounds (a fast/sloppy tap) still fires MouseUp here instead
    // of being swallowed by whatever's underneath.
    private DispatcherTimer? _qmbHoldTimer;
    private bool _qmbHoldFired;

    private void QmbQuadrant_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not UIElement el) return;
        el.CaptureMouse();
        _qmbHoldFired = false;
        _qmbHoldTimer?.Stop();
        _qmbHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _qmbHoldTimer.Tick += (_, _) =>
        {
            _qmbHoldTimer?.Stop();
            _qmbHoldFired = true;
            if (DataContext is MainViewModel vm && vm.QmbStoreCommand.CanExecute(null)) vm.QmbStoreCommand.Execute(null);
        };
        _qmbHoldTimer.Start();
        e.Handled = true;
    }

    private void QmbQuadrant_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement el) el.ReleaseMouseCapture();
        _qmbHoldTimer?.Stop();
        if (!_qmbHoldFired && DataContext is MainViewModel vm && vm.QmbRecallCommand.CanExecute(null))
        {
            vm.QmbRecallCommand.Execute(null);
        }
        e.Handled = true;
    }

    // PLAY LIST popover (MainWindow.xaml, PLAY cell): refresh the
    // recordings list every time it's opened, same as tapping Refresh used
    // to, so it doesn't show stale entries from the last ~2s poll tick.
    private void PlayListPopup_Opened(object sender, EventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.RefreshRecordingsCommand.CanExecute(null))
        {
            vm.RefreshRecordingsCommand.Execute(null);
        }
    }
}
