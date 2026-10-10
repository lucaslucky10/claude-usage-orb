using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClaudeUsageOrb;

internal enum OrbIndicator { None, Stale, Error }

internal sealed record RingModel(Color Color, double Fraction, double? Marker);

internal sealed record OrbModel(IReadOnlyList<RingModel> Rings, string CenterText, string CenterLabel, Color CenterColor, OrbIndicator Indicator);

/// <summary>Draws the orb: dark disk, one ring per limit (arc = share remaining), number in the middle.</summary>
internal sealed class OrbVisual : FrameworkElement
{
    private static readonly Typeface NumberFace = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
    private static readonly Brush DiskBrush = Frozen(new RadialGradientBrush(Color.FromRgb(0x2A, 0x2A, 0x2E), Color.FromRgb(0x17, 0x17, 0x1A)) { GradientOrigin = new Point(0.35, 0.3) });
    private static readonly Pen DiskPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Brush ShadowBrush = Frozen(new RadialGradientBrush(
        new GradientStopCollection { new(Color.FromArgb(0x70, 0, 0, 0), 0.78), new(Color.FromArgb(0, 0, 0, 0), 1.0) }));
    private static readonly Pen MarkerPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)), 1.2));

    private OrbModel model = new(Array.Empty<RingModel>(), "…", "", Colors.White, OrbIndicator.None);
    private double[] shown = Array.Empty<double>();
    private readonly DispatcherTimer animation;

    public OrbVisual()
    {
        animation = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        animation.Tick += (_, _) => Step();
        SnapsToDevicePixels = false;
    }

    public double Diameter { get; set; } = 72;
    public double ShadowMargin { get; set; } = 6;

    public void SetModel(OrbModel next)
    {
        bool sameShape = shown.Length == next.Rings.Count;
        var target = new double[next.Rings.Count];
        for (int i = 0; i < target.Length; i++)
        {
            target[i] = sameShape ? shown[i] : 0;
        }
        shown = target;
        model = next;
        animation.Start();
        InvalidateVisual();
    }

    /// <summary>Shows the model without animating (used for static previews).</summary>
    public void SetModelImmediate(OrbModel next)
    {
        model = next;
        shown = new double[next.Rings.Count];
        for (int i = 0; i < shown.Length; i++)
        {
            shown[i] = next.Rings[i].Fraction;
        }
        InvalidateVisual();
    }

    private void Step()
    {
        bool moving = false;
        for (int i = 0; i < shown.Length; i++)
        {
            double goal = model.Rings[i].Fraction;
            double diff = goal - shown[i];
            if (Math.Abs(diff) < 0.002)
            {
                shown[i] = goal;
            }
            else
            {
                shown[i] += diff * 0.16;
                moving = true;
            }
        }
        if (!moving)
        {
            animation.Stop();
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double s = Diameter;
        double radius = s / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);

        dc.DrawEllipse(ShadowBrush, null, new Point(c.X, c.Y + s * 0.025), radius + ShadowMargin * 0.95, radius + ShadowMargin * 0.95);
        dc.DrawEllipse(DiskBrush, DiskPen, c, radius - 0.5, radius - 0.5);

        int n = model.Rings.Count;
        double pad = Math.Max(2.5, s * 0.06);
        double thickness = Math.Max(2.4, s * (n >= 3 ? 0.064 : n == 2 ? 0.08 : 0.095));
        double gap = Math.Max(1.2, s * 0.024);
        double r = radius - pad - thickness / 2;
        double free = radius - pad;

        bool dim = model.Indicator != OrbIndicator.None;
        if (dim)
        {
            dc.PushOpacity(0.55);
        }
        for (int i = 0; i < n; i++)
        {
            RingModel ring = model.Rings[i];
            var track = new Pen(new SolidColorBrush(Color.FromArgb(0x38, ring.Color.R, ring.Color.G, ring.Color.B)), thickness);
            dc.DrawEllipse(null, track, c, r, r);

            var pen = new Pen(new SolidColorBrush(ring.Color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            DrawArc(dc, pen, c, r, Math.Clamp(shown[i], 0, 1));

            if (ring.Marker is { } marker && marker is > 0.002 and < 0.998)
            {
                double a = marker * 2 * Math.PI - Math.PI / 2;
                double inner = r - thickness / 2 - 0.6;
                double outer = r + thickness / 2 + 0.6;
                var pen2 = new Pen(MarkerPen.Brush, Math.Max(1.0, s * 0.02));
                dc.DrawLine(pen2, new Point(c.X + inner * Math.Cos(a), c.Y + inner * Math.Sin(a)), new Point(c.X + outer * Math.Cos(a), c.Y + outer * Math.Sin(a)));
            }

            free = r - thickness / 2 - gap * 0.6;
            r -= thickness + gap;
        }
        if (dim)
        {
            dc.Pop();
        }

        DrawCenter(dc, c, free, s);
        DrawIndicator(dc, c, radius, s);
    }

    private void DrawCenter(DrawingContext dc, Point c, double free, double s)
    {
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        bool withLabel = s >= 56 && !string.IsNullOrEmpty(model.CenterLabel);
        string text = model.CenterText;
        double size = free * (text.Length >= 3 ? 0.82 : 1.06);

        var number = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, NumberFace, size,
            new SolidColorBrush(model.CenterColor), ppd);
        Geometry glyphs = number.BuildGeometry(new Point(0, 0));
        Rect b = glyphs.Bounds;
        if (b.IsEmpty)
        {
            return;
        }
        double maxWidth = free * 1.62;
        double scale = b.Width > maxWidth ? maxWidth / b.Width : 1;
        double centerY = withLabel ? c.Y - free * 0.16 : c.Y;
        double x = c.X - (b.X + b.Width / 2) * scale;
        double y = centerY - (b.Y + b.Height / 2) * scale;

        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, x, y));
        dc.DrawGeometry(new SolidColorBrush(model.CenterColor), null, glyphs);
        dc.Pop();

        if (withLabel)
        {
            var label = new FormattedText(model.CenterLabel, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace,
                Math.Max(7.5, free * 0.36), new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), ppd);
            if (label.Width > free * 1.5)
            {
                label.MaxTextWidth = free * 1.5;
                label.Trimming = TextTrimming.CharacterEllipsis;
                label.MaxLineCount = 1;
            }
            double ly = c.Y + free * 0.5 - label.Height / 2;
            dc.DrawText(label, new Point(c.X - Math.Min(label.Width, free * 1.5) / 2, ly));
        }
    }

    private void DrawIndicator(DrawingContext dc, Point c, double radius, double s)
    {
        if (model.Indicator == OrbIndicator.None)
        {
            return;
        }
        Color color = model.Indicator == OrbIndicator.Error ? Color.FromRgb(0xFF, 0x5A, 0x5F) : Color.FromRgb(0xF5, 0xB7, 0x3B);
        double dot = Math.Max(2.6, s * 0.06);
        var at = new Point(c.X + radius * 0.71, c.Y - radius * 0.71);
        dc.DrawEllipse(new SolidColorBrush(color), new Pen(new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x1A)), Math.Max(1, s * 0.022)), at, dot, dot);
    }

    private static void DrawArc(DrawingContext dc, Pen pen, Point c, double r, double fraction)
    {
        if (fraction <= 0.001)
        {
            return;
        }
        if (fraction >= 0.999)
        {
            dc.DrawEllipse(null, pen, c, r, r);
            return;
        }
        double a0 = -Math.PI / 2;
        double a1 = fraction * 2 * Math.PI - Math.PI / 2;
        var start = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
        var end = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
