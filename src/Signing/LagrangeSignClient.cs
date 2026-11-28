using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace ShiroBot.LagrangeAdapter;

internal sealed class LagrangeSignClient : IDisposable
{
    private readonly HttpClient _client;
    private readonly Uri _base;
    private readonly LagrangeAdapterConfig _options;
    private readonly CancellationToken _stopping;

    internal LagrangeSignClient(LagrangeAdapterConfig options, CancellationToken stopping,
        HttpMessageHandler? handler = null)
    {
        options.Validate();
        if (string.IsNullOrWhiteSpace(options.SignServer)) throw new ArgumentException("SignServer 不能为空。");
        _options = options;
        _stopping = stopping;
        _base = new Uri(options.SignServer.TrimEnd('/') + "/");
        handler ??= new HttpClientHandler
        {
            AllowAutoRedirect = false,
            Proxy = string.IsNullOrWhiteSpace(options.SignProxyUrl) ? null : new WebProxy(options.SignProxyUrl),
            UseProxy = true
        };
        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(options.SignTimeoutSeconds),
            MaxResponseContentBufferSize = 1024 * 1024
        };
        if (!string.IsNullOrWhiteSpace(options.SignToken))
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.SignToken);
    }

    internal Task<JsonObject> RequestAsync(string endpoint, object? payload = null) =>
        RequestWithCancellationAsync(endpoint, payload, CancellationToken.None);

    internal async Task<JsonObject> RequestWithCancellationAsync(string endpoint, object? payload, CancellationToken cancellationToken)
    {
        using var linked = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(_stopping, cancellationToken)
            : null;
        var token = linked?.Token ?? _stopping;
        try
        {
            using var request = new HttpRequestMessage(payload is null ? HttpMethod.Get : HttpMethod.Post,
                new Uri(_base, endpoint));
            if (payload is not null) request.Content = JsonContent.Create(payload);
            using var response = await _client.SendAsync(request, token);
            var body = await response.Content.ReadAsStringAsync(token);
            JsonObject? root;
            try { root = JsonNode.Parse(body) as JsonObject; }
            catch (System.Text.Json.JsonException)
            {
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"签名服务 HTTP {(int)response.StatusCode}: 非 JSON 响应。", null, response.StatusCode);
                throw new InvalidDataException("签名服务返回非 JSON 响应。");
            }

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"签名服务 HTTP {(int)response.StatusCode}: {Detail(root)}", null, response.StatusCode);
            if (root is null) throw new InvalidDataException("签名服务返回空响应或非对象响应。");
            if (root["code"] is { } code && (code is not JsonValue value || !value.TryGetValue<int>(out var n) || n != 0))
                throw new InvalidDataException($"签名服务失败: {Detail(root)}");
            return root;
        }
        catch (HttpRequestException ex)
        {
            // Network exceptions can contain the full URL, including a path Token.
            throw new HttpRequestException(ex.HttpRequestError, LagrangeSecrets.Redact(ex.Message, _options),
                inner: null, ex.StatusCode);
        }
    }

    private string Detail(JsonObject? root)
    {
        var node = root?["msg"] ?? root?["message"] ?? root?["error"] ?? root?["detail"];
        var text = node is JsonValue value && value.TryGetValue<string>(out var message)
            ? message : "服务未提供错误说明";
        text = LagrangeSecrets.Redact(text, _options);
        return text.Length > 1024 ? text[..1024] : text;
    }

    internal static byte[] Hex(JsonNode? node, string field, bool allowEmpty = true)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text) ||
            (!allowEmpty && text.Length == 0))
            throw new InvalidDataException($"签名服务响应缺少字符串字段 {field}。");
        try { return Convert.FromHexString(text); }
        catch (FormatException) { throw new InvalidDataException($"签名服务字段 {field} 不是有效 HEX。"); }
    }

    public void Dispose() => _client.Dispose();
}

internal static class LagrangeSecrets
{
    internal static string Redact(string text, LagrangeAdapterConfig config)
    {
        List<string?> secrets = [config.Password, config.SignToken, config.SignServer];
        if (Uri.TryCreate(config.SignServer, UriKind.Absolute, out var uri))
            secrets.AddRange(uri.Segments.Select(Uri.UnescapeDataString).Select(x => x.Trim('/'))
                .Where(x => x.StartsWith("ss_", StringComparison.Ordinal)));
        foreach (var secret in secrets.Where(x => !string.IsNullOrEmpty(x)).OrderByDescending(x => x!.Length))
        {
            text = text.Replace(secret!, "[已隐藏]", StringComparison.Ordinal);
            text = text.Replace(Uri.EscapeDataString(secret!), "[已隐藏]", StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }
}
