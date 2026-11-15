using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ShiroBot.LagrangeAdapter;

/// <summary>Outbound-only SSE tasks + HTTPS replies, authenticated with the existing sign token.</summary>
internal sealed class LagrangeTokenRelay : IAsyncDisposable
{
    internal record Identity(bool Online, long Uin, string Guid);
    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly string _qua;
    private readonly Func<Identity> _identity;
    private readonly Func<string, byte[], CancellationToken, Task<(int Code, byte[] Body)>> _send;
    private readonly CancellationTokenSource _stopping;
    private readonly Task _loop;
    private readonly HashSet<string> _seen = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal LagrangeTokenRelay(LagrangeAdapterConfig config, string qua, Func<Identity> identity,
        Func<string, byte[], CancellationToken, Task<(int, byte[])>> send, CancellationToken stopping,
        HttpMessageHandler? handler = null)
    {
        _base = new Uri(config.SignServer!.TrimEnd('/') + "/");
        if (_base.Scheme != "https" && !(_base.Scheme == "http" && _base.IsLoopback))
            throw new ArgumentException("Relay requires HTTPS; HTTP is allowed only for loopback tests.");
        _qua = qua; _identity = identity; _send = send;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _http = new HttpClient(handler ?? new HttpClientHandler {
            AllowAutoRedirect = false, UseProxy = true,
            Proxy = string.IsNullOrWhiteSpace(config.SignProxyUrl) ? null : new WebProxy(config.SignProxyUrl)
        }) { Timeout = Timeout.InfiniteTimeSpan };
        if (!string.IsNullOrWhiteSpace(config.SignToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.SignToken);
        _loop = Run();
    }
    private async Task<JsonElement> Post(string endpoint, object payload, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await _http.PostAsJsonAsync(new Uri(_base, endpoint), payload, Json, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        // Bound even a misconfigured signing endpoint's reply.
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (memory.Length + read > 4096) throw new InvalidDataException("Relay response too large.");
            memory.Write(buffer, 0, read);
        }
        return JsonSerializer.Deserialize<JsonElement>(memory.ToArray(), Json);
    }
    private async Task Run()
    {
        string? client = null;
        Identity? registered = null;
        var failures = 0;
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var identity = _identity();
                if (!identity.Online || identity.Uin <= 0 || identity.Guid.Length != 32)
                { client = null; await Task.Delay(1000, _stopping.Token); continue; }
                try
                {
                    if (client is null || registered != identity)
                    {
                        var result = await Post("relay/register", new {
                            uin = identity.Uin.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            guid = identity.Guid, qua = _qua,
                            // Verified local facts only; unknown hardware/detector fields are omitted.
                            features = new[] {
                                new { id = 1, value = identity.Guid },
                                new { id = 4, value = RuntimeInformation.OSArchitecture.ToString() },
                                new { id = 6, value = RuntimeInformation.OSDescription }
                            }
                        }, _stopping.Token);
                        client = result.GetProperty("client").GetString();
                        if (client is null || client.Length != 64 || client.Any(c => !Uri.IsHexDigit(c)))
                            throw new InvalidDataException("Invalid relay lease.");
                        registered = identity;
                    }
                    using var session = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                    session.CancelAfter(TimeSpan.FromSeconds(40));
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, "relay/events?client=" + client));
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, session.Token);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                        throw new InvalidDataException("Signing endpoint does not support relay SSE.");
                    await using var stream = await response.Content.ReadAsStreamAsync(session.Token);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var data = new StringBuilder();
                    var type = "";
                    while (await reader.ReadLineAsync(session.Token) is { } line)
                    {
                        if (_identity() != identity) break;
                        if (line.Length > 40000 || data.Length + line.Length > 40000)
                            throw new InvalidDataException("Relay event too large.");
                        if (line.Length == 0)
                        {
                            if (type == "task" && data.Length > 0) await Handle(data.ToString(), client, identity, session.Token);
                            data.Clear(); type = "";
                        }
                        else if (line.StartsWith("event:", StringComparison.Ordinal)) type = line[6..].Trim();
                        else if (line.StartsWith("data:", StringComparison.Ordinal)) data.Append(line[5..].TrimStart());
                    }
                    failures = 0;
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    // Includes expected 404/422 on older/unsupported sign servers. Never log credential URLs.
                    client = null;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 << Math.Min(failures++, 4))), _stopping.Token);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }
    private async Task Handle(string data, string client, Identity identity, CancellationToken cancellation)
    {
        using var document = JsonDocument.Parse(data);
        var packet = document.RootElement;
        var id = packet.GetProperty("id").GetString()!;
        var command = packet.GetProperty("command").GetString()!;
        var body = packet.GetProperty("body").GetString()!;
        var uin = packet.GetProperty("uin").GetString()!;
        var guid = packet.GetProperty("guid").GetString()!;
        if (id.Length != 32 || id.Any(c => !Uri.IsHexDigit(c)) ||
            command is not ("trpc.o3.ecdh_access.EcdhAccess.SsoEstablishShareKey" or "trpc.o3.ecdh_access.EcdhAccess.SsoSecureAccess") ||
            uin != identity.Uin.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
            !guid.Equals(identity.Guid, StringComparison.OrdinalIgnoreCase) ||
            body.Length is 0 or > 32768 || body.Length % 2 != 0 || body.Any(c => !Uri.IsHexDigit(c)) || _identity() != identity)
            throw new InvalidDataException("Relay task scope invalid.");
        if (!_seen.Add(id)) return;
        if (_seen.Count > 128) _seen.Remove(_seen.First());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        int retcode; byte[] bytes;
        try { (retcode, bytes) = await _send(command, Convert.FromHexString(body), timeout.Token); }
        catch (Exception) when (!cancellation.IsCancellationRequested) { retcode = -1; bytes = []; }
        if (_identity() != identity) return;
        if (bytes.Length > 32768) { retcode = -1; bytes = []; }
        await Post("relay/result", new { client, id, retcode, body = Convert.ToHexString(bytes).ToLowerInvariant(), uin, guid = identity.Guid }, cancellation);
    }
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _http.Dispose();
        await _loop;
        _stopping.Dispose();
    }
}
