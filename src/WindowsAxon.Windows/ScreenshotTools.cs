using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using WindowsAxon.Agent.Tools;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace WindowsAxon.Windows;

public sealed class ScreenshotTools(DesktopUiTools ui)
{
    internal const int MaxSide = 1280;

    [Tool("ui_screenshot",
          "desktop_ui",
          "Capture an image of one window ID from ui_list_windows, scaled to at most 1280 pixels. Use it when ui_snapshot cannot show the content, such as a canvas or a custom-drawn app, or to check a visual result. Blocks security, credential, remote-desktop, password-manager, and WindowsAxon windows. Remote endpoints require approval.",
          policyDriven: true)]
    public async Task<IEnumerable<AIContent>> Capture(string windowId)
    {
        var (title, pixels, width, height) = await ApartmentThread.Run(ApartmentState.MTA, () =>
        {
            var root = ui.Root(windowId);
            var handle = root.NativeWindowHandle;
            if (IsIconic(handle))
            {
                throw new InvalidOperationException("The window is minimized. Use ui_focus_window first.");
            }

            if (!GetWindowRect(handle, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            {
                throw new InvalidOperationException("The window has no visible area.");
            }

            var size = (Width: rect.Right - rect.Left, Height: rect.Bottom - rect.Top);
            return (root.Name, CaptureWindow(handle, size.Width, size.Height), size.Width, size.Height);
        });

        var image = await Encode(pixels, width, height);
        return
        [
            new TextContent($"Screenshot of \"{title}\", {image.Width}x{image.Height} pixels, attached as an image."),
            new DataContent(image.Png, "image/png"),
        ];
    }

    private static byte[] CaptureWindow(nint handle, int width, int height)
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

            // A negative height requests top-down rows of 32-bit BGRA pixels.
            var header = new BitmapInfoHeader
            {
                Size = 40,
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
            };
            var pixels = new byte[width * height * 4];
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref header, 0) != height)
            {
                throw new InvalidOperationException("Windows could not read the captured pixels.");
            }

            return pixels;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    internal static async Task<(byte[] Png, int Width, int Height)> Encode(byte[] bgra, int width, int height)
    {
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(width, height));
        var scaledWidth = Math.Max(1, (int)Math.Round(width * scale));
        var scaledHeight = Math.Max(1, (int)Math.Round(height * scale));
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        // GDI leaves the alpha byte undefined, so the encoder must ignore it.
        encoder.SetPixelData(BitmapPixelFormat.Bgra8,
                             BitmapAlphaMode.Ignore,
                             (uint)width,
                             (uint)height,
                             96,
                             96,
                             bgra);
        if (scale < 1)
        {
            encoder.BitmapTransform.ScaledWidth = (uint)scaledWidth;
            encoder.BitmapTransform.ScaledHeight = (uint)scaledHeight;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        await encoder.FlushAsync();
        var png = new byte[stream.Size];
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(png);
        }

        return (png, scaledWidth, scaledHeight);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;

        // BITMAPINFO color table space; 32-bit BI_RGB does not use it, but GetDIBits may write up to three masks.
        public uint Color0;
        public uint Color1;
        public uint Color2;
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

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);
}
