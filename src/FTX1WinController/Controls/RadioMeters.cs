using System.Windows;
using System.Windows.Media;

namespace FTX1WinController.Controls;

public enum ArcMeterKind { SMeter, PowerOutput }

/// One S-meter/Power-Output tick — see CLAUDE.md's "S-meter / power-meter
/// gauge" section. `Fraction` is a hand-placed position along the bow
/// (0=left, 1=right), not derived from a raw CAT value — there is no
/// published raw-to-real-unit calibration curve from Yaesu, so tick
/// positions here are a visual approximation of the Mac app's own
/// reference screenshot, not a calibrated scale.
internal readonly record struct ArcMeterTick(double Fraction, string Label);

/// Analog-style S-meter/Power-Output gauge (CLAUDE.md's RadioArcMeterView
/// port) — a parabolic "bow" rather than a true circular arc, deliberately,
/// so nothing can land outside the control from a trig sign error. Needle
/// position is only proportional to the raw 0-255 CAT reading (see
/// MainViewModel.SMeterValue/PowerOutputValue) — same caveat as the Mac
/// app's own implementation.
public sealed class RadioArcMeterView : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RadioArcMeterView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(
        nameof(MaxValue), typeof(double), typeof(RadioArcMeterView),
        new FrameworkPropertyMetadata(255.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double MaxValue { get => (double)GetValue(MaxValueProperty); set => SetValue(MaxValueProperty, value); }

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(ArcMeterKind), typeof(RadioArcMeterView),
        new FrameworkPropertyMetadata(ArcMeterKind.SMeter, FrameworkPropertyMetadataOptions.AffectsRender));
    public ArcMeterKind Kind { get => (ArcMeterKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    private const double LeftMargin = 18;
    private const double RightMargin = 8;
    private const double TopY = 5;
    private const double BowBottomY = 22;

    // S1..S9 evenly spaced, then +20/+40/+60 compressed into the remaining
    // width — matches the reference screenshot's proportions closely
    // enough without an actual calibration curve to work from. The "which
    // meter is this" identity (S / P) is a dedicated left-margin label
    // (see OnRender below), not one of these ticks — folding it into the
    // tick row made it both too small to read and easy to mistake for a
    // value (confirmed 2026-09-04: "S is a tad small and the P is
    // unreadable").
    private static readonly ArcMeterTick[] SMeterTicks =
    {
        new(0.10, "1"),
        new(0.24, "3"),
        new(0.38, "5"),
        new(0.52, "7"),
        new(0.64, "9"),
        new(0.77, "+20"),
        new(0.88, "+40"),
        new(0.98, "+60"),
    };

    // Real hardware calibration (2026-09-04, this radio's 100W amp — least-
    // squares fit against 5 measured raw-PO readings from 10W to 100W):
    //   10W→74  25W→105  50W→139  75W→163  100W→190
    // fits raw ≈ PowerCalSlope*sqrt(watts) + PowerCalOffset (residuals
    // within ~2.6 raw units) — the expected shape for a real analog RF
    // wattmeter, whose needle deflection tracks rectified RF *voltage*
    // (∝ sqrt(power) into a fixed load), not power directly. The needle
    // itself still just moves linearly with the raw value (Value/MaxValue
    // in OnRender) — that's genuinely how the meter movement works too;
    // it's only these tick POSITIONS that needed to move to match where
    // each wattage actually falls on that linear raw scale. 150W is
    // extrapolated from the same curve, not independently measured — this
    // radio's amp tops out at 100W.
    private const double PowerCalSlope = 16.70;
    private const double PowerCalOffset = 21.0;

    private static double PowerFractionForWatts(double watts) =>
        Math.Clamp((PowerCalSlope * Math.Sqrt(Math.Max(0, watts)) + PowerCalOffset) / 255.0, 0, 1);

    private static readonly ArcMeterTick[] PowerTicks =
    {
        new(PowerFractionForWatts(0), "0"),
        new(PowerFractionForWatts(10), "10"),
        new(PowerFractionForWatts(25), "25"),
        new(PowerFractionForWatts(50), "50"),
        new(PowerFractionForWatts(75), "75"),
        new(PowerFractionForWatts(100), "100"),
        new(PowerFractionForWatts(150), "150"),
    };

    private static readonly Color WhiteArc = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color MeterBlue = Color.FromRgb(0x8C, 0xB8, 0xFA);
    private static readonly Color Amber = Color.FromRgb(0xF2, 0xA6, 0x26);

    private double YAt(double f)
    {
        var u = (f - 0.5) * 2;
        return TopY + (BowBottomY - TopY) * u * u;
    }

    private double XAt(double f, double width) => LeftMargin + f * Math.Max(0, width - LeftMargin - RightMargin);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= 0 || ActualHeight <= 0) return;

        var ticks = Kind == ArcMeterKind.SMeter ? SMeterTicks : PowerTicks;
        // S-meter's arc goes white -> blue past S9 (~0.64); Power Output
        // stays a single amber arc throughout.
        double? colorBreak = Kind == ArcMeterKind.SMeter ? 0.64 : null;
        var primaryColor = Kind == ArcMeterKind.SMeter ? WhiteArc : Amber;

        if (colorBreak is double breakF)
        {
            DrawArcSegment(dc, width, 0, breakF, primaryColor);
            DrawArcSegment(dc, width, breakF, 1, MeterBlue);
        }
        else
        {
            DrawArcSegment(dc, width, 0, 1, primaryColor);
        }

        var tickPen = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), 1);
        tickPen.Freeze();
        var tickTextBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        tickTextBrush.Freeze();

        foreach (var tick in ticks)
        {
            var x = XAt(tick.Fraction, width);
            var y = YAt(tick.Fraction);
            dc.DrawLine(tickPen, new Point(x, y - 3), new Point(x, y + 3));
            DrawText(dc, tick.Label, x, y + 6, 9, FontWeights.Bold, tickTextBrush, monospace: true);
        }

        // "Which meter is this" identity — a dedicated, larger left-margin
        // label (S / P) rather than a tick, so it stays legible regardless
        // of the tick row's font size. Vertically centered on the whole
        // control, not tied to the bow curve.
        var identityBrush = new SolidColorBrush(Colors.White);
        identityBrush.Freeze();
        var identityLabel = Kind == ArcMeterKind.SMeter ? "S" : "P";
        var identityTypeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var identityText = new FormattedText(identityLabel, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            identityTypeface, 13, identityBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(identityText, new Point(0, ActualHeight / 2 - identityText.Height / 2));

        var needleFraction = MaxValue > 0 ? Math.Clamp(Value / MaxValue, 0, 1) : 0;
        var needleX = XAt(needleFraction, width);
        var needleY = YAt(needleFraction);
        var needlePen = new Pen(Brushes.White, 2);
        needlePen.Freeze();
        dc.DrawLine(needlePen, new Point(needleX, ActualHeight), new Point(needleX, needleY));
        dc.DrawEllipse(Brushes.White, null, new Point(needleX, needleY), 2.5, 2.5);
    }

    private void DrawArcSegment(DrawingContext dc, double width, double f0, double f1, Color color)
    {
        const int steps = 24;
        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            ctx.BeginFigure(new Point(XAt(f0, width), YAt(f0)), false, false);
            for (var i = 1; i <= steps; i++)
            {
                var f = f0 + (f1 - f0) * i / steps;
                ctx.LineTo(new Point(XAt(f, width), YAt(f)), true, false);
            }
        }
        geom.Freeze();
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(153, color.R, color.G, color.B)), 1.5);
        pen.Freeze();
        dc.DrawGeometry(null, pen, geom);
    }

    private void DrawText(DrawingContext dc, string text, double centerX, double y, double fontSize, FontWeight weight, Brush brush, bool monospace)
    {
        var typeface = new Typeface(new FontFamily(monospace ? "Consolas" : "Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal);
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, fontSize, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(centerX - formatted.Width / 2, y));
    }
}

/// VOL/RF horizontal rail meter (CLAUDE.md's RadioVolMeterView port) — a
/// straight ruled-line rail with 19 purely decorative tick posts and one
/// amber downward-pointing triangle marking the current value. Read-only
/// display; the app's existing MAIN AF/RF/SQL stepper (VFO Controls) is
/// still how these values actually get adjusted.
public sealed class RadioVolMeterView : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RadioVolMeterView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(
        nameof(MaxValue), typeof(double), typeof(RadioVolMeterView),
        new FrameworkPropertyMetadata(255.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double MaxValue { get => (double)GetValue(MaxValueProperty); set => SetValue(MaxValueProperty, value); }

    public static readonly DependencyProperty RailColorProperty = DependencyProperty.Register(
        nameof(RailColor), typeof(Color), typeof(RadioVolMeterView),
        new FrameworkPropertyMetadata(Colors.Red, FrameworkPropertyMetadataOptions.AffectsRender));
    public Color RailColor { get => (Color)GetValue(RailColorProperty); set => SetValue(RailColorProperty, value); }

    /// What this rail is metering (e.g. "VOL", "RFG") — drawn at the left
    /// in place of the plain "−" sign, so each rail is self-identifying
    /// (confirmed 2026-09-04: a bare "−" didn't say what the rail was for).
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(RadioVolMeterView),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    private const double LeftMargin = 32;
    private const double RightMargin = 16;
    private const int TickPostCount = 19;
    private static readonly Color Amber = Color.FromRgb(0xF2, 0xA6, 0x26);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var railY = height * 0.45;
        var left = LeftMargin;
        var right = Math.Max(left, width - RightMargin);
        var usable = right - left;

        var railPen = new Pen(new SolidColorBrush(RailColor), 1.5);
        railPen.Freeze();
        dc.DrawLine(railPen, new Point(left, railY), new Point(right, railY));

        var tickBrush = new SolidColorBrush(Color.FromArgb(166, 255, 255, 255));
        tickBrush.Freeze();
        var tickPen = new Pen(tickBrush, 2);
        tickPen.Freeze();
        for (var i = 0; i < TickPostCount; i++)
        {
            var f = TickPostCount == 1 ? 0 : i / (double)(TickPostCount - 1);
            var x = left + f * usable;
            dc.DrawLine(tickPen, new Point(x, railY + 2), new Point(x, railY + 7));
        }

        var fraction = MaxValue > 0 ? Math.Clamp(Value / MaxValue, 0, 1) : 0;
        var mx = left + fraction * usable;
        var amberBrush = new SolidColorBrush(Amber);
        amberBrush.Freeze();
        var triangle = new StreamGeometry();
        using (var ctx = triangle.Open())
        {
            ctx.BeginFigure(new Point(mx - 5, railY - 4), true, true);
            ctx.LineTo(new Point(mx + 5, railY - 4), true, false);
            ctx.LineTo(new Point(mx, railY + 3), true, false);
        }
        triangle.Freeze();
        dc.DrawGeometry(amberBrush, null, triangle);

        var labelBrush = new SolidColorBrush(Color.FromArgb(191, 255, 255, 255));
        labelBrush.Freeze();
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var nameText = new FormattedText(Label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, labelBrush, dpi);
        dc.DrawText(nameText, new Point(0, railY - nameText.Height / 2));
        var plusText = new FormattedText("+", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, labelBrush, dpi);
        dc.DrawText(plusText, new Point(width - plusText.Width, railY - plusText.Height / 2));
    }
}
