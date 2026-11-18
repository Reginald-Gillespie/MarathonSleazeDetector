using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SleazeDetector.Core;

/// <summary>
/// Reads a few UI images straight out of the user's own Marathon install, read-only, so nothing from the game ships
/// with this app. Static file reading only (Bungie permits viewing assets extracted from the data files); it never
/// touches the running game. Everything fails soft: when the install, a package or the format is not what we expect,
/// the image is simply null and callers fall back to text.
/// </summary>
public static class GameAssets
{
    /// <summary>The Rook-with-a-red-cross profile icon, used on the meter for runners you flagged as killing Rooks.</summary>
    public const uint RookKillerIconTag = 0x80A667C2;

    private static readonly Lazy<Task<ImageSource?>> _rookKiller = new(() => Task.Run(() => LoadIcon(RookKillerIconTag)));

    /// <summary>The icon once loaded (null while loading or when unavailable).</summary>
    public static ImageSource? RookKillerIcon => _rookKiller.Value.IsCompletedSuccessfully ? _rookKiller.Value.Result : null;

    /// <summary>Starts loading in the background; the task completes with null when the icon cannot be read.</summary>
    public static Task<ImageSource?> LoadRookKillerIconAsync() => _rookKiller.Value;

    private static ImageSource? _round;

    /// <summary>The Rook-killer icon clipped to a circle (window icons), or null while unavailable.</summary>
    public static ImageSource? RoundRookIcon(int px = 64)
    {
        if (_round != null) return _round;
        if (RookKillerIcon is not { } img) return null;
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushClip(new System.Windows.Media.EllipseGeometry(new System.Windows.Point(px / 2.0, px / 2.0), px / 2.0, px / 2.0));
            dc.DrawImage(img, new System.Windows.Rect(0, 0, px, px));
        }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(px, px, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return _round = rtb;
    }

    /// <summary>
    /// The icon as a Windows icon (round, <paramref name="px"/> square) for the tray, or null. Built from the game's own
    /// file on this computer, never shipped with the app.
    /// </summary>
    public static System.Drawing.Icon? ToTrayIcon(ImageSource? img, int px = 64)
    {
        if (img == null) return null;
        try
        {
            var dv = new System.Windows.Media.DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushClip(new System.Windows.Media.EllipseGeometry(new System.Windows.Point(px / 2.0, px / 2.0), px / 2.0, px / 2.0));
                dc.DrawImage(img, new System.Windows.Rect(0, 0, px, px));
            }
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(px, px, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var png = new MemoryStream();
            enc.Save(png);
            // An .ico holding one PNG image: 6-byte header, one 16-byte directory entry, then the PNG.
            using var ico = new MemoryStream();
            using (var w = new BinaryWriter(ico, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)1);
                w.Write((byte)(px >= 256 ? 0 : px)); w.Write((byte)(px >= 256 ? 0 : px)); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32); w.Write((int)png.Length); w.Write(22);
                w.Write(png.ToArray());
            }
            ico.Position = 0;
            return new System.Drawing.Icon(ico);
        }
        catch (Exception ex)
        {
            Log.Info($"Game assets: tray icon: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static ImageSource? LoadIcon(uint tag)
    {
        try
        {
            var dir = FindInstall();
            if (dir == null) { Log.Info("Game assets: Marathon install not found"); return null; }
            using var reader = new TigerReader(dir);
            var img = reader.ReadTexture(tag);
            if (img == null) Log.Info($"Game assets: texture {tag:x8} not readable");
            return img;
        }
        catch (Exception ex)
        {
            Log.Info($"Game assets: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Full name of the font the HUD draws runner names in (kill feed, pings, prox chat).</summary>
    public const string HudFontName = "PP Fraktion Mono Bold";

    /// <summary>
    /// The HUD font file, read from the game's UI packages (an OpenType font stored as-is) and cached locally under the
    /// app's data folder, keyed by the package it came from so a game patch refreshes it. Null when unavailable.
    /// </summary>
    public static string? HudFontFile()
    {
        try
        {
            var dir = FindInstall();
            if (dir == null) { Log.Info("Game assets: Marathon install not found (HUD font)"); return null; }
            var cacheDir = Path.Combine(Paths.DataDir, "cache");
            var cache = Path.Combine(cacheDir, HudFontName.Replace(" ", "") + ".otf");
            var keyFile = cache + ".src";
            using var reader = new TigerReader(dir);
            foreach (var pkg in reader.PackagesLikely("ui"))
            {
                var key = $"{pkg}|{new FileInfo(pkg).Length}|{File.GetLastWriteTimeUtc(pkg).Ticks}";
                if (File.Exists(cache) && File.Exists(keyFile) && File.ReadAllText(keyFile) == key) return cache;
                foreach (var bytes in reader.EntriesStartingWith(pkg, "OTTO"u8.ToArray()))
                {
                    if (OpenTypeFullName(bytes) != HudFontName) continue;
                    Directory.CreateDirectory(cacheDir);
                    File.WriteAllBytes(cache, bytes);
                    File.WriteAllText(keyFile, key);
                    Log.Info($"Game assets: HUD font found in {Path.GetFileName(pkg)}");
                    return cache;
                }
            }
            Log.Info("Game assets: HUD font not found in the game packages");
        }
        catch (Exception ex) { Log.Info($"Game assets: HUD font: {ex.GetType().Name}: {ex.Message}"); }
        return null;
    }

    /// <summary>The full font name (name ID 4) of an OpenType file, or null.</summary>
    public static string? OpenTypeFullName(byte[] f)
    {
        static int U16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
        static int U32(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        if (f.Length < 12) return null;
        int tables = U16(f, 4);
        for (int i = 0; i < tables; i++)
        {
            int rec = 12 + i * 16;
            if (rec + 16 > f.Length) return null;
            if (f[rec] != 'n' || f[rec + 1] != 'a' || f[rec + 2] != 'm' || f[rec + 3] != 'e') continue;
            int off = U32(f, rec + 8);
            int count = U16(f, off + 2), strings = off + U16(f, off + 4);
            for (int r = 0; r < count; r++)
            {
                int nr = off + 6 + r * 12;
                int platform = U16(f, nr), nameId = U16(f, nr + 6), len = U16(f, nr + 8), so = strings + U16(f, nr + 10);
                if (nameId != 4 || so + len > f.Length) continue;
                return platform == 3 || platform == 0
                    ? System.Text.Encoding.BigEndianUnicode.GetString(f, so, len)
                    : System.Text.Encoding.Latin1.GetString(f, so, len);
            }
        }
        return null;
    }

    /// <summary>The Marathon folder in any Steam library (registry SteamPath + libraryfolders.vdf).</summary>
    public static string? FindInstall()
    {
        var roots = new List<string>();
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string sp) roots.Add(sp);
        }
        catch { }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));

        var libs = new List<string>();
        foreach (var root in roots)
        {
            libs.Add(root);
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        foreach (var lib in libs)
        {
            var game = Path.Combine(lib, "steamapps", "common", "Marathon");
            if (Directory.Exists(Path.Combine(game, "packages")) && File.Exists(Path.Combine(game, "bin", "x64", "oo2core_9_win64.dll")))
                return game;
        }
        return null;
    }
}

/// <summary>
/// Minimal read-only reader for Tiger engine .pkg files (layout per the open-source tiger-pkg project): entry and
/// block tables, AES-GCM block encryption and Oodle compression, using the Oodle DLL that ships with the game.
/// </summary>
internal sealed class TigerReader : IDisposable
{
    private const int BlockSize = 0x40000;
    private const int FCompressed = 0x1, FEncrypted = 0x2, FAltKey = 0x4, FRedacted = 0x8;
    private static readonly byte[] Key0 = { 0xD6, 0x2A, 0xB2, 0xC1, 0x0C, 0xC0, 0x1B, 0xC5, 0x35, 0xDB, 0x7B, 0x86, 0x55, 0xC7, 0xDC, 0x3B };
    private static readonly byte[] Key1 = { 0x3A, 0x4A, 0x5D, 0x36, 0x73, 0xA6, 0x60, 0x58, 0x7E, 0x63, 0xE6, 0x76, 0xE4, 0x08, 0x92, 0xB5 };
    private static readonly byte[] Nonce = { 0x84, 0xDF, 0x11, 0xC0, 0xAC, 0xAB, 0xFA, 0x20, 0x33, 0x11, 0x26, 0x99 };

    private readonly string _packages;
    private readonly IntPtr _oodle;
    private readonly OodleDecompress _decompress;
    private readonly Dictionary<int, Package?> _byId = new();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long OodleDecompress(byte[] src, long srcLen, byte[] dst, long dstLen, int fuzz, int crc, int verbose,
        IntPtr dstBase, long dstBaseSize, IntPtr cb, IntPtr cbData, IntPtr scratch, long scratchSize, int threadPhase);

    public TigerReader(string gameDir)
    {
        _packages = Path.Combine(gameDir, "packages");
        _oodle = NativeLibrary.Load(Path.Combine(gameDir, "bin", "x64", "oo2core_9_win64.dll"));
        _decompress = Marshal.GetDelegateForFunctionPointer<OodleDecompress>(NativeLibrary.GetExport(_oodle, "OodleLZ_Decompress"));
    }

    public void Dispose()
    {
        foreach (var p in _byId.Values) p?.Dispose();
        // The Oodle DLL stays loaded; freeing it while delegates may still be referenced is not worth the risk.
    }

    /// <summary>Decodes an uncompressed RGBA8 (DXGI 28/29) or BGRA8 (87/91) texture header tag into a frozen bitmap.</summary>
    public BitmapSource? ReadTexture(uint tag)
    {
        var (hp, he) = Resolve(tag);
        if (hp == null || he.Type != 32 || he.Sub != 1 || he.Size < 0x40) return null;
        var h = hp.Read(he, this);
        uint fmt = BitConverter.ToUInt32(h, 0x04);
        int w = BitConverter.ToUInt16(h, 0x22), hh = BitConverter.ToUInt16(h, 0x24);
        uint large = BitConverter.ToUInt32(h, 0x3C);
        if (w is <= 0 or > 1024 || hh is <= 0 or > 1024) return null;

        byte[]? data;
        if (large != 0xFFFFFFFF) data = ReadTag(large);
        else if (he.Ref != 0) data = ReadTag(he.Ref);
        else data = he.Index + 1 < hp.Entries.Length ? hp.Read(hp.Entries[he.Index + 1], this) : null;
        int need = w * hh * 4;
        if (data == null || data.Length < need) return null;

        var px = new byte[need];
        if (fmt is 28 or 29)
            for (int i = 0; i < need; i += 4) { px[i] = data[i + 2]; px[i + 1] = data[i + 1]; px[i + 2] = data[i]; px[i + 3] = data[i + 3]; }
        else if (fmt is 87 or 91) Buffer.BlockCopy(data, 0, px, 0, need);
        else return null;

        var bmp = BitmapSource.Create(w, hh, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Package files (highest patch of each) whose name contains the word, most likely first.</summary>
    public IEnumerable<string> PackagesLikely(string word) =>
        Directory.EnumerateFiles(_packages, "*.pkg")
                 .Where(f => Path.GetFileName(f).Contains($"_{word}_", StringComparison.OrdinalIgnoreCase))
                 .GroupBy(f => f[..f.LastIndexOf('_')])
                 .Select(g => g.OrderByDescending(f => int.TryParse(Path.GetFileNameWithoutExtension(f)[(Path.GetFileNameWithoutExtension(f).LastIndexOf('_') + 1)..], out int n) ? n : 0).First())
                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every entry of a package whose data starts with the given bytes (e.g. "OTTO" for OpenType fonts).</summary>
    public IEnumerable<byte[]> EntriesStartingWith(string path, byte[] magic)
    {
        int pid;
        using (var fs = File.OpenRead(path))
        {
            var b = new byte[2];
            fs.Seek(0x10, SeekOrigin.Begin);
            if (fs.Read(b, 0, 2) != 2) yield break;
            pid = BitConverter.ToUInt16(b);
        }
        var p = new Package(path, pid);
        foreach (var e in p.Entries)
        {
            if (e.Size < 1024) continue;
            byte[] head;
            try
            {
                var blk = p.GetBlock(e.Block, this);
                if (e.Offset + magic.Length > blk.Length) continue;
                head = blk[e.Offset..(e.Offset + magic.Length)];
            }
            catch (Exception) { continue; }
            if (!head.AsSpan().SequenceEqual(magic)) continue;
            byte[] data;
            try { data = p.Read(e, this); } catch (Exception) { continue; }
            yield return data;
        }
    }

    public byte[]? ReadTag(uint tag)
    {
        var (p, e) = Resolve(tag);
        return p == null ? null : p.Read(e, this);
    }

    private (Package?, Entry) Resolve(uint tag)
    {
        int pid = (int)(((tag - 0x80800000u) >> 13) & 0xFFFF), idx = (int)(tag & 0x1FFF);
        var p = PackageById(pid);
        if (p == null || idx >= p.Entries.Length) return (null, default);
        return (p, p.Entries[idx]);
    }

    /// <summary>The highest patch of the package with this id (file names carry the id as 4 hex digits; the header confirms it).</summary>
    private Package? PackageById(int pid)
    {
        if (_byId.TryGetValue(pid, out var cached)) return cached;
        Package? best = null;
        int bestPatch = -1;
        foreach (var f in Directory.EnumerateFiles(_packages, $"*_{pid:x4}_*.pkg"))
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            if (!int.TryParse(stem[(stem.LastIndexOf('_') + 1)..], out int patch) || patch <= bestPatch) continue;
            using (var fs = File.OpenRead(f))
            {
                var b = new byte[2];
                fs.Seek(0x10, SeekOrigin.Begin);
                if (fs.Read(b, 0, 2) != 2 || BitConverter.ToUInt16(b) != pid) continue;
            }
            bestPatch = patch;
            best?.Dispose();
            best = new Package(f, pid);
        }
        _byId[pid] = best;
        return best;
    }

    internal byte[] DecodeBlock(Package p, int i)
    {
        var b = p.Blocks[i];
        if ((b.Flags & FRedacted) != 0) throw new InvalidDataException("redacted block");
        var data = new byte[b.Size];
        using (var fs = File.OpenRead($"{p.Base}_{b.Patch}.pkg"))
        {
            fs.Seek(b.Offset, SeekOrigin.Begin);
            fs.ReadExactly(data);
        }
        if ((b.Flags & FEncrypted) != 0)
        {
            using var aes = new AesGcm((b.Flags & FAltKey) != 0 ? Key1 : Key0, 16);
            var plain = new byte[data.Length];
            aes.Decrypt(p.Nonce, data, b.GcmTag, plain);
            data = plain;
        }
        if ((b.Flags & FCompressed) != 0)
        {
            var dst = new byte[BlockSize];
            long n = _decompress(data, data.Length, dst, dst.Length, 0, 0, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 3);
            if (n <= 0) throw new InvalidDataException("oodle failed");
            Array.Resize(ref dst, (int)n);
            data = dst;
        }
        return data;
    }

    internal readonly record struct Entry(int Index, uint Ref, int Type, int Sub, int Block, int Offset, uint Size);

    internal readonly record struct Block(uint Offset, uint Size, int Patch, int Flags, byte[] GcmTag);

    internal sealed class Package : IDisposable
    {
        public readonly string Base;
        public readonly Entry[] Entries;
        public readonly Block[] Blocks;
        public readonly byte[] Nonce;
        private readonly Dictionary<int, byte[]> _cache = new();

        public Package(string path, int pid)
        {
            Base = path[..path.LastIndexOf('_')];
            using var fs = File.OpenRead(path);
            var h = new byte[0x124];
            fs.ReadExactly(h);
            uint etSize = BitConverter.ToUInt32(h, 0x60), etOff = BitConverter.ToUInt32(h, 0x64);
            uint btSize = BitConverter.ToUInt32(h, 0x68), btOff = BitConverter.ToUInt32(h, 0x6C);
            if (etSize > 0x2000 || btSize > 0x100000) throw new InvalidDataException("unexpected package tables");

            var raw = new byte[etSize * 16];
            fs.Seek(etOff, SeekOrigin.Begin);
            fs.ReadExactly(raw);
            Entries = new Entry[etSize];
            for (int i = 0; i < etSize; i++)
            {
                uint r = BitConverter.ToUInt32(raw, i * 16), ti = BitConverter.ToUInt32(raw, i * 16 + 4);
                ulong bi = BitConverter.ToUInt64(raw, i * 16 + 8);
                Entries[i] = new Entry(i, r, (int)((ti >> 9) & 0x7F), (int)((ti >> 6) & 7),
                    (int)(bi & 0x3FFF), (int)(((bi >> 14) & 0x3FFF) << 4), (uint)((bi >> 28) & 0xFFFFFFFF));
            }

            raw = new byte[btSize * 48];
            fs.Seek(btOff, SeekOrigin.Begin);
            fs.ReadExactly(raw);
            Blocks = new Block[btSize];
            for (int i = 0; i < btSize; i++)
            {
                int o = i * 48;
                Blocks[i] = new Block(BitConverter.ToUInt32(raw, o), BitConverter.ToUInt32(raw, o + 4),
                    BitConverter.ToUInt16(raw, o + 8), BitConverter.ToUInt16(raw, o + 10), raw[(o + 32)..(o + 48)]);
            }

            Nonce = (byte[])TigerReader.Nonce.Clone();
            Nonce[0] ^= (byte)((pid >> 8) & 0xFF);
            Nonce[1] = 0xEA;
            Nonce[11] ^= (byte)(pid & 0xFF);
        }

        public byte[] GetBlock(int b, TigerReader r)
        {
            if (!_cache.TryGetValue(b, out var data))
            {
                if (_cache.Count > 16) _cache.Clear();
                _cache[b] = data = r.DecodeBlock(this, b);
            }
            return data;
        }

        public byte[] Read(Entry e, TigerReader r)
        {
            var output = new byte[e.Size];
            int got = 0, b = e.Block, off = e.Offset;
            while (got < e.Size)
            {
                if (b >= Blocks.Length) throw new InvalidDataException("entry runs past the block table");
                if (!_cache.TryGetValue(b, out var data))
                {
                    if (_cache.Count > 16) _cache.Clear();
                    _cache[b] = data = r.DecodeBlock(this, b);
                }
                int n = (int)Math.Min(e.Size - got, data.Length - off);
                if (n <= 0) throw new InvalidDataException("short block");
                Buffer.BlockCopy(data, off, output, got, n);
                got += n; b++; off = 0;
            }
            return output;
        }

        public void Dispose() => _cache.Clear();
    }
}
