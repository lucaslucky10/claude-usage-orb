using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ClaudeUsageOrb;

internal sealed record PopupRow(string Label, Color Color, double Remaining, string ValueText, string SubText, double? Marker, string? Note, bool NoteIsWarning);

internal sealed record PopupModel(string Title, string? Plan, string Updated, IReadOnlyList<PopupRow> Rows, string? Extra, string? Status, bool StatusIsError);

/// <summary>Hover card with the full breakdown. Never takes focus.</summary>
internal sealed class DetailPopup : Window
{
    private const double CardWidth = 284;
    private const double CardMargin = 12;
    private const double Inner = CardWidth - 28 - 2;

    private static readonly Brush Primary = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF4)));
    private static readonly Brush Secondary = Frozen(new SolidColorBrush(Color.FromRgb(0xA3, 0xA3, 0xAB)));
    private static readonly Brush Amber = Frozen(new SolidColorBrush(Color.FromRgb(0xF5, 0xB7, 0x3B)));
    private static readonly Brush Red = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6F)));

    private readonly StackPanel stack = new();

    public DetailPopup()
    {
        Title = "Claude Usage Orb details";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000;
        Top = -10000;
        var card = new Border
        {
            Width = CardWidth,
            Margin = new Thickness(CardMargin),
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x1E, 0x1E, 0x22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.45, Color = Colors.Black },
            Child = stack,
        };
        // Set text defaults on the card itself so it also renders correctly outside this window (preview mode).
        TextElement.SetFontFamily(card, new FontFamily("Segoe UI Variable Text, Segoe UI"));
        TextElement.SetFontSize(card, 12.5);
        TextElement.SetForeground(card, Primary);
        TextOptions.SetTextFormattingMode(card, TextFormattingMode.Display);
        Content = card;

        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            Native.SetExStyleFlag(Handle, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, true);
        };
    }

    public IntPtr Handle { get; private set; }

    public void Render(PopupModel model)
    {
        stack.Children.Clear();

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
        var updated = new TextBlock { Text = model.Updated, Foreground = Secondary, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(updated, Dock.Right);
        header.Children.Add(updated);
        var title = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center };
        title.Inlines.Add(model.Title);
        if (!string.IsNullOrEmpty(model.Plan))
        {
            title.Inlines.Add(new System.Windows.Documents.Run("  " + model.Plan) { Foreground = Secondary, FontWeight = FontWeights.Normal, FontSize = 11.5 });
        }
        header.Children.Add(title);
        stack.Children.Add(header);

        foreach (PopupRow row in model.Rows)
        {
            stack.Children.Add(BuildRow(row));
        }

        if (!string.IsNullOrEmpty(model.Extra))
        {
            stack.Children.Add(new TextBlock { Text = model.Extra, Foreground = Secondary, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 4) });
        }

        if (!string.IsNullOrEmpty(model.Status))
        {
            stack.Children.Add(new TextBlock
            {
                Text = model.Status,
                Foreground = model.StatusIsError ? Red : Amber,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 2),
            });
        }

        stack.Children.Add(new TextBlock
        {
            Text = Loc.Hint,
            Foreground = Secondary,
            FontSize = 10.5,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
    }

    private static UIElement BuildRow(PopupRow row)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };

        var line = new DockPanel { LastChildFill = true };
        var value = new TextBlock
        {
            Text = row.ValueText,
            FontWeight = FontWeights.SemiBold,
            Foreground = row.Remaining < 10 ? Red : row.Remaining < 25 ? Amber : Primary,
        };
        DockPanel.SetDock(value, Dock.Right);
        line.Children.Add(value);
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(row.Color), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(new TextBlock { Text = row.Label, TextTrimming = TextTrimming.CharacterEllipsis });
        line.Children.Add(label);
        panel.Children.Add(line);

        var bar = new Canvas { Width = Inner, Height = 6, Margin = new Thickness(0, 6, 0, 5), ClipToBounds = false };
        bar.Children.Add(new Border { Width = Inner, Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x33, row.Color.R, row.Color.G, row.Color.B)) });
        double fill = Math.Clamp(row.Remaining / 100, 0, 1) * Inner;
        if (fill > 0.5)
        {
            bar.Children.Add(new Border { Width = Math.Max(6, fill), Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(row.Color) });
        }
        if (row.Marker is { } marker && marker is > 0.003 and < 0.997)
        {
            var tick = new Border { Width = 2, Height = 10, CornerRadius = new CornerRadius(1), Background = Brushes.White, Opacity = 0.9 };
            Canvas.SetLeft(tick, marker * Inner - 1);
            Canvas.SetTop(tick, -2);
            bar.Children.Add(tick);
        }
        panel.Children.Add(bar);

        panel.Children.Add(new TextBlock { Text = row.SubText, Foreground = Secondary, FontSize = 11.5 });
        if (!string.IsNullOrEmpty(row.Note))
        {
            panel.Children.Add(new TextBlock
            {
                Text = row.Note,
                Foreground = row.NoteIsWarning ? Amber : Secondary,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }
        return panel;
    }

    /// <summary>Places the card above (or below) the orb, inside the orb's monitor work area.</summary>
    public void PlaceNear(IntPtr orb, double orbShadowMargin)
    {
        if (Handle == IntPtr.Zero || orb == IntPtr.Zero)
        {
            return;
        }
        UpdateLayout();
        Native.GetWindowRect(orb, out var o);
        Native.GetWindowRect(Handle, out var p);
        var info = Native.GetMonitorInfo(Native.MonitorFromWindow(orb, Native.MONITOR_DEFAULTTONEAREST));
        var work = info.rcWork;
        var mon = info.rcMonitor;
        double scale = Native.ScaleFor(orb);

        int orbM = (int)Math.Round(orbShadowMargin * scale);
        int cardM = (int)Math.Round(CardMargin * scale);
        int gap = (int)Math.Round(6 * scale);
        int w = p.Width, h = p.Height;
        int ocx = (o.Left + o.Right) / 2;

        int x = Math.Clamp(ocx - w / 2, work.Left - cardM, work.Right - w + cardM);
        int above = o.Top + orbM - gap - h + cardM;
        int below = o.Bottom - orbM + gap - cardM;
        bool preferAbove = (o.Top + o.Bottom) / 2 > (mon.Top + mon.Bottom) / 2;
        int y = preferAbove
            ? (above >= work.Top - cardM ? above : below)
            : (below + h <= work.Bottom + cardM ? below : above);

        // Not enough room above or below (e.g. tiny screens): put it beside the orb.
        if (y < work.Top - cardM || y + h > work.Bottom + cardM)
        {
            y = Math.Clamp((o.Top + o.Bottom) / 2 - h / 2, work.Top - cardM, work.Bottom - h + cardM);
            bool roomLeft = o.Left + orbM - gap - w + cardM >= work.Left - cardM;
            x = roomLeft ? o.Left + orbM - gap - w + cardM : o.Right - orbM + gap - cardM;
        }

        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
