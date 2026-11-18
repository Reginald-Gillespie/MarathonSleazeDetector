using System.Text;
using SleazeDetector.Capture;
using SleazeDetector.Ocr;

namespace SleazeDetector.Dev;

/// <summary>Dev tool: SleazeDetector --ocr-test out.txt img1.png [img2.png ...]: dumps OCR lines per zone.</summary>
public static class OcrTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        var sb = new StringBuilder();
        var reader = new HudOcr();
        foreach (var path in args.Skip(1))
        {
            var frame = Frame.FromFile(path);
            sb.AppendLine($"=== {Path.GetFileName(path)} ({frame.Width}x{frame.Height})");
            foreach (var mode in Enum.GetValues<Preprocess>())
            {
                sb.AppendLine($"  -- {mode}");
                foreach (var l in await reader.ReadAsync(frame, int.Parse(Environment.GetEnvironmentVariable("OCR_SCALE") ?? "2"), mode))
                    sb.AppendLine($"    [{l.X},{l.Y}] " + string.Join(" ", l.Words.Select(w => $"{w.Text}<{w.Color}>")));
            }
        }
        File.WriteAllText(args[0], sb.ToString());
        return 0;
    }
}
