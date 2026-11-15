using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShiroBot.LagrangeAdapter;

/// <summary>Optional authenticated loopback transport, never an arbitrary SSO proxy.</summary>
internal sealed class LagrangeTokenRelay : IAsyncDisposable
{
    internal record Identity(bool Online, long Uin, string Guid);
    internal record Packet(string Command, string Body, string Uin, string Guid);
    internal record Reply(int Retcode, string Body, string Uin, string Guid);
    private readonly HttpListener _listener;
    internal int Port { get; }
    private string? _discoveryPath;
    private readonly CancellationTokenSource _stopping;
    private readonly Func<Identity> _identity;
    private readonly Func<string, byte[], CancellationToken, Task<(int Code, byte[] Body)>> _send;
    private readonly byte[] _keyHash;
    private readonly SemaphoreSlim _single = new(1);
    private readonly Task _loop;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, Task> _requests = new();
    private long _nextId;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal LagrangeTokenRelay(int port, string key, Func<Identity> identity,
        Func<string, byte[], CancellationToken, Task<(int, byte[])>> send, CancellationToken stopping)
    {
        if (port is < 0 or > 65535 || key.Length is < 32 or > 256 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Token relay requires a port and a 32–256 character URL-safe key.");
        _identity = identity; _send = send;
        _keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        try { (_listener, Port) = Listen(port); }
        catch { _stopping.Dispose(); _single.Dispose(); throw; }
        _loop = Run();
    }
    private static (HttpListener Listener, int Port) Listen(int requested)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var port = requested;
            if (port == 0)
            {
                using var reservation = new TcpListener(IPAddress.Loopback, 0);
                reservation.Start();
                port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            }
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); return (listener, port); }
            catch (HttpListenerException) when (requested == 0 && attempt < 9) { listener.Close(); }
            catch { listener.Close(); throw; }
        }
        throw new IOException("Unable to allocate a loopback Token relay port.");
    }
    internal void PublishDiscovery(string path, string keyFile)
    {
        LagrangeTokenRelayFiles.WriteDiscovery(path, Port, keyFile);
        _discoveryPath = path;
    }
    private async Task Run()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_stopping.Token);
                var id = Interlocked.Increment(ref _nextId);
                var work = Handle(context);
                _requests[id] = work;
                _ = work.ContinueWith(_ => _requests.TryRemove(id, out var ignored), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (HttpListenerException) when (_stopping.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stopping.IsCancellationRequested) { }
    }
    private async Task Handle(HttpListenerContext context)
    {
        context.Response.StatusCode = 503; // Closing during unload must never emit an empty success.
        var acquired = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var request = context.Request;
            if (request.Url?.AbsolutePath != "/debug/shiroSignToken") { context.Response.StatusCode = 404; return; }
            if (request.HttpMethod != "POST") { context.Response.StatusCode = 405; return; }
            if (request.RemoteEndPoint is null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address) || request.Headers["Origin"] is not null)
            { context.Response.StatusCode = 403; return; }
            var auth = request.Headers["Authorization"];
            if (auth is null || auth.Length > 270 || !auth.StartsWith("Bearer ", StringComparison.Ordinal) ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(auth[7..])), _keyHash))
            { context.Response.StatusCode = 401; return; }
            if (request.ContentLength64 is < 1 or > 40000 || request.ContentType?.Split(';')[0].Trim() != "application/json")
            { context.Response.StatusCode = 400; return; }
            acquired = await _single.WaitAsync(0, timeout.Token);
            if (!acquired) { context.Response.StatusCode = 503; return; }
            var raw = new byte[(int)request.ContentLength64];
            await request.InputStream.ReadExactlyAsync(raw, timeout.Token);
            var packet = System.Text.Json.JsonSerializer.Deserialize<Packet>(raw, Json);
            if (packet is null || packet.Command is not ("trpc.o3.ecdh_access.EcdhAccess.SsoEstablishShareKey" or "trpc.o3.ecdh_access.EcdhAccess.SsoSecureAccess") ||
                packet.Body is null || packet.Body.Length is 0 or > 32768 || packet.Body.Length % 2 != 0 ||
                packet.Guid is null || packet.Guid.Length != 32 || packet.Guid.Any(c => !Uri.IsHexDigit(c)))
            { context.Response.StatusCode = 400; return; }
            var identity = _identity();
            if (!identity.Online || identity.Uin <= 0) { context.Response.StatusCode = 503; return; }
            if (packet.Uin != identity.Uin.ToString(System.Globalization.CultureInfo.InvariantCulture) || !packet.Guid.Equals(identity.Guid, StringComparison.OrdinalIgnoreCase))
            { context.Response.StatusCode = 409; return; }
            byte[] bytes;
            try { bytes = Convert.FromHexString(packet.Body); }
            catch (FormatException) { context.Response.StatusCode = 400; return; }
            var result = await _send(packet.Command, bytes, timeout.Token);
            if (_identity() != identity) { context.Response.StatusCode = 409; return; }
            if (result.Body.Length > 32768) { context.Response.StatusCode = 502; return; }
            var output = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Reply(result.Code,
                Convert.ToHexString(result.Body).ToLowerInvariant(), packet.Uin, identity.Guid), Json);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = output.Length;
            context.Response.Headers["Cache-Control"] = "no-store";
            await context.Response.OutputStream.WriteAsync(output, timeout.Token);
        }
        catch (JsonException) { SetError(context.Response, 400); }
        catch (OperationCanceledException) { SetError(context.Response, 504); }
        catch (Exception) { SetError(context.Response, 502); }
        finally
        {
            if (acquired) _single.Release();
            try { context.Response.Close(); } catch (Exception) { /* Client may have disconnected. */ }
        }
    }
    private static void SetError(HttpListenerResponse response, int code)
    {
        try { response.StatusCode = code; } catch (Exception) { /* Already sent or closed. */ }
    }
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Close();
        await _loop;
        await Task.WhenAll(_requests.Values);
        _single.Dispose(); _stopping.Dispose();
        if (_discoveryPath is { } path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
