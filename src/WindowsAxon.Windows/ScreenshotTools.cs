using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.AI;
using WindowsAxon.Agent.Tools;

namespace WindowsAxon.Windows;

public sealed class ScreenshotTools(DesktopUiTools ui)
{
    internal const int MaxSide = 1280;

    [Tool("ui_screenshot",
          "desktop_ui",
          "Capture an image of one window ID from ui_list_windows, scaled to at most 1280 pixels. Use it when ui_snapshot cannot show the content, such as a canvas or a custom-drawn app, or to check a visual result. Blocks security, credential, remote-desktop, password-manager, and WindowsAxon windows. Remote endpoints require approval.",
          policyDriven: true)]
    public Task<IEnumerable<AIContent>> Capture(string windowId)
    {
        return StaThread.Run<IEnumerable<AIContent>>(() =>
        {
            var root = ui.Root(windowId);
            var handle = new nint(root.Current.NativeWindowHandle);
            if (IsIconic(handle))
            {
                throw new InvalidOperationException("The window is minimized. Use ui_focus_window first.");
            }

            if (!GetWindowRect(handle, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            {
                throw new InvalidOperationException("The window has no visible area.");
            }

            var image = Encode(CaptureWindow(handle, rect.Right - rect.Left, rect.Bottom - rect.Top));
            return
            [
                new TextContent($"Screenshot of \"{root.Current.Name}\", {image.Width}x{image.Height} pixels, attached as an image."),
                new DataContent(image.Png, "image/png"),
            ];
        });
    }

    private static BitmapSource CaptureWindow(nint handle, int width, int height)
    {
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var previous = SelectObject(memory, bitmap);
        try
        {
            // PW_RENDERFULLCONTENT captures DirectComposition content and windows covered by other windows.
            var captured = PrintWindow(handle, memory, 2);
            SelectObject(memory, previous);
            if (!captured)
            {
                throw new InvalidOperationException("Windows could not capture this window.");
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    internal static (byte[] Png, int Width, int Height) Encode(BitmapSource source)
    {
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource scaled = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(scaled));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return (stream.ToArray(), scaled.PixelWidth, scaled.PixelHeight);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out WindowRect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint window, nint dc, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint item);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint item);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);
}
