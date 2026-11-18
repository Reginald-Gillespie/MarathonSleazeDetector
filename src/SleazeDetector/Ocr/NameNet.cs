using System.Reflection;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SleazeDetector.Capture;

namespace SleazeDetector.Ocr;

/// <summary>
/// Learned name reader: a small CRNN (tools/namereader) trained on the game's own HUD font drawn over game scenery and
/// pushed through real video compression, plus labelled names from recordings. Reads a name box in a few milliseconds
/// on the CPU, including the recordings where colour detail is lost (red text on orange), which the font matcher
/// cannot read reliably.
/// The crop and scaling here must match tools/namereader/common.py exactly.
/// </summary>
public sealed class NameNet : IDisposable
{
    public const int HIn = 32, WMax = 448;
    private const string Charset = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_#-.*+ ";
    private const string Resource = "SleazeDetector.Assets.namenet.onnx";

    private readonly InferenceSession _session;
    private readonly object _lock = new();

    private NameNet(InferenceSession s) { _session = s; }

    /// <summary>The embedded model, or null if the build has none or it cannot be loaded.</summary>
    public static NameNet? Load(string? path = null)
    {
        try
        {
            byte[] bytes;
            if (path != null) bytes = File.ReadAllBytes(path);
            else
            {
                using var st = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource);
                if (st == null) return null;
                using var ms = new MemoryStream();
                st.CopyTo(ms);
                bytes = ms.ToArray();
            }
            // Two threads: a read takes a few milliseconds either way, and the game needs the cores.
            var opts = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            if (Environment.GetEnvironmentVariable("SLEAZE_ORT_ARENA") == "0") { opts.EnableCpuMemArena = false; opts.EnableMemoryPattern = false; }
            return new NameNet(new InferenceSession(bytes, opts));
        }
        catch (Exception e)
        {
            Core.Log.Info($"Name model: could not load ({e.Message})");
            return null;
        }
    }

    /// <summary>Text read (spaces inside names kept), confidence per character, and overall confidence.</summary>
    public sealed record Reading(string Text, double[] CharConf, double Score);

    /// <summary>Reads the name whose colour box (screen pixels) is given, on a game <paramref name="gameHeight"/> high.</summary>
    public Reading? Read(Frame f, int bx, int by, int bw, int bh, int gameHeight)
    {
        if (bw < 3 || bh < 4) return null;
        // The box plus a margin, as in training.
        double capRef = 13.0 / 1200 * gameHeight;
        int mx = (int)Math.Round(capRef * 1.2), my = (int)Math.Round(capRef * 0.6);
        int lx = bx - f.ScreenX, ly = by - f.ScreenY;
        int x0 = Math.Max(0, lx - mx), y0 = Math.Max(0, ly - my);
        int x1 = Math.Min(f.Width, lx + bw + mx), y1 = Math.Min(f.Height, ly + bh + my);
        int cw = x1 - x0, ch = y1 - y0;
        if (cw < 4 || ch < 4) return null;
        int nw = Math.Clamp((int)Math.Round(cw * (double)HIn / ch), 8, WMax);
        // Run at the line's own width plus a little blank (as in training), not the full WMax: twice as fast.
        int inW = Math.Min(WMax, (nw + 3) / 4 * 4 + 16);
        var input = new DenseTensor<float>(new[] { 1, 3, HIn, inW });
        var wx = Weights(cw, nw, x0);
        var wy = Weights(ch, HIn, y0);
        var row = new float[nw * 3];
        for (int oy = 0; oy < HIn; oy++)
        {
            Array.Clear(row);
            foreach (var (sy, ky) in wy[oy])
            {
                int rowBase = sy * f.Width;
                for (int ox = 0; ox < nw; ox++)
                {
                    float r = 0, g = 0, b = 0;
                    foreach (var (sx, kx) in wx[ox])
                    {
                        int i = (rowBase + sx) * 4;
                        r += f.Bgra[i + 2] * kx; g += f.Bgra[i + 1] * kx; b += f.Bgra[i] * kx;
                    }
                    row[ox * 3] += r * ky; row[ox * 3 + 1] += g * ky; row[ox * 3 + 2] += b * ky;
                }
            }
            for (int ox = 0; ox < nw; ox++)
            {
                // Rounded to bytes, as the training images were stored.
                input[0, 0, oy, ox] = MathF.Round(Math.Clamp(row[ox * 3], 0, 255));
                input[0, 1, oy, ox] = MathF.Round(Math.Clamp(row[ox * 3 + 1], 0, 255));
                input[0, 2, oy, ox] = MathF.Round(Math.Clamp(row[ox * 3 + 2], 0, 255));
            }
        }
        float[] p;
        int T, C;
        lock (_lock)
        {
            using var res = _session.Run(new[] { NamedOnnxValue.CreateFromTensor("x", input) });
            var t = res.First().AsTensor<float>();
            T = t.Dimensions[1]; C = t.Dimensions[2];
            p = t.ToArray();
        }
        // Best path: the most likely class per step, repeats merged, blanks (0) dropped. A character's confidence is
        // its highest probability over the steps that emitted it.
        var text = new System.Text.StringBuilder();
        var conf = new List<double>();
        int prev = 0;
        for (int s = 0; s < T; s++)
        {
            int best = 0; float bp = p[s * C];
            for (int k = 1; k < C; k++) if (p[s * C + k] > bp) { bp = p[s * C + k]; best = k; }
            if (best != 0 && best != prev) { text.Append(Charset[best - 1]); conf.Add(bp); }
            else if (best != 0 && best == prev) conf[^1] = Math.Max(conf[^1], bp);
            prev = best;
        }
        // Spaces at the ends are not part of a name.
        int a = 0, e = text.Length;
        while (a < e && text[a] == ' ') a++;
        while (e > a && text[e - 1] == ' ') e--;
        if (e <= a) return new Reading("", Array.Empty<double>(), 0);
        var cc = conf.Skip(a).Take(e - a).ToArray();
        return new Reading(text.ToString(a, e - a), cc, cc.Average());
    }

    /// <summary>
    /// Resampling weights from <paramref name="n"/> source pixels (starting at <paramref name="offset"/>) to
    /// <paramref name="m"/>: pixel-area averaging when shrinking, bilinear when enlarging (OpenCV's INTER_AREA).
    /// </summary>
    private static List<(int, float)>[] Weights(int n, int m, int offset)
    {
        var w = new List<(int, float)>[m];
        double s = (double)n / m;
        for (int i = 0; i < m; i++)
        {
            w[i] = new List<(int, float)>();
            if (s >= 1)
            {
                double a = i * s, b = (i + 1) * s;
                for (int j = (int)Math.Floor(a); j < Math.Min(n, (int)Math.Ceiling(b)); j++)
                {
                    double ov = Math.Min(b, j + 1) - Math.Max(a, j);
                    if (ov > 1e-9) w[i].Add((offset + j, (float)(ov / s)));
                }
            }
            else
            {
                double c = (i + 0.5) * s - 0.5;
                int j0 = (int)Math.Floor(c);
                double t = c - j0;
                int ja = Math.Clamp(j0, 0, n - 1), jb = Math.Clamp(j0 + 1, 0, n - 1);
                w[i].Add((offset + ja, (float)(1 - t)));
                w[i].Add((offset + jb, (float)t));
            }
        }
        return w;
    }

    public void Dispose() => _session.Dispose();
}
