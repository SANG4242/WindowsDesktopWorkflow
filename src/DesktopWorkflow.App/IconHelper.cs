using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopWorkflow.App;

public static class IconHelper
{
    private static readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? GetIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        return _cache.GetOrAdd(path, p =>
        {
            try
            {
                if (!File.Exists(p)) return null;
                using var icon = Icon.ExtractAssociatedIcon(p);
                if (icon is null) return null;

                var source = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            catch
            {
                return null;
            }
        });
    }
}
