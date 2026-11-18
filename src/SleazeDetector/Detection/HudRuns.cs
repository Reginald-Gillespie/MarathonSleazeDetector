using SleazeDetector.Capture;
using SleazeDetector.Ocr;

namespace SleazeDetector.Detection;

/// <summary>One coloured name on screen: its exact pixel box (screen coordinates) and colour.</summary>
public sealed record TextRun(int X0, int Y0, int X1, int Y1, TextColor Color)
{
    public int W => X1 - X0;
    public int H => Y1 - Y0;
    public int CenterY => (Y0 + Y1) / 2;
}

/// <summary>A HUD line (kill feed row / ping row / prox speaker): its coloured names and where the line ends.</summary>
public sealed record TextRow(List<TextRun> Runs, int Y0, int Y1, int Right, int WhiteBetween);

/// <summary>
/// Finds coloured HUD names straight from pixels, every frame, without OCR. Marathon draws runner names in two fixed
/// colours (enemy pink-red ~255,30,73 and squad lime ~190,230,10), which barely occur in the 3D scene at full
/// saturation. A colour mask, a row profile and a column profile give each name's exact box in a few milliseconds,
/// so meters can follow names frame by frame. Reading the text is done separately (and cached) by the glyph matcher.
/// </summary>
public static class HudRuns
{
    // Hue by ratios, plus a brightness floor: names are drawn at full brightness, so the floor separates them from
    // red / olive scenery of the same hue (a red wall is ~157,57,57) while still allowing some dimming (effects, a
    // player's pause scrim). Measured: enemy red 255,30,73 / 255,60,110; squad lime 224,227,16 over orange,
    // 199,234,76 over purple. Ability / action text is yellow (255,217,97): too much blue for lime, so it is never a name.
    public static bool IsRed(int r, int g, int b) => r >= 175 && r - g >= 100 && g * 100 <= r * 47 && b * 100 <= r * 72;
    public static bool IsLime(int r, int g, int b) => g >= 160 && r * 2 >= g && r * 100 <= g * 113 && b * 100 <= g * 34 && g - b >= 110;
    /// <summary>Yellow ability / action text ("SEARCH AND DESTROY"), and lime's look-alike.</summary>
    public static bool IsYellow(int r, int g, int b) => r >= 170 && g >= 140 && r >= g - 10 && b * 100 > g * 34 && b * 100 <= g * 70;

    /// <summary>Text ink in a box split into yellow (ability / action text) and white or lime (names). Screen coordinates.</summary>
    public static (int name, int yellow) NameVsYellow(Frame f, int x, int y, int w, int h)
    {
        int name = 0, yellow = 0;
        int x0 = Math.Max(0, x - f.ScreenX), y0 = Math.Max(0, y - f.ScreenY);
        int x1 = Math.Min(f.Width, x - f.ScreenX + w), y1 = Math.Min(f.Height, y - f.ScreenY + h);
        for (int yy = y0; yy < y1; yy++)
            for (int xx = x0; xx < x1; xx++)
            {
                int o = (yy * f.Width + xx) * 4;
                int b = f.Bgra[o], g = f.Bgra[o + 1], r = f.Bgra[o + 2];
                if (IsLime(r, g, b) || (r >= 175 && g >= 175 && b >= 175 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) <= 45)) name++;
                else if (IsYellow(r, g, b)) yellow++;
            }
        return (name, yellow);
    }
    private static bool IsWhite(int r, int g, int b) => r >= 200 && g >= 200 && b >= 200;

    /// <summary>
    /// Width (pixels) of letter-height white ink right before a name (screen x), within the name's height: the word
    /// "PINGED" on a ping line (measured 2 to 3.3 cap heights on recordings), against at most about one cap height
    /// for a kill line's weapon, arrow or downed icon. A white shape
    /// counts when it is at least half a cap height tall. White means grey and clearly brighter than the background
    /// around it (video compression dims thin white letters). Measuring stops at name-coloured ink (the name before)
    /// or a gap of more than a cap height.
    /// </summary>
    public static int WhiteTextLeft(Frame f, int y0, int y1, int x, double capH)
    {
        int ya = Math.Max(0, y0 - f.ScreenY), yb = Math.Min(f.Height, y1 - f.ScreenY);
        int start = x - f.ScreenX - 1, end = Math.Max(0, start - (int)(capH * 14));
        int maxGap = (int)Math.Ceiling(capH * 1.2), minTall = (int)Math.Ceiling(capH * 0.5);
        int width = 0, shapeW = 0, gap = 0, top = int.MaxValue, bottom = -1;
        bool started = false;
        if (yb <= ya || start <= end) return 0;
        // Background brightness: the darker quarter of the searched area.
        var lums = new List<int>();
        for (int cx = end; cx <= Math.Min(start, f.Width - 1); cx += 2)
            for (int y = ya; y < yb; y += 2)
            {
                int o = (y * f.Width + cx) * 4;
                lums.Add((f.Bgra[o] + f.Bgra[o + 1] * 2 + f.Bgra[o + 2]) / 4);
            }
        lums.Sort();
        int bg = lums.Count > 0 ? lums[lums.Count / 4] : 0;
        void EndShape()
        {
            if (shapeW > 0 && bottom - top + 1 >= minTall) width += shapeW;
            shapeW = 0; top = int.MaxValue; bottom = -1;
        }
        for (int cx = Math.Min(start, f.Width - 1); cx >= end; cx--)
        {
            bool colour = false;
            int colTop = int.MaxValue, colBottom = -1;
            for (int y = ya; y < yb; y++)
            {
                int o = (y * f.Width + cx) * 4;
                int b = f.Bgra[o], g = f.Bgra[o + 1], r = f.Bgra[o + 2];
                if (IsRed(r, g, b) || IsLime(r, g, b)) colour = true;
                else
                {
                    int lum = (b + g * 2 + r) / 4, sat = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                    if (sat < 50 && lum >= 120 && lum >= bg + 60) { colTop = Math.Min(colTop, y); colBottom = Math.Max(colBottom, y); }
                }
            }
            if (colour && started) break;
            if (colBottom >= 0)
            {
                shapeW++; started = true; gap = 0;
                top = Math.Min(top, colTop); bottom = Math.Max(bottom, colBottom);
            }
            else
            {
                EndShape();
                if (++gap > (started ? maxGap : maxGap * 2)) break;
            }
        }
        EndShape();
        return width;
    }

    /// <summary>
    /// Squad list entries are "[A1] NAME": the name is always preceded by a slot badge, a small box filled with one
    /// saturated colour (blue, pink, yellow...) holding a dark letter and digit. Kill feed and ping lines that happen to
    /// reach into the same part of the screen have nothing like it, so this tells a squad name from a feed name.
    /// Screen coordinates; <paramref name="x"/> is where the name starts.
    /// </summary>
    public static bool HasSlotBadge(Frame f, int x, int y, int h, double capH)
    {
        // The OCR box of the name can sit a few pixels above or below the badge: search a little beyond it.
        int pad = (int)Math.Ceiling(capH * 0.4);
        int lx = x - f.ScreenX, ly0 = Math.Max(0, y - f.ScreenY - pad), ly1 = Math.Min(f.Height, y - f.ScreenY + h + pad);
        int from = Math.Max(0, lx - (int)(capH * 3.5)), to = Math.Min(f.Width, lx);
        if (to - from < 4 || ly1 - ly0 < 3) return false;
        // A column is "filled" when it holds a solid vertical stretch of strong, bright colour at least 0.7 cap tall
        // (the badge's body; letter strokes break up, scenery is rarely this saturated).
        int minRun = (int)Math.Ceiling(capH * 0.7);
        var filled = new bool[to - from];
        for (int xx = from; xx < to; xx++)
        {
            int run = 0, best = 0;
            for (int yy = ly0; yy < ly1; yy++)
            {
                int o = (yy * f.Width + xx) * 4;
                int b = f.Bgra[o], g = f.Bgra[o + 1], r = f.Bgra[o + 2];
                int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                run = mx >= 110 && mx - mn >= 70 ? run + 1 : 0;
                best = Math.Max(best, run);
            }
            filled[xx - from] = best >= minRun;
        }
        // A run of filled columns the width of a badge (about 0.9 to 2.6 caps; the badge's dark letters may break it
        // once or twice for a pixel).
        int minW = (int)Math.Round(capH * 0.9), maxW = (int)Math.Round(capH * 2.6);
        for (int i = 0; i < filled.Length;)
        {
            if (!filled[i]) { i++; continue; }
            int j = i, holes = 0, lastFilled = i;
            while (j < filled.Length && (filled[j] || (holes < 3 && j + 1 < filled.Length && filled[j + 1])))
            {
                if (!filled[j]) holes++; else lastFilled = j;
                j++;
            }
            int w = lastFilled - i + 1;
            if (w >= minW && w <= maxW) return true;
            i = j + 1;
        }
        return false;
    }

    /// <summary>Pixels of a name colour (red or lime) in a box. Screen coordinates.</summary>
    public static int ColorInk(Frame f, int x, int y, int w, int h, TextColor color)
    {
        int n = 0;
        int x0 = Math.Max(0, x - f.ScreenX), y0 = Math.Max(0, y - f.ScreenY);
        int x1 = Math.Min(f.Width, x - f.ScreenX + w), y1 = Math.Min(f.Height, y - f.ScreenY + h);
        for (int yy = y0; yy < y1; yy++)
            for (int xx = x0; xx < x1; xx++)
            {
                int o = (yy * f.Width + xx) * 4;
                int b = f.Bgra[o], g = f.Bgra[o + 1], r = f.Bgra[o + 2];
                if (color == TextColor.Red ? IsRed(r, g, b) : IsLime(r, g, b)) n++;
            }
        return n;
    }

    /// <summary>
    /// How far left HUD text (white, or either name colour) continues from <paramref name="toX"/> on the band
    /// <paramref name="y0"/>..<paramref name="y1"/>: walks left column by column and stops at the first gap wider than
    /// <paramref name="maxGap"/> pixels. Returns the leftmost text column reached (screen x), or toX if none.
    /// </summary>
    public static int TextReachLeft(Frame f, int y0, int y1, int toX, int stopX, int maxGap)
    {
        int ya = Math.Max(0, y0 - f.ScreenY), yb = Math.Min(f.Height, y1 - f.ScreenY);
        int reach = toX, gap = 0;
        for (int x = toX - 1 - f.ScreenX; x >= Math.Max(0, stopX - f.ScreenX); x--)
        {
            if (x >= f.Width) continue;
            int ink = 0;
            for (int y = ya; y < yb && ink < 2; y++)
            {
                int o = (y * f.Width + x) * 4;
                int b = f.Bgra[o], g = f.Bgra[o + 1], r = f.Bgra[o + 2];
                // Bright and grey counts as white here: a line still fading in isn't full white yet.
                bool pale = Math.Min(r, Math.Min(g, b)) >= 140 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) <= 60;
                if (pale || IsRed(r, g, b) || IsLime(r, g, b)) ink++;
            }
            if (ink >= 2) { reach = x + f.ScreenX; gap = 0; }
            else if (++gap > maxGap) break;
        }
        return reach;
    }

    /// <param name="capH">Expected cap height of HUD text in pixels (scales with the game resolution).</param>
    public static List<TextRow> Find(Frame f, double capH) => Find(f, capH, out _);

    /// <param name="occluders">Boxes (screen coordinates) of name-coloured blobs too tall to be text: icons and markers
    /// in the name colours. A name under one is covered, not gone.</param>
    public static List<TextRow> Find(Frame f, double capH, out List<(int X0, int Y0, int X1, int Y1)> occluders)
    {
        int W = f.Width, H = f.Height;
        var px = f.Bgra;
        // Work buffers the size of the zone are rented and cleared (this runs many times a second).
        var pool = System.Buffers.ArrayPool<byte>.Shared;
        // 0 = nothing, 1 = red, 2 = lime
        var mask = pool.Rent(W * H);
        var band = pool.Rent(W * H);
        var label = System.Buffers.ArrayPool<int>.Shared.Rent(W * H);
        try
        {
        Array.Clear(mask, 0, W * H);
        Array.Clear(band, 0, W * H);
        Array.Clear(label, 0, W * H);

        for (int y = 0; y < H; y++)
        {
            int o = y * W * 4;
            for (int x = 0; x < W; x++, o += 4)
            {
                int b = px[o], g = px[o + 1], r = px[o + 2];
                byte m = IsRed(r, g, b) ? (byte)1 : IsLime(r, g, b) ? (byte)2 : (byte)0;
                if (m != 0) mask[y * W + x] = m;
            }
        }

        // Lines are found from letters, not from whole pixel rows: coloured scenery anywhere else in the zone (a lime
        // sign, a red light, a window edge) must not merge into a line and make it look too tall. Every connected blob
        // of ink is labelled; letters are blobs about cap height tall, and the letters of one line share their top and
        // bottom edges. Blobs taller than a line are scenery and are ignored completely.
        var rows = new List<TextRow>();
        int minH = (int)Math.Max(3, capH * 0.7), maxH = (int)Math.Ceiling(capH * 1.6);
        var comps = Label(mask, W, H, label);
        occluders = comps.Where(c => c.Y1 - c.Y0 > maxH)
                         .Select(c => (c.X0 + f.ScreenX, c.Y0 + f.ScreenY, c.X1 + f.ScreenX, c.Y1 + f.ScreenY)).ToList();
        var lines = GroupLetters(comps, capH, maxH);
        foreach (var (y0, y1) in lines)
        {
            if (y1 - y0 < minH) continue;
            // This line's own ink: every blob (letter fragments too) lying inside the line's height.
            int bx0 = W, bx1 = 0;
            for (int ci = 0; ci < comps.Count; ci++)
            {
                var c = comps[ci];
                if (c.Y1 - c.Y0 > maxH || c.Y0 < y0 - 1 || c.Y1 > y1 + 1) continue;
                for (int y = c.Y0; y < c.Y1; y++)
                    for (int x = c.X0; x < c.X1; x++)
                        if (label[y * W + x] == ci + 1) band[y * W + x] = mask[y * W + x];
                bx0 = Math.Min(bx0, c.X0); bx1 = Math.Max(bx1, c.X1);
            }
            int ly0 = Math.Max(0, y0 - 1), ly1 = Math.Min(H, y1 + 1);
            var runs = bx1 > bx0 ? RunsInBand(band, W, ly0, ly1, f.ScreenX, f.ScreenY, capH) : new List<TextRun>();
            if (runs.Count == 0) { Array.Clear(band, ly0 * W, (ly1 - ly0) * W); continue; }
            int right = LineEnd(band, W, ly0, ly1, runs.Max(r => r.X1) - f.ScreenX, capH) + f.ScreenX;
            Array.Clear(band, ly0 * W, (ly1 - ly0) * W);
            // White ink between the first and last name: short for a weapon icon (kill), long for "PINGED +" (ping).
            int white = 0;
            if (runs.Count >= 2)
            {
                int a = runs[0].X1 - f.ScreenX, c = runs[^1].X0 - f.ScreenX;
                for (int x = a; x < c; x++)
                {
                    bool any = false;
                    for (int yy = ly0; yy < ly1 && !any; yy++)
                    {
                        int o = (yy * W + x) * 4;
                        any = IsWhite(px[o + 2], px[o + 1], px[o]);
                    }
                    if (any) white++;
                }
            }
            rows.Add(new TextRow(runs, ly0 + f.ScreenY, ly1 + f.ScreenY, right, white));
        }
        rows.Sort((p, q) => p.Y0.CompareTo(q.Y0));
        return rows;
        }
        finally
        {
            pool.Return(mask); pool.Return(band);
            System.Buffers.ArrayPool<int>.Shared.Return(label);
        }
    }

    private readonly record struct Comp(int X0, int Y0, int X1, int Y1, int Pixels);

    /// <summary>8-connected blobs of the mask (W * H, cleared label buffer given); label[i] = blob index + 1 (0 = no ink).</summary>
    private static List<Comp> Label(byte[] mask, int W, int H, int[] label)
    {
        var comps = new List<Comp>();
        var stack = new Stack<int>();
        for (int i = 0; i < W * H; i++)
        {
            if (mask[i] == 0 || label[i] != 0) continue;
            int id = comps.Count + 1, x0 = W, y0 = H, x1 = 0, y1 = 0, n = 0;
            label[i] = id; stack.Push(i);
            while (stack.Count > 0)
            {
                int p = stack.Pop(), px = p % W, py = p / W;
                n++;
                if (px < x0) x0 = px; if (px >= x1) x1 = px + 1;
                if (py < y0) y0 = py; if (py >= y1) y1 = py + 1;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = py + dy;
                    if (ny < 0 || ny >= H) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = px + dx;
                        if (nx < 0 || nx >= W) continue;
                        int q = ny * W + nx;
                        if (mask[q] != 0 && label[q] == 0) { label[q] = id; stack.Push(q); }
                    }
                }
            }
            comps.Add(new Comp(x0, y0, x1, y1, n));
        }
        return comps;
    }

    /// <summary>
    /// Text lines from letter-sized blobs: letters of one line have the same top and bottom (within a couple of
    /// pixels for glow / anti-aliasing), scenery blobs rarely line up with each other. Returns each line's rows.
    /// </summary>
    private static List<(int Y0, int Y1)> GroupLetters(List<Comp> comps, double capH, int maxH)
    {
        double tol = Math.Max(2, capH * 0.25);
        var letters = comps.Where(c => c.Y1 - c.Y0 >= capH * 0.6 && c.Y1 - c.Y0 <= maxH).OrderBy(c => c.Y0).ToList();
        var groups = new List<(double Top, double Bottom, int N, int Y0, int Y1)>();
        foreach (var c in letters)
        {
            int gi = groups.FindIndex(g => Math.Abs(g.Top - c.Y0) <= tol && Math.Abs(g.Bottom - c.Y1) <= tol);
            if (gi < 0) { groups.Add((c.Y0, c.Y1, 1, c.Y0, c.Y1)); continue; }
            var g = groups[gi];
            groups[gi] = ((g.Top * g.N + c.Y0) / (g.N + 1), (g.Bottom * g.N + c.Y1) / (g.N + 1), g.N + 1,
                          Math.Min(g.Y0, c.Y0), Math.Max(g.Y1, c.Y1));
        }
        // Groups of the same line that came out apart (a letter with more glow): merge when mostly overlapping.
        var merged = new List<(int Y0, int Y1)>();
        foreach (var g in groups.OrderBy(g => g.Y0))
        {
            if (merged.Count > 0)
            {
                var m = merged[^1];
                int overlap = Math.Min(m.Y1, g.Y1) - Math.Max(m.Y0, g.Y0);
                if (overlap >= Math.Min(m.Y1 - m.Y0, g.Y1 - g.Y0) * 0.6 && Math.Max(m.Y1, g.Y1) - Math.Min(m.Y0, g.Y0) <= maxH)
                { merged[^1] = (Math.Min(m.Y0, g.Y0), Math.Max(m.Y1, g.Y1)); continue; }
            }
            merged.Add((g.Y0, g.Y1));
        }
        return merged;
    }

    /// <summary>
    /// The end of the line: follow coloured ink rightwards from the last name while gaps stay letter/word sized, so
    /// a partly detected last word (thin strokes over a bright scene) doesn't move the meter.
    /// </summary>
    private static int LineEnd(byte[] mask, int W, int y0, int y1, int from, double capH)
    {
        int gapMax = (int)Math.Ceiling(capH * 1.35), last = from - 1;
        for (int x = from; x < W && x - last <= gapMax; x++)
            for (int y = y0; y < y1; y++)
                if (mask[y * W + x] != 0) { last = x; break; }
        return last + 1;
    }

    private static List<TextRun> RunsInBand(byte[] mask, int W, int y0, int y1, int sx, int sy, double capH)
    {
        int bh = y1 - y0;
        // Column profile per colour.
        var col = new byte[W];
        for (int cx = 0; cx < W; cx++)
        {
            int red = 0, lime = 0;
            for (int y = y0; y < y1; y++)
            {
                byte m = mask[y * W + cx];
                if (m == 1) red++; else if (m == 2) lime++;
            }
            col[cx] = red == 0 && lime == 0 ? (byte)0 : red >= lime ? (byte)1 : (byte)2;
        }
        // Group columns into names: letters are ~0.2 cap apart, words (spaces) ~1 cap, a weapon icon 2+ caps.
        var runs = new List<TextRun>();
        int gapMax = (int)Math.Ceiling(capH * 1.35);
        int start = 0, last = 0; byte color = 0;
        int x = 0;
        while (x < W)
        {
            if (col[x] == 0) { x++; continue; }
            color = col[x]; start = last = x++;
            while (x < W)
            {
                if (col[x] == color) { last = x++; continue; }
                if (col[x] != 0) break;                 // colour change: next name
                if (x - last > gapMax) break;           // big gap: icon or end of name
                x++;
            }
            Emit();
            x = last + 1;
        }
        return runs;

        void Emit()
        {
            int x0 = start, x1 = last + 1, w = x1 - x0;
            // A name is at least ~3 characters wide; filled shapes (badges, health bars) have too many ink columns
            // relative to stroke text, so check how much of the box is ink.
            if (w < capH * 1.8) return;
            // Real HUD text has a cap-height-tall ink box; scene blobs are usually much shorter or taller.
            int inkTop = y1, inkBottom = y0;
            for (int y = y0; y < y1; y++)
                for (int xx = x0; xx < x1; xx++) if (mask[y * W + xx] != 0) { inkTop = Math.Min(inkTop, y); inkBottom = Math.Max(inkBottom, y + 1); break; }
            if (inkBottom - inkTop < capH * 0.7 || inkBottom - inkTop > capH * 1.45) return;
            int ink = 0;
            for (int y = y0; y < y1; y++)
                for (int xx = x0; xx < x1; xx++) if (mask[y * W + xx] != 0) ink++;
            double fill = ink / (double)(w * bh);
            if (fill < 0.08 || fill > 0.75) return;   // text with its glow; solid badges / bars are ~0.9
            // Tighten vertically to this run's own ink.
            int ty0 = y1, ty1 = y0;
            for (int y = y0; y < y1; y++)
                for (int xx = x0; xx < x1; xx++) if (mask[y * W + xx] != 0) { ty0 = Math.Min(ty0, y); ty1 = Math.Max(ty1, y + 1); break; }
            runs.Add(new TextRun(x0 + sx, ty0 + sy, x1 + sx, ty1 + sy, color == 1 ? TextColor.Red : TextColor.Green));
        }
    }
}
