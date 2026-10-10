using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClaudeUsageOrb;

/// <summary>
/// `ClaudeUsageOrb.exe --preview &lt;dir&gt;` renders the orb and the hover card from the cached usage
/// into PNG files and exits. Used for README screenshots and visual checks.
/// </summary>
internal static class Preview
{
    public static void Render(string dir)
    {
        Directory.CreateDirectory(dir);
        var settings = Settings.Load();
        Loc.Init(settings.Language);
        var poller = new UsagePoller(settings);
        poller.LoadCachedSnapshot();
        var (orbModel, popupModel, _) = Presenter.Build(settings, poller, DateTimeOffset.Now);

        foreach (int size in Settings.Sizes)
        {
            var visual = new OrbVisual { Diameter = size, ShadowMargin = OrbWindow.ShadowMargin };
            visual.SetModelImmediate(orbModel);
            double outer = size + OrbWindow.ShadowMargin * 2;
            Save(visual, outer, outer, Path.Combine(dir, $"orb-{size}.png"));
        }

        var popup = new DetailPopup();
        popup.Render(popupModel);
        var card = (FrameworkElement)popup.Content;
        popup.Content = null;
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Save(card, card.DesiredSize.Width, card.DesiredSize.Height, Path.Combine(dir, "popup.png"));
    }

    private static void Save(FrameworkElement element, double width, double height, string path)
    {
        const double scale = 2;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
