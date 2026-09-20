using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;
using MediaPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;

namespace DesktopWorkflow.App;

public sealed record LayoutZoneItem(
    NormalizedRectangle Zone,
    string Title = "",
    string Subtitle = "",
    bool IsHighlighted = false,
    ImageSource? Thumbnail = null);

public sealed class LayoutPreviewControl : FrameworkElement
{
    private IReadOnlyList<LayoutZoneItem> _items = [];

    public IReadOnlyList<NormalizedRectangle> Zones
    {
        get => _items.Select(item => item.Zone).ToList();
        set
        {
            var zones = value ?? [];
            _items = zones.Select((zone, index) => new LayoutZoneItem(zone, (index + 1).ToString(CultureInfo.InvariantCulture), string.Empty, index == 0)).ToList();
            InvalidateVisual();
        }
    }

    public IReadOnlyList<LayoutZoneItem> Items
    {
        get => _items;
        set
        {
            _items = value ?? [];
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var background = new SolidColorBrush(MediaColor.FromRgb(248, 250, 252));
        var backgroundPen = new MediaPen(new SolidColorBrush(MediaColor.FromRgb(226, 232, 240)), 1);

        var highlightFill = new SolidColorBrush(MediaColor.FromRgb(235, 243, 254));
        var normalFill = new SolidColorBrush(MediaColor.FromRgb(255, 255, 255));

        var highlightBorder = new MediaPen(new SolidColorBrush(MediaColor.FromRgb(186, 214, 251)), 1.75);
        var normalBorder = new MediaPen(new SolidColorBrush(MediaColor.FromRgb(226, 232, 240)), 1.25);

        drawingContext.DrawRoundedRectangle(background, backgroundPen,
            new Rect(0, 0, ActualWidth, ActualHeight), 10, 10);
        const double padding = 12;
        var width = Math.Max(0, ActualWidth - padding * 2);
        var height = Math.Max(0, ActualHeight - padding * 2);

        var typeface = new Typeface("Microsoft YaHei UI, Segoe UI");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (var index = 0; index < _items.Count; index++)
        {
            var item = _items[index];
            var zone = item.Zone;
            var rect = new Rect(
                padding + zone.X * width + 2,
                padding + zone.Y * height + 2,
                Math.Max(1, zone.Width * width - 4),
                Math.Max(1, zone.Height * height - 4));

            var border = item.IsHighlighted ? highlightBorder : normalBorder;

            if (item.Thumbnail is not null)
            {
                var clipGeometry = new RectangleGeometry(rect, 8, 8);
                drawingContext.PushClip(clipGeometry);

                drawingContext.DrawImage(item.Thumbnail, rect);

                var bannerHeight = Math.Min(rect.Height * 0.45, 52);
                var bannerRect = new Rect(rect.Left, rect.Bottom - bannerHeight, rect.Width, bannerHeight);
                var gradient = new LinearGradientBrush(
                    MediaColor.FromArgb(0, 15, 23, 42),
                    MediaColor.FromArgb(170, 15, 23, 42),
                    90.0);
                drawingContext.DrawRectangle(gradient, null, bannerRect);

                drawingContext.Pop();

                drawingContext.DrawRoundedRectangle(null, border, rect, 8, 8);

                var titleText = string.IsNullOrWhiteSpace(item.Title)
                    ? (index + 1).ToString(CultureInfo.InvariantCulture)
                    : item.Title;

                var titleFormatted = new FormattedText(
                    titleText,
                    CultureInfo.CurrentUICulture,
                    System.Windows.FlowDirection.LeftToRight,
                    typeface,
                    13.5,
                    new SolidColorBrush(MediaColor.FromRgb(255, 255, 255)),
                    dpi);

                var subtitleFormatted = new FormattedText(
                    item.Subtitle,
                    CultureInfo.CurrentUICulture,
                    System.Windows.FlowDirection.LeftToRight,
                    typeface,
                    11.0,
                    new SolidColorBrush(MediaColor.FromArgb(220, 241, 245, 249)),
                    dpi);

                var textX = rect.Left + 10;
                var textY = rect.Bottom - titleFormatted.Height - subtitleFormatted.Height - 8;
                if (textY < rect.Top + 4) textY = rect.Top + 4;

                drawingContext.DrawText(titleFormatted, new WpfPoint(textX, textY));
                drawingContext.DrawText(subtitleFormatted, new WpfPoint(textX, textY + titleFormatted.Height + 2));
            }
            else
            {
                var fill = item.IsHighlighted ? highlightFill : normalFill;
                drawingContext.DrawRoundedRectangle(fill, border, rect, 8, 8);

                var titleText = string.IsNullOrWhiteSpace(item.Title)
                    ? (index + 1).ToString(CultureInfo.InvariantCulture)
                    : item.Title;

                var titleFormatted = new FormattedText(
                    titleText,
                    CultureInfo.CurrentUICulture,
                    System.Windows.FlowDirection.LeftToRight,
                    typeface,
                    15,
                    new SolidColorBrush(MediaColor.FromRgb(15, 23, 42)),
                    dpi);

                if (string.IsNullOrWhiteSpace(item.Subtitle))
                {
                    drawingContext.DrawText(titleFormatted,
                        new WpfPoint(rect.Left + (rect.Width - titleFormatted.Width) / 2,
                                     rect.Top + (rect.Height - titleFormatted.Height) / 2));
                }
                else
                {
                    var subtitleFormatted = new FormattedText(
                        item.Subtitle,
                        CultureInfo.CurrentUICulture,
                        System.Windows.FlowDirection.LeftToRight,
                        typeface,
                        11.5,
                        new SolidColorBrush(MediaColor.FromRgb(100, 116, 139)),
                        dpi);

                    var totalTextHeight = titleFormatted.Height + 4 + subtitleFormatted.Height;
                    var startY = rect.Top + (rect.Height - totalTextHeight) / 2;

                    drawingContext.DrawText(titleFormatted,
                        new WpfPoint(rect.Left + (rect.Width - titleFormatted.Width) / 2, startY));
                    drawingContext.DrawText(subtitleFormatted,
                        new WpfPoint(rect.Left + (rect.Width - subtitleFormatted.Width) / 2, startY + titleFormatted.Height + 4));
                }
            }
        }
    }
}
