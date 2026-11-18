using System.Runtime.InteropServices;
using SleazeDetector.Native;

namespace SleazeDetector.Capture;

/// <summary>A BGRA pixel buffer plus where it sits on screen (so OCR rects map back to screen coords).</summary>
public sealed class Frame
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }
    public int ScreenX { get; }
    public int ScreenY { get; }

    public Frame(int w, int h, byte[] bgra, int screenX = 0, int screenY = 0)
    {
        Width = w; Height = h; Bgra = bgra; ScreenX = screenX; ScreenY = screenY;
    }

    // Screen captures run many times a second at sizes of several MB. Their buffers are reused: a frame from a
    // capture is handed back with Release() once read, and the next capture of the same size takes its buffer.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, System.Collections.Concurrent.ConcurrentBag<byte[]>> Pool = new();
    private const int PoolPerSize = 4;
    private bool _pooled, _kept;

    /// <summary>A buffer of exactly <paramref name="length"/> bytes, reused if one is free. Its contents are undefined.</summary>
    internal static byte[] Rent(int length) =>
        Pool.TryGetValue(length, out var bag) && bag.TryTake(out var buf) ? buf : new byte[length];

    /// <summary>A capture frame on a reusable buffer.</summary>
    internal static Frame Pooled(int w, int h, byte[] buf, int x, int y) => new(w, h, buf, x, y) { _pooled = true };

    /// <summary>The frame is kept beyond its tick (a recording): its buffer is never reused.</summary>
    public void Keep() => _kept = true;

    /// <summary>Hands the buffer back for reuse. The frame must not be used afterwards.</summary>
    public void Release()
    {
        if (!_pooled || _kept) return;
        _pooled = false;
        var bag = Pool.GetOrAdd(Bgra.Length, _ => new());
        if (bag.Count < PoolPerSize) bag.Add(Bgra);
    }

    public (byte r, byte g, byte b) Pixel(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return (Bgra[i + 2], Bgra[i + 1], Bgra[i]);
    }

    public Frame Crop(int x, int y, int w, int h)
    {
        x = Math.Clamp(x, 0, Width - 1); y = Math.Clamp(y, 0, Height - 1);
        w = Math.Clamp(w, 1, Width - x); h = Math.Clamp(h, 1, Height - y);
        var dst = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(Bgra, ((y + row) * Width + x) * 4, dst, row * w * 4, w * 4);
        return new Frame(w, h, dst, ScreenX + x, ScreenY + y);
    }

    /// <summary>
    /// Grabs a rectangle of the screen. Desktop Duplication first (sees fullscreen video and flip-model games), GDI
    /// BitBlt as the fallback.
    /// </summary>
    public static Frame CaptureScreen(int x, int y, int w, int h) =>
        DesktopDuplication.Shared.Capture(x, y, w, h) ?? CaptureGdi(x, y, w, h);

    /// <summary>GDI capture of the composed desktop (can miss hardware-plane / independent-flip content).</summary>
    public static Frame CaptureGdi(int x, int y, int w, int h)
    {
        IntPtr screen = Win32.GetDC(IntPtr.Zero);
        IntPtr mem = Win32.CreateCompatibleDC(screen);
        var bi = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
            biWidth = w, biHeight = -h, // top-down
            biPlanes = 1, biBitCount = 32,
        };
        IntPtr dib = Win32.CreateDIBSection(screen, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = Win32.SelectObject(mem, dib);
        try
        {
            Win32.BitBlt(mem, 0, 0, w, h, screen, x, y, Win32.SRCCOPY);
            var buf = Rent(w * h * 4);
            Marshal.Copy(bits, buf, 0, buf.Length);
            return Pooled(w, h, buf, x, y);
        }
        finally
        {
            Win32.SelectObject(mem, old);
            Win32.DeleteObject(dib);
            Win32.DeleteDC(mem);
            Win32.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static Frame FromFile(string path)
    {
        using var bmp = new System.Drawing.Bitmap(path);
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        using var conv = bmp.Clone(rect, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = conv.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var buf = new byte[bmp.Width * bmp.Height * 4];
        for (int row = 0; row < bmp.Height; row++)
            Marshal.Copy(data.Scan0 + row * data.Stride, buf, row * bmp.Width * 4, bmp.Width * 4);
        conv.UnlockBits(data);
        return new Frame(bmp.Width, bmp.Height, buf);
    }

    public void Save(string path)
    {
        using var bmp = new System.Drawing.Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, Width, Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        for (int row = 0; row < Height; row++)
            Marshal.Copy(Bgra, row * Width * 4, data.Scan0 + row * data.Stride, Width * 4);
        bmp.UnlockBits(data);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
