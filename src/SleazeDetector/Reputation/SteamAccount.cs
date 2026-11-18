using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using SleazeDetector.Core;

namespace SleazeDetector.Reputation;

/// <summary>
/// Signing in to the ratings server with Steam. The browser goes to Steam's own sign-in page (through the server); the
/// server checks Steam's answer and sends a token back to this app on a local port that only exists for the sign-in.
/// The token is stored encrypted for the current Windows user (DPAPI). Steam's password never passes through the app.
/// </summary>
public sealed class SteamAccount
{
    private static string FilePath => Path.Combine(Paths.DataDir, "account.dat");
    private readonly object _gate = new();

    public string? Token { get; private set; }
    public string? SteamId { get; private set; }
    public bool SignedIn => Token != null;

    /// <summary>Signed in or out (raised on any thread).</summary>
    public event Action? Changed;

    public SteamAccount()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            var parts = Encoding.UTF8.GetString(plain).Split('\n');
            if (parts.Length == 2) { SteamId = parts[0]; Token = parts[1]; }
        }
        catch (Exception ex) { Log.Error("Could not read the saved sign-in", ex); }
    }

    private Task<bool>? _signingIn;

    /// <summary>Opens Steam sign-in in the browser and waits (up to 5 minutes) for the server to hand back a token.
    /// While one sign-in is waiting, further calls wait for that one.</summary>
    public Task<bool> SignInAsync(string server, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_signingIn is { IsCompleted: false } pending) return pending;
            return _signingIn = SignInCoreAsync(server, ct);
        }
    }

    private async Task<bool> SignInCoreAsync(string server, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Process.Start(new ProcessStartInfo($"{server}/auth/start?port={port}") { UseShellExecute = true });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            while (!timeout.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                using var stream = client.GetStream();
                var buf = new byte[4096];
                int n = await stream.ReadAsync(buf, timeout.Token);
                // "GET /signed-in?token=...&steamId=... HTTP/1.1"
                var line = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0];
                var target = line.Split(' ').ElementAtOrDefault(1) ?? "";
                if (!target.StartsWith("/signed-in?")) { await Respond(stream, 404, "Not found"); continue; }
                var q = System.Web.HttpUtility.ParseQueryString(target["/signed-in?".Length..]);
                string? token = q["token"], steamId = q["steamId"];
                if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(steamId))
                {
                    await Respond(stream, 400, "Sign-in failed. Try again from the app's Settings.");
                    return false;
                }
                await Respond(stream, 200, "Signed in. You can close this tab and go back to the game.");
                Save(steamId, token);
                Log.Info($"Signed in with Steam ({steamId})");
                return true;
            }
            return false;
        }
        catch (OperationCanceledException) { return false; }
        finally { listener.Stop(); }
    }

    private static async Task Respond(NetworkStream s, int code, string text)
    {
        var html = $"<!doctype html><meta charset=\"utf-8\"><title>Sleaze Detector</title>" +
                   $"<body style=\"font:16px system-ui;margin:40px auto;max-width:480px;padding:0 16px\"><p>{WebUtility.HtmlEncode(text)}</p>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await s.WriteAsync(head);
        await s.WriteAsync(body);
    }

    private void Save(string steamId, string token)
    {
        lock (_gate)
        {
            SteamId = steamId; Token = token;
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes($"{steamId}\n{token}"), null, DataProtectionScope.CurrentUser));
        }
        Changed?.Invoke();
    }

    /// <summary>Forgets the sign-in (also when the server says the token is no longer valid).</summary>
    public void SignOut()
    {
        lock (_gate)
        {
            if (Token == null) return;
            Token = null; SteamId = null;
            try { File.Delete(FilePath); } catch { }
        }
        Log.Info("Signed out of the ratings server");
        Changed?.Invoke();
    }
}
