using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SleazeDetector.Capture;

/// <summary>
/// Screen capture through DXGI Desktop Duplication (the API OBS display capture uses). Unlike GDI BitBlt it sees
/// content shown on hardware planes / in independent-flip mode (fullscreen video players and borderless games)
/// which GDI can return as black or stale. One duplication per monitor; only the requested rectangle is copied
/// out of the GPU frame. Windows marked WDA_EXCLUDEFROMCAPTURE (our overlay) are left out, as with GDI.
/// </summary>
public sealed class DesktopDuplication : IDisposable
{
    private readonly object _gate = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _ctx;
    private IDXGIOutputDuplication? _dup;
    private ID3D11Texture2D? _last;            // copy of the latest desktop frame (GPU)
    // CPU-readable copies, one per requested size (the same few zones are captured over and over).
    private readonly Dictionary<(int, int), ID3D11Texture2D> _staging = new();
    private int _outX, _outY, _outW, _outH;
    private IntPtr _monitor;
    private DateTime _retryAfter = DateTime.MinValue;

    public static DesktopDuplication Shared { get; } = new();

    /// <summary>Returns the rectangle (screen pixels) or null if duplication isn't available (caller falls back to GDI).</summary>
    public Frame? Capture(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;
        lock (_gate)
        {
            try
            {
                var mon = MonitorFromPoint(new POINT { X = x + w / 2, Y = y + h / 2 }, 2 /* MONITOR_DEFAULTTONEAREST */);
                if (_dup == null || mon != _monitor)
                {
                    if (DateTime.Now < _retryAfter) return null;
                    if (!Init(mon)) { _retryAfter = DateTime.Now.AddSeconds(5); return null; }
                }
                Acquire();
                if (_last == null) return null;

                // Clip to this monitor.
                int lx = Math.Clamp(x - _outX, 0, _outW), ly = Math.Clamp(y - _outY, 0, _outH);
                int rx = Math.Clamp(x - _outX + w, 0, _outW), ry = Math.Clamp(y - _outY + h, 0, _outH);
                if (rx <= lx || ry <= ly) return null;
                int cw = rx - lx, ch = ry - ly;
                var staging = Staging(cw, ch);
                _ctx!.CopySubresourceRegion(staging, 0, 0, 0, 0, _last, 0, new Vortice.Mathematics.Box(lx, ly, 0, rx, ry, 1));
                var map = _ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                var buf = Frame.Rent(w * h * 4);
                if (cw != w || ch != h) Array.Clear(buf);   // part of the rectangle is off this monitor
                try
                {
                    int ox = lx - (x - _outX), oy = ly - (y - _outY);
                    for (int row = 0; row < ch; row++)
                        Marshal.Copy(map.DataPointer + row * (int)map.RowPitch, buf, ((oy + row) * w + ox) * 4, cw * 4);
                }
                finally { _ctx.Unmap(staging, 0); }
                return Frame.Pooled(w, h, buf, x, y);
            }
            catch (Exception ex)
            {
                Core.Log.Error("Desktop duplication failed; falling back to GDI capture", ex);
                Reset();
                _retryAfter = DateTime.Now.AddSeconds(5);
                return null;
            }
        }
    }

    private void Acquire()
    {
        // Right after starting there is no frame yet: wait briefly for the first one, afterwards never block.
        var res = _dup!.AcquireNextFrame(_last == null ? 100u : 0u, out var info, out var resource);
        if (res.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) return;          // nothing changed: keep the last frame
        if (res.Code == Vortice.DXGI.ResultCode.AccessLost.Code) { Reset(); return; } // mode change / secure desktop
        res.CheckError();
        try
        {
            using var tex = resource!.QueryInterface<ID3D11Texture2D>();
            if (_last == null)
            {
                var d = tex.Description;
                _last = _device!.CreateTexture2D(new Texture2DDescription
                {
                    Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = 1, Format = d.Format,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                });
            }
            _ctx!.CopyResource(_last, tex);
        }
        finally
        {
            resource?.Dispose();
            _dup.ReleaseFrame();
        }
    }

    private ID3D11Texture2D Staging(int w, int h)
    {
        if (_staging.TryGetValue((w, h), out var t)) return t;
        // Sizes come from a handful of zones; if they keep changing (window resized), start over.
        if (_staging.Count >= 12) { foreach (var s in _staging.Values) s.Dispose(); _staging.Clear(); }
        t = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = _last!.Description.Format,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        return _staging[(w, h)] = t;
    }

    private bool Init(IntPtr monitor)
    {
        Reset();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var desc = output.Description;
                        if (desc.Monitor != monitor) continue;
                        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, null, out _device, out _ctx).CheckError();
                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        _dup = output1.DuplicateOutput(_device);
                        _outX = desc.DesktopCoordinates.Left; _outY = desc.DesktopCoordinates.Top;
                        _outW = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
                        _outH = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
                        _monitor = monitor;
                        Core.Log.Info($"Desktop duplication started on {desc.DeviceName} ({_outW}x{_outH})");
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private void Reset()
    {
        foreach (var s in _staging.Values) s.Dispose();
        _staging.Clear();
        _last?.Dispose(); _last = null;
        _dup?.Dispose(); _dup = null;
        _ctx?.Dispose(); _ctx = null;
        _device?.Dispose(); _device = null;
        _monitor = IntPtr.Zero;
    }

    public void Dispose() { lock (_gate) Reset(); }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
}
