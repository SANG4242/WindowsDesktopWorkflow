using System.Windows;
using System.Windows.Media.Imaging;

namespace DesktopWorkflow.App;

public static class WindowThumbnailService
{
    private const uint PwRenderFullContent = 2;

    public static BitmapSource? Capture(nint window, int maxWidth = 960, int maxHeight = 720)
    {
        if (window == nint.Zero || !NativeMethods.IsWindow(window) || !NativeMethods.IsWindowVisible(window))
        {
            return null;
        }

        if (NativeMethods.IsIconic(window))
        {
            return null;
        }

        if (!NativeMethods.GetWindowRect(window, out var rect))
        {
            return null;
        }

        var width = rect.Width;
        var height = rect.Height;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var hdcScreen = NativeMethods.GetDC(nint.Zero);
        if (hdcScreen == nint.Zero) return null;

        var hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
        if (hdcMem == nint.Zero)
        {
            _ = NativeMethods.ReleaseDC(nint.Zero, hdcScreen);
            return null;
        }

        var hBitmap = NativeMethods.CreateCompatibleBitmap(hdcScreen, width, height);
        if (hBitmap == nint.Zero)
        {
            _ = NativeMethods.DeleteDC(hdcMem);
            _ = NativeMethods.ReleaseDC(nint.Zero, hdcScreen);
            return null;
        }

        var hOld = NativeMethods.SelectObject(hdcMem, hBitmap);
        BitmapSource? result = null;

        try
        {
            var printed = NativeMethods.PrintWindow(window, hdcMem, PwRenderFullContent);
            if (!printed)
            {
                printed = NativeMethods.PrintWindow(window, hdcMem, 0);
            }

            if (printed)
            {
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    nint.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                if (maxWidth > 0 && maxHeight > 0 && (source.PixelWidth > maxWidth || source.PixelHeight > maxHeight))
                {
                    var scaleX = (double)maxWidth / source.PixelWidth;
                    var scaleY = (double)maxHeight / source.PixelHeight;
                    var scale = Math.Min(scaleX, scaleY);
                    var transformed = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(scale, scale));
                    transformed.Freeze();
                    result = transformed;
                }
                else
                {
                    source.Freeze();
                    result = source;
                }
            }
        }
        catch
        {
            result = null;
        }
        finally
        {
            _ = NativeMethods.SelectObject(hdcMem, hOld);
            _ = NativeMethods.DeleteObject(hBitmap);
            _ = NativeMethods.DeleteDC(hdcMem);
            _ = NativeMethods.ReleaseDC(nint.Zero, hdcScreen);
        }

        return result;
    }
}
