using System.Runtime.InteropServices.WindowsRuntime;
using SleazeDetector.Capture;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace SleazeDetector.Ocr;

public enum TextColor { White, Red, Green, Yellow, Blue, Other }

/// <param name="Conf">Read confidence 0..1 (1 for Windows OCR words; the glyph matcher's score otherwise).</param>
public sealed record OcrWord(string Text, int X, int Y, int W, int H, TextColor Color, double Conf = 1)
{
    public int Right => X + W;
    public int CenterY => Y + H / 2;
}

public sealed record OcrLine(IReadOnlyList<OcrWord> Words)
{
    public string Text => string.Join(' ', Words.Select(w => w.Text));
    public int X => Words.Min(w => w.X);
    public int Y => Words.Min(w => w.Y);
    public int Right => Words.Max(w => w.Right);
    public int Bottom => Words.Max(w => w.Y + w.H);
}

public enum Preprocess
{
    /// <summary>Upscaled original pixels.</summary>
    Raw,
    /// <summary>Bright pixels become ink: white/coloured HUD text over a dark scene.</summary>
    BrightText,
    /// <summary>Saturated pixels become ink: red/green names over a light or grey scene (snow, white walls).</summary>
    ColorText,
    /// <summary>Only clearly red/pink pixels become ink: hostile names (prox chat) over white walls, snow or sky.</summary>
    RedText,
}

/// <summary>
/// Thin wrapper over Windows' built-in OCR engine (Windows.Media.Ocr). It ships with every
/// Windows 10/11 install, so the release exe has no OCR runtime to bundle.
/// Returned coordinates are absolute screen pixels.
/// </summary>
public sealed class HudOcr
{
    private readonly OcrEngine _engine;
    private const int Pad = 24;

    public HudOcr()
    {
        _engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
                  ?? OcrEngine.TryCreateFromUserProfileLanguages()
                  ?? throw new InvalidOperationException(
                      "Windows OCR is unavailable. Install an English language pack in Settings > Time & Language.");
    }

    public async Task<List<OcrLine>> ReadAsync(Frame frame, int scale = 2, Preprocess mode = Preprocess.BrightText)
    {
        int maxDim = (int)OcrEngine.MaxImageDimension;
        while (scale > 1 && (frame.Width * scale + 2 * Pad > maxDim || frame.Height * scale + 2 * Pad > maxDim)) scale--;

        var prepared = Prepare(frame, scale, mode, Pad);
        if (Environment.GetEnvironmentVariable("SLEAZE_OCR_DUMP") is { Length: > 0 } dumpDir)
            new Frame(frame.Width * scale + 2 * Pad, frame.Height * scale + 2 * Pad, prepared)
                .Save(Path.Combine(dumpDir, $"ocr_{mode}_{DateTime.Now:HHmmssfff}.png"));
        using var sb = SoftwareBitmap.CreateCopyFromBuffer(prepared.AsBuffer(), BitmapPixelFormat.Bgra8,
            frame.Width * scale + 2 * Pad, frame.Height * scale + 2 * Pad, BitmapAlphaMode.Premultiplied);
        var result = await _engine.RecognizeAsync(sb);

        var lines = new List<OcrLine>();
        foreach (var line in result.Lines)
        {
            var words = new List<OcrWord>();
            foreach (var w in line.Words)
            {
                var r = w.BoundingRect;
                int lx = (int)((r.X - Pad) / scale), ly = (int)((r.Y - Pad) / scale);
                int lw = Math.Max(1, (int)Math.Ceiling(r.Width / scale)), lh = Math.Max(1, (int)Math.Ceiling(r.Height / scale));
                var color = DominantColor(frame, lx, ly, lw, lh);
                words.Add(new OcrWord(w.Text, frame.ScreenX + lx, frame.ScreenY + ly, lw, lh, color));
            }
            if (words.Count > 0) lines.Add(new OcrLine(words));
        }
        return lines;
    }

    /// <summary>
    /// Upscales (Windows OCR wants ~20px+ glyphs) and optionally converts to dark-text-on-white.
    /// HUD text in Marathon is bright and saturated against a busy 3D scene, so keeping only
    /// bright pixels removes most of the background noise.
    /// </summary>
    private static byte[] Prepare(Frame f, int scale, Preprocess mode, int pad)
    {
        int w = f.Width * scale, h = f.Height * scale;
        int ow = w + 2 * pad, oh = h + 2 * pad;
        var dst = new byte[ow * oh * 4];
        // Margin colour: white for dark-on-white mode, mid-grey for raw.
        byte fill = mode == Preprocess.Raw ? (byte)40 : (byte)255;
        for (int i = 0; i < dst.Length; i += 4) { dst[i] = dst[i + 1] = dst[i + 2] = fill; dst[i + 3] = 255; }
        var src = f.Bgra;

        // Adaptive threshold: text is the brightest thing in the crop; pick a cut at the 88th percentile, floor 150.
        int threshold = 0;
        if (mode == Preprocess.BrightText)
        {
            var hist = new int[256];
            for (int i = 0; i < src.Length; i += 4) hist[Math.Max(src[i], Math.Max(src[i + 1], src[i + 2]))]++;
            int target = (int)(f.Width * f.Height * 0.88), acc = 0;
            for (int v = 0; v < 256; v++) { acc += hist[v]; if (acc >= target) { threshold = v; break; } }
            threshold = Math.Clamp(threshold, 150, 215);
        }

        // Per-source-pixel "ink" score (>0 = text), then bilinear upscale so glyph edges stay smooth.
        var score = new float[f.Width * f.Height];
        for (int i = 0, p = 0; i < src.Length; i += 4, p++)
        {
            int b = src[i], g = src[i + 1], r = src[i + 2];
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            score[p] = mode switch
            {
                Preprocess.BrightText => max - threshold + 0.5f,
                Preprocess.ColorText => Math.Min(max - 110, (max - min) - 0.38f * max),
                Preprocess.RedText => Math.Min(r - 120, r - Math.Max(g, b) - 55),
                _ => 0,
            };
        }

        for (int y = 0; y < h; y++)
        {
            float fy = Math.Clamp((y + 0.5f) / scale - 0.5f, 0, f.Height - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, f.Height - 1);
            float ty = fy - y0;
            for (int x = 0; x < w; x++)
            {
                float fx = Math.Clamp((x + 0.5f) / scale - 0.5f, 0, f.Width - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, f.Width - 1);
                float tx = fx - x0;
                int di = ((y + pad) * ow + x + pad) * 4;
                if (mode == Preprocess.Raw)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        float top = src[(y0 * f.Width + x0) * 4 + c] * (1 - tx) + src[(y0 * f.Width + x1) * 4 + c] * tx;
                        float bot = src[(y1 * f.Width + x0) * 4 + c] * (1 - tx) + src[(y1 * f.Width + x1) * 4 + c] * tx;
                        dst[di + c] = (byte)(top * (1 - ty) + bot * ty);
                    }
                }
                else
                {
                    float top = score[y0 * f.Width + x0] * (1 - tx) + score[y0 * f.Width + x1] * tx;
                    float bot = score[y1 * f.Width + x0] * (1 - tx) + score[y1 * f.Width + x1] * tx;
                    byte o = top * (1 - ty) + bot * ty > 0 ? (byte)0 : (byte)255;
                    dst[di] = dst[di + 1] = dst[di + 2] = o;
                }
                dst[di + 3] = 255;
            }
        }
        return dst;
    }

    /// <summary>Classifies the colour of the glyph pixels inside a word box (kill feed: red = enemy, green = teammate).</summary>
    public static TextColor DominantColor(Frame f, int x, int y, int w, int h)
    {
        // Glyph pixels are the most "ink-like" ones in the box. Rank by brightness + saturation and keep the top 35%,
        // so a red screen tint over white text (low health / death) doesn't turn the text red.
        var px = new List<(int r, int g, int b, int max, int min, double rank)>();
        for (int yy = Math.Max(0, y); yy < Math.Min(f.Height, y + h); yy++)
        for (int xx = Math.Max(0, x); xx < Math.Min(f.Width, x + w); xx++)
        {
            var (r, g, b) = f.Pixel(xx, yy);
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            if (max < 110) continue;
            px.Add((r, g, b, max, min, max + (max - min) * 0.5));
        }
        if (px.Count == 0) return TextColor.Other;
        var top = px.OrderByDescending(p => p.rank).Take(Math.Max(3, px.Count * 35 / 100)).ToList();

        double sumH = 0, sumHy = 0; int sat = 0;
        foreach (var (r, g, b, max, min, _) in top)
        {
            double s = (max - min) / (double)max;
            if (s < 0.45) continue;
            sat++;
            double hue = Hue(r, g, b, max, min) * Math.PI / 180;
            sumH += Math.Cos(hue); sumHy += Math.Sin(hue);
        }
        if (sat < top.Count * 0.5) return TextColor.White;
        double avg = Math.Atan2(sumHy, sumH) * 180 / Math.PI;
        if (avg < 0) avg += 360;
        return avg switch
        {
            < 25 or >= 320 => TextColor.Red,
            < 55 => TextColor.Yellow,
            < 170 => TextColor.Green,
            < 260 => TextColor.Blue,
            _ => TextColor.Other,
        };
    }

    private static double Hue(int r, int g, int b, int max, int min)
    {
        double d = max - min;
        if (d == 0) return 0;
        double hue = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        hue *= 60;
        return hue < 0 ? hue + 360 : hue;
    }
}
