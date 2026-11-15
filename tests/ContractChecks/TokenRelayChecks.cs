using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ShiroBot.LagrangeAdapter;

namespace ContractChecks;
internal static class TokenRelayChecks
{
    private const string Key = "test-relay-credential-0123456789abcdef";
    private const string Guid = "0123456789abcdef0123456789abcdef";
    private const string Establish = "trpc.o3.ecdh_access.EcdhAccess.SsoEstablishShareKey";
    private const string Secure = "trpc.o3.ecdh_access.EcdhAccess.SsoSecureAccess";
    private static int Port()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start(); return ((IPEndPoint)socket.LocalEndpoint).Port;
    }
    private static void Check(bool value, string name)
    { if (!value) throw new Exception("Token relay: " + name); }
    private static async Task AutomaticSetup()
    {
        var directory = Directory.CreateTempSubdirectory("lagrange-relay-check-").FullName;
        var keyFile = Path.Combine(directory, "first.relay.key");
        var info = Path.Combine(directory, "first.relay.json");
        try
        {
            var key = LagrangeTokenRelayFiles.LoadOrCreateKey(keyFile);
            Check(key.Length == 64 && LagrangeTokenRelayFiles.LoadOrCreateKey(keyFile) == key, "generated key persists");
            Check(LagrangeTokenRelayFiles.LoadOrCreateKey(Path.Combine(directory, "second.relay.key")) != key, "instance keys isolated");
            if (!OperatingSystem.IsWindows())
            {
                Check(File.GetUnixFileMode(keyFile) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "private generated key");
                File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                try { LagrangeTokenRelayFiles.LoadOrCreateKey(keyFile); throw new Exception("public key permissions accepted"); }
                catch (IOException) { }
                File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.WriteAllText(keyFile, "bad");
            try { LagrangeTokenRelayFiles.LoadOrCreateKey(keyFile); throw new Exception("malformed existing key accepted"); }
            catch (InvalidDataException) { }
            File.WriteAllText(keyFile, key);
            var identity = new LagrangeTokenRelay.Identity(true, 123456789, Guid);
            var first = new LagrangeTokenRelay(0, key, () => identity,
                (_, _, _) => Task.FromResult((0, Array.Empty<byte>())), CancellationToken.None);
            try
            {
                await using var second = new LagrangeTokenRelay(0, key, () => identity,
                    (_, _, _) => Task.FromResult((0, Array.Empty<byte>())), CancellationToken.None);
                Check(first.Port > 0 && second.Port > 0 && first.Port != second.Port, "multiple automatic listeners");
                first.PublishDiscovery(info, keyFile);
                var text = File.ReadAllText(info);
                var data = JsonDocument.Parse(text).RootElement;
                Check(data.GetProperty("endpoint").GetString() == $"http://127.0.0.1:{first.Port}/debug/shiroSignToken" &&
                    data.GetProperty("keyFile").GetString() == keyFile && !text.Contains(key), "discovery contains endpoint without credential");
                if (!OperatingSystem.IsWindows())
                    Check(File.GetUnixFileMode(info) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "private discovery");
            }
            finally { await first.DisposeAsync(); }
            Check(!File.Exists(info) && File.Exists(keyFile), "unload removes discovery and preserves key");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    internal static async Task Run()
    {
        var config = new LagrangeAdapterConfig { Protocol = "MacOs" };
        config.Validate();
        Check(config.TokenRelayEnabled && config.TokenRelayPort == 0 && config.TokenRelayKeyFile == "", "default enabled with automatic port and key");
        config.TokenRelayEnabled = false; config.Validate();
        config.Protocol = "AndroidPad"; config.SignServer = "http://127.0.0.1:1";
        config.Validate(); // Unsupported protocol needs no relay key.
        await AutomaticSetup();
        var identity = new LagrangeTokenRelay.Identity(true, 123456789, Guid);
        var calls = 0;
        Func<string, byte[], CancellationToken, Task<(int, byte[])>> send = (command, bytes, _) => {
            Interlocked.Increment(ref calls);
            Check(command is Establish or Secure, "command whitelist");
            Check(bytes.SequenceEqual(new byte[] { 1, 2 }), "request hex decoded");
            return Task.FromResult((0, new byte[] { 3, 4 }));
        };
        var port = Port();
        var relay = new LagrangeTokenRelay(port, Key, () => identity,
            (command, body, ct) => send(command, body, ct), CancellationToken.None);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
        async Task<HttpResponseMessage> Request(string? command = Establish, string body = "0102", string uin = "123456789",
            string guid = Guid, string? key = Key, string? origin = null, string path = "/debug/shiroSignToken",
            string method = "POST", string? raw = null, string media = "application/json")
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), path);
            req.Content = new StringContent(raw ?? JsonSerializer.Serialize(new { command, body, uin, guid }), Encoding.UTF8, media);
            if (key is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (origin is not null) req.Headers.Add("Origin", origin);
            return await client.SendAsync(req);
        }
        async Task Status(int expected, Task<HttpResponseMessage> pending)
        { using var response = await pending; Check((int)response.StatusCode == expected, $"expected {expected}, got {response.StatusCode}"); }
        try
        {
            await Status(404, Request(path: "/other"));
            await Status(405, Request(method: "GET"));
            await Status(401, Request(key: null));
            await Status(401, Request(key: "wrong"));
            await Status(403, Request(origin: "https://example.com"));
            await Status(400, Request(command: "arbitrary.command"));
            await Status(400, Request(body: "xx"));
            await Status(400, Request(body: "0"));
            await Status(400, Request(body: ""));
            await Status(400, Request(raw: "{"));
            await Status(400, Request(raw: new string('x', 40001)));
            await Status(400, Request(media: "text/plain"));
            await Status(400, Request(guid: "wrong"));
            await Status(409, Request(uin: "987654321"));
            await Status(409, Request(guid: new string('a', 32)));
            identity = identity with { Online = false };
            await Status(503, Request());
            Check(calls == 0, "rejected requests never send SSO");
            identity = identity with { Online = true };
            foreach (var command in new[] { Establish, Secure })
            {
                using var response = await Request(command, guid: Guid.ToUpperInvariant());
                Check(response.IsSuccessStatusCode, "fixed command succeeds");
                var reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
                Check(reply.GetProperty("retcode").GetInt32() == 0 && reply.GetProperty("body").GetString() == "0304" &&
                    reply.GetProperty("guid").GetString() == Guid && reply.GetProperty("uin").GetString() == "123456789", "wire reply");
                Check(response.Headers.CacheControl?.NoStore == true, "no-store response");
            }
            send = (_, _, _) => { identity = identity with { Online = false }; return Task.FromResult((0, new byte[] { 1 })); };
            await Status(409, Request()); // No stale response after reconnect/account change.
            identity = identity with { Online = true };
            send = (_, _, _) => Task.FromResult((0, new byte[32769]));
            await Status(502, Request());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            send = async (_, _, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return (0, Array.Empty<byte>()); };
            var pending = Request();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Status(503, Request()); // One in-flight request per adapter.
            await relay.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            relay = null!;
            try { using var response = await pending; Check(!response.IsSuccessStatusCode, "unload must not return success"); }
            catch (HttpRequestException) { }
            // Same port can be reused immediately after unload.
            await using var replacement = new LagrangeTokenRelay(port, Key, () => identity,
                (_, _, _) => Task.FromResult((7, new byte[] { 8 })), CancellationToken.None);
            using var replaced = await Request();
            Check(replaced.IsSuccessStatusCode && JsonDocument.Parse(await replaced.Content.ReadAsStringAsync()).RootElement.GetProperty("retcode").GetInt32() == 7,
                "port released and nonzero retcode preserved");
        }
        finally { if (relay is not null) await relay.DisposeAsync(); }
        Console.WriteLine("Token relay loopback authentication, whitelist, identity, bounds, busy and unload checks passed.");
    }
}
