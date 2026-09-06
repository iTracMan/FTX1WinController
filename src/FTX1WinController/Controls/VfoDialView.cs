using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FTX1WinController.Controls;

/// The VFO tuning knob (CLAUDE.md's VFODial.swift port) — a custom-drawn
/// rotating dial: static outer bezel, two fixed glow arc strips (blue,
/// red when the clarifier is active), and a rotating "dome" with radiating
/// grain lines, a knurled tick ring, and an off-center boss. Purely a
/// relative encoder like the real hardware knob — there's no absolute
/// dial position tied to frequency, only a Stepped event fired once per
/// detent (100 detents/revolution, matching the Mac app's own knob) that
/// the caller wires to the existing VfoStepUp/DownCommand.
public sealed class VfoDialView : FrameworkElement
{
    public static readonly DependencyProperty IsClarifierActiveProperty = DependencyProperty.Register(
        nameof(IsClarifierActive), typeof(bool), typeof(VfoDialView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsClarifierActive { get => (bool)GetValue(IsClarifierActiveProperty); set => SetValue(IsClarifierActiveProperty, value); }

    /// Fires +1 (clockwise) or -1 (counter-clockwise) once per detent —
    /// the visual rotation itself updates continuously with every drag
    /// pixel/scroll tick regardless of when a detent actually fires, same
    /// as the Mac app's own knob.
    public event EventHandler<int>? Stepped;

    // 60 rather than the Mac app's 100 — that figure was tuned for a
    // trackpad's continuous, high-resolution scrollingDeltaY; on a mouse
    // drag around a ~140px on-screen dial, 100 detents/revolution proved
    // too sensitive to control precisely (confirmed on hardware
    // 2026-09-04 — "very hit and miss").
    private const int DetentsPerRevolution = 60;
    private const double DetentRadians = 2 * Math.PI / DetentsPerRevolution;

    private double _rotation;
    private bool _dragging;
    private Point _lastPoint;
    private double _accumulatedDrag;

    public VfoDialView()
    {
        ClipToBounds = false;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragging = true;
        _lastPoint = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        var pos = e.GetPosition(this);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var prevAngle = Math.Atan2(_lastPoint.Y - center.Y, _lastPoint.X - center.X);
        var curAngle = Math.Atan2(pos.Y - center.Y, pos.X - center.X);
        var delta = curAngle - prevAngle;
        while (delta > Math.PI) delta -= 2 * Math.PI;
        while (delta < -Math.PI) delta += 2 * Math.PI;

        _rotation += delta;
        _accumulatedDrag += delta;
        _lastPoint = pos;

        while (_accumulatedDrag >= DetentRadians)
        {
            _accumulatedDrag -= DetentRadians;
            Stepped?.Invoke(this, 1);
        }
        while (_accumulatedDrag <= -DetentRadians)
        {
            _accumulatedDrag += DetentRadians;
            Stepped?.Invoke(this, -1);
        }

        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // e.Delta is ±120 per notch on a standard wheel — Windows has no
        // real equivalent to the Mac's continuous trackpad scrollingDeltaY,
        // so this adapts to "one notch = one step" rather than porting the
        // Mac's literal delta-accumulation threshold.
        var notches = e.Delta / 120;
        if (notches != 0)
        {
            _rotation += notches * DetentRadians * 3;
            for (var i = 0; i < Math.Abs(notches); i++)
            {
                Stepped?.Invoke(this, Math.Sign(notches));
            }
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var center = new Point(width / 2, height / 2);
        var outerRadius = Math.Min(width, height) / 2 - 2;

        // Static outer bezel — light-to-dark grey radial gradient, angled
        // via an off-center origin, doesn't rotate.
        var bezelBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.35, 0.28),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.75,
            RadiusY = 0.75,
        };
        bezelBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0xB4, 0xB4, 0xB8), 0.0));
        bezelBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x3A, 0x3A, 0x3E), 1.0));
        bezelBrush.Freeze();
        var bezelPen = new Pen(Brushes.Black, 1);
        bezelPen.Freeze();
        dc.DrawEllipse(bezelBrush, bezelPen, center, outerRadius, outerRadius);

        // Two fixed glow arc strips either side of the bezel — blue
        // normally, red when the clarifier is active.
        var arcColor = IsClarifierActive ? Color.FromRgb(0xFA, 0x4C, 0x47) : Color.FromRgb(0x0A, 0x84, 0xFF);
        DrawGlowArc(dc, center, outerRadius - 3, 180, 48, arcColor);
        DrawGlowArc(dc, center, outerRadius - 3, 0, 48, arcColor);

        // Rotating dome.
        var domeRadius = outerRadius - 9;
        dc.PushClip(new EllipseGeometry(center, domeRadius, domeRadius));
        dc.PushTransform(new RotateTransform(_rotation * 180 / Math.PI, center.X, center.Y));

        var domeBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.3, 0.22),
            Center = new Point(0.5, 0.5),
            RadiusX = 1.3,
            RadiusY = 1.3,
        };
        domeBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x9E, 0x9E, 0xA2), 0.0));
        domeBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x55, 0x55, 0x58), 0.5));
        domeBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x15, 0x15, 0x17), 1.0));
        domeBrush.Freeze();
        dc.DrawEllipse(domeBrush, null, center, domeRadius, domeRadius);

        // 72 radiating "grain" lines, alternating shade, 12%-105% of dome radius.
        var grainBrushA = new SolidColorBrush(Color.FromArgb(56, 158, 158, 158));
        grainBrushA.Freeze();
        var grainBrushB = new SolidColorBrush(Color.FromArgb(56, 77, 77, 77));
        grainBrushB.Freeze();
        var grainPenA = new Pen(grainBrushA, 0.5);
        grainPenA.Freeze();
        var grainPenB = new Pen(grainBrushB, 0.5);
        grainPenB.Freeze();
        const int grainCount = 72;
        for (var i = 0; i < grainCount; i++)
        {
            var angle = i * (2 * Math.PI / grainCount);
            var innerR = domeRadius * 0.12;
            var outerR = domeRadius * 1.05;
            var p1 = new Point(center.X + innerR * Math.Cos(angle), center.Y + innerR * Math.Sin(angle));
            var p2 = new Point(center.X + outerR * Math.Cos(angle), center.Y + outerR * Math.Sin(angle));
            dc.DrawLine(i % 2 == 0 ? grainPenA : grainPenB, p1, p2);
        }

        // 110-tick knurled ring just inside the dome edge.
        var tickBrushA = new SolidColorBrush(Color.FromRgb(0x73, 0x73, 0x73));
        tickBrushA.Freeze();
        var tickBrushB = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26));
        tickBrushB.Freeze();
        var tickPenA = new Pen(tickBrushA, 0.6);
        tickPenA.Freeze();
        var tickPenB = new Pen(tickBrushB, 0.6);
        tickPenB.Freeze();
        const int tickCount = 110;
        for (var i = 0; i < tickCount; i++)
        {
            var angle = i * (2 * Math.PI / tickCount);
            var r1 = domeRadius - 1.5;
            var r2 = domeRadius - 5.5;
            var p1 = new Point(center.X + r1 * Math.Cos(angle), center.Y + r1 * Math.Sin(angle));
            var p2 = new Point(center.X + r2 * Math.Cos(angle), center.Y + r2 * Math.Sin(angle));
            dc.DrawLine(i % 2 == 0 ? tickPenA : tickPenB, p1, p2);
        }

        // Boss — a smaller circle offset toward the bottom of the dome
        // (fixed within the rotating frame, so it visually spins with it).
        var bossRadius = domeRadius * 0.22;
        var bossOffset = domeRadius * 0.42;
        var bossCenter = new Point(center.X, center.Y + bossOffset);
        var bossBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.35, 0.3),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.9,
            RadiusY = 0.9,
        };
        bossBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0xB0, 0xB0, 0xB0), 0.0));
        bossBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x15, 0x15, 0x17), 1.0));
        bossBrush.Freeze();
        var bossPen = new Pen(Brushes.Black, 0.5);
        bossPen.Freeze();
        dc.DrawEllipse(bossBrush, bossPen, bossCenter, bossRadius, bossRadius);

        dc.Pop();
        dc.Pop();
    }

    /// Draws one of the two fixed glow arc strips — a soft halo
    /// approximated by stacking progressively wider, more transparent
    /// passes of the same stroke underneath the crisp top line (DrawingContext
    /// draws can't carry a UIElement-style DropShadowEffect directly).
    private static void DrawGlowArc(DrawingContext dc, Point center, double radius, double centerDeg, double halfSpanDeg, Color color)
    {
        var startRad = (centerDeg - halfSpanDeg) * Math.PI / 180;
        var endRad = (centerDeg + halfSpanDeg) * Math.PI / 180;
        var p1 = new Point(center.X + radius * Math.Cos(startRad), center.Y + radius * Math.Sin(startRad));
        var p2 = new Point(center.X + radius * Math.Cos(endRad), center.Y + radius * Math.Sin(endRad));
        var isLargeArc = halfSpanDeg * 2 > 180;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(p1, false, false);
            ctx.ArcTo(p2, new Size(radius, radius), 0, isLargeArc, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();

        for (var pass = 3; pass >= 1; pass--)
        {
            var glowBrush = new SolidColorBrush(Color.FromArgb((byte)(60 / pass), color.R, color.G, color.B));
            glowBrush.Freeze();
            var glowPen = new Pen(glowBrush, 3.5 + pass * 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            glowPen.Freeze();
            dc.DrawGeometry(null, glowPen, geometry);
        }

        var coreBrush = new SolidColorBrush(color);
        coreBrush.Freeze();
        var corePen = new Pen(coreBrush, 3.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        corePen.Freeze();
        dc.DrawGeometry(null, corePen, geometry);
    }
}
