using System;
using SD = System.Drawing;
using SD2 = System.Drawing.Drawing2D;
using WF = System.Windows.Forms;

namespace ClaudeUsageOrb;

/// <summary>Notification-area icon: a mini orb with the remaining percentage.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly WF.NotifyIcon icon = new();
    private string lastKey = "";

    public TrayIcon()
    {
        icon.Text = Loc.AppName;
        icon.MouseUp += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left)
            {
                LeftClick?.Invoke();
            }
            else if (e.Button == WF.MouseButtons.Right)
            {
                RightClick?.Invoke();
            }
        };
        Update(null, SD.Color.Gray, 0, false, Loc.AppName);
        icon.Visible = true;
    }

    public event Action? LeftClick;
    public event Action? RightClick;

    public void Update(string? number, SD.Color ringColor, double fraction, bool dim, string tooltip)
    {
        icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        int size = IconSize();
        string key = $"{number}|{ringColor.ToArgb()}|{Math.Round(fraction, 2)}|{dim}|{size}";
        if (key == lastKey)
        {
            return;
        }
        lastKey = key;

        using var bitmap = Render(size, number, ringColor, fraction, dim);
        IntPtr handle = bitmap.GetHicon();
        SD.Icon owned;
        using (var borrowed = SD.Icon.FromHandle(handle))
        {
            // Clone copies the HICON, so the GDI handle from GetHicon can be released right away.
            owned = (SD.Icon)borrowed.Clone();
        }
        Native.DestroyIcon(handle);

        SD.Icon? previous = icon.Icon;
        icon.Icon = owned;
        previous?.Dispose();
    }

    private static int IconSize()
    {
        uint dpi = Native.GetDpiForSystem();
        int size = Native.GetSystemMetricsForDpi(Native.SM_CXSMICON, dpi);
        return size > 0 ? size : 16;
    }

    private static SD.Bitmap Render(int size, string? number, SD.Color ringColor, double fraction, bool dim)
    {
        var bmp = new SD.Bitmap(size, size, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(bmp);
        g.SmoothingMode = SD2.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = SD2.PixelOffsetMode.HighQuality;
        g.TextRenderingHint = SD.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(SD.Color.Transparent);

        float s = size;
        float stroke = Math.Max(1.6f, s * 0.13f);
        var disk = new SD.RectangleF(0.5f, 0.5f, s - 1f, s - 1f);
        using (var fill = new SD.SolidBrush(SD.Color.FromArgb(255, 0x1C, 0x1C, 0x20)))
        {
            g.FillEllipse(fill, disk);
        }

        var ring = new SD.RectangleF(stroke / 2 + 0.5f, stroke / 2 + 0.5f, s - stroke - 1f, s - stroke - 1f);
        int alpha = dim ? 140 : 255;
        using (var track = new SD.Pen(SD.Color.FromArgb(70, ringColor), stroke))
        {
            g.DrawEllipse(track, ring);
        }
        if (fraction > 0.004)
        {
            using var pen = new SD.Pen(SD.Color.FromArgb(alpha, ringColor), stroke) { StartCap = SD2.LineCap.Round, EndCap = SD2.LineCap.Round };
            if (fraction >= 0.995)
            {
                g.DrawEllipse(pen, ring);
            }
            else
            {
                g.DrawArc(pen, ring, -90f, (float)(fraction * 360));
            }
        }

        string text = string.IsNullOrEmpty(number) ? "…" : number;
        float fontPx = text.Length >= 3 ? s * 0.36f : s * 0.5f;
        using var font = new SD.Font("Segoe UI", fontPx, SD.FontStyle.Bold, SD.GraphicsUnit.Pixel);
        using var format = new SD.StringFormat(SD.StringFormat.GenericTypographic)
        {
            Alignment = SD.StringAlignment.Center,
            LineAlignment = SD.StringAlignment.Center,
        };
        using var brush = new SD.SolidBrush(SD.Color.FromArgb(dim ? 170 : 255, 255, 255, 255));
        g.DrawString(text, font, brush, new SD.RectangleF(0, s * 0.03f, s, s), format);
        return bmp;
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Icon?.Dispose();
        icon.Dispose();
    }
}
