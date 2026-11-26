using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Lagrange.Core;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Exceptions;

namespace ShiroBot.LagrangeAdapter;

internal sealed class LagrangeWebTickets(BotContext context, HttpClient? loginClient = null,
    Func<CancellationToken, Task<string>>? fetchClientKey = null,
    Func<string, CancellationToken, Task<Dictionary<string, string>>>? fetchDomainKeys = null)
{
    private static readonly ConditionalWeakTable<BotContext, LagrangeWebTickets> Sessions = new();
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Uri LoginEndpoint = new("https://ssl.ptlogin2.qq.com/jump");

    private readonly SemaphoreSlim _refresh = new(1);
    private (string Key, DateTimeOffset Expires) _skey;
    private readonly Dictionary<string, (string Key, DateTimeOffset Expires)> _pskeys = new(StringComparer.Ordinal);

    internal static LagrangeWebTickets For(BotContext context) =>
        Sessions.GetValue(context, static bot => new LagrangeWebTickets(bot));

    internal async Task<string> GetCookiesAsync(string domain, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (domain.Length is 0 or > 253 || Uri.CheckHostName(domain) != UriHostNameType.Dns ||
            domain.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')))
            throw new ArgumentException("Cookie 域名无效。", nameof(domain));
        domain = domain.TrimEnd('.').ToLowerInvariant();
        if (IsDomainOrSubdomain(domain, "pay.qq.com"))
            throw new ArgumentException("该域名禁止获取 Cookie。", nameof(domain));

        var (skey, pskey) = await TryGetWebKeysAsync(domain, token);
        token.ThrowIfCancellationRequested();
        pskey ??= await GetPSKeyAsync(domain, token);
        skey ??= await GetSKeyAsync(token);
        var uin = context.BotUin.ToString(CultureInfo.InvariantCulture);
        return $"uin=o{uin}; p_uin=o{uin}; skey={skey}; p_skey={pskey}";
    }

    private async Task<(string? SKey, string? PSKey)> TryGetWebKeysAsync(string domain, CancellationToken token)
    {
        if (!IsDomainOrSubdomain(domain, "qq.com")) return default;

        var uin = context.BotUin.ToString(CultureInfo.InvariantCulture);
        var target = new UriBuilder(Uri.UriSchemeHttps, domain) { Path = $"/{uin}/infocenter" }.Uri;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        string? skey = null;
        string? pskey = null;
        try
        {
            var uri = await CreateLoginUriAsync(target, timeout.Token);
            var cookies = new CookieContainer();
            for (var hop = 0; hop <= 4; hop++)
            {
                using var response = await (loginClient ?? Client).GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                ReadSetCookies(cookies, uri, response);
                var keys = cookies.GetCookies(target);
                skey = keys["skey"] is { Value.Length: > 0 } session ? session.Value : null;
                pskey = keys.Cast<Cookie>().FirstOrDefault(cookie => cookie.Name == "p_skey" && cookie.Value.Length > 0 &&
                    string.Equals(cookie.Domain.TrimStart('.'), domain, StringComparison.OrdinalIgnoreCase))?.Value;
                if (skey is not null && pskey is not null) return (skey, pskey);

                if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) ||
                    !Uri.TryCreate(uri, response.Headers.Location, out var redirect) ||
                    redirect.Scheme != Uri.UriSchemeHttps || !redirect.IsDefaultPort || redirect.UserInfo.Length != 0 ||
                    !IsDomainOrSubdomain(redirect.Host, "qq.com") || IsDomainOrSubdomain(redirect.Host, "pay.qq.com") ||
                    hop == 4)
                    return (skey, pskey);
                uri = redirect;
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested &&
            ex is HttpRequestException or OperationCanceledException or LagrangeException or InvalidOperationException)
        {
            // The existing QQ ticket API is still available if the web login cannot issue a cookie.
        }

        token.ThrowIfCancellationRequested();
        return (skey, pskey);
    }

    private static bool IsDomainOrSubdomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    private async Task<Uri> CreateLoginUriAsync(Uri target, CancellationToken token)
    {
        var clientKey = fetchClientKey is null ? (await context.FetchClientKey().WaitAsync(token)).Item1 : await fetchClientKey(token);
        if (string.IsNullOrWhiteSpace(clientKey))
            throw new InvalidOperationException("QQ 未返回用于获取 SKey 的 clientKey。");

        using var query = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ptlang"] = "1033",
            ["clientuin"] = context.BotUin.ToString(CultureInfo.InvariantCulture),
            ["clientkey"] = clientKey,
            ["u1"] = target.AbsoluteUri,
            ["keyindex"] = "19",
            ["random"] = "2599"
        });
        return new UriBuilder(LoginEndpoint) { Query = await query.ReadAsStringAsync(token) }.Uri;
    }

    private static void ReadSetCookies(CookieContainer cookies, Uri uri, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers)) return;
        foreach (var header in headers)
        {
            try { cookies.SetCookies(uri, header); }
            catch (CookieException) { }
        }
    }

    internal async Task<string> GetPSKeyAsync(string domain, CancellationToken token)
    {
        await _refresh.WaitAsync(token);
        try
        {
            if (_pskeys.TryGetValue(domain, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
                return cached.Key;

            var keys = fetchDomainKeys is null ? await context.FetchCookies(domain).WaitAsync(token) : await fetchDomainKeys(domain, token);
            if (!keys.TryGetValue(domain, out var key) || string.IsNullOrEmpty(key))
                throw new InvalidOperationException("QQ 未返回该域名的 p_skey。");
            if (_pskeys.Count >= 256) _pskeys.Clear();
            _pskeys[domain] = (key, DateTimeOffset.UtcNow.AddDays(1));
            return key;
        }
        finally
        {
            _refresh.Release();
        }
    }

    internal async Task<string> GetSKeyAsync(CancellationToken token)
    {
        await _refresh.WaitAsync(token);
        try
        {
            if (_skey.Expires > DateTimeOffset.UtcNow) return _skey.Key;
            var saved = context.Keystore.WLoginSigs.SKey;
            if (context.Config.Protocol is Protocols.AndroidPhone or Protocols.AndroidPad && saved is { Length: > 0 })
                return Encoding.UTF8.GetString(saved);

            var uri = await CreateLoginUriAsync(new Uri("https://h5.qzone.qq.com/qqnt/qzoneinpcqq/friend?refresh=0&clientuin=0&darkMode=0"), token);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var response = await (loginClient ?? Client).GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
                    var cookies = new CookieContainer();
                    ReadSetCookies(cookies, uri, response);

                    var cookie = cookies.GetCookies(uri)["skey"];
                    if (cookie is { Value.Length: > 0 })
                    {
                        var expires = DateTimeOffset.UtcNow.AddDays(1);
                        if (cookie.Expires != DateTime.MinValue)
                            expires = new DateTimeOffset(cookie.Expires.ToUniversalTime()) < expires
                                ? new DateTimeOffset(cookie.Expires.ToUniversalTime()) : expires;
                        _skey = (cookie.Value, expires);
                        return cookie.Value;
                    }

                    var status = (int)response.StatusCode;
                    if (attempt == 3 || (status < 500 && status is not (408 or 429)))
                        throw new InvalidOperationException($"QQ 登录跳转未返回 SKey（HTTP {status}）。");
                }
                catch (HttpRequestException) when (attempt < 3 && !token.IsCancellationRequested) { }
                catch (OperationCanceledException) when (attempt < 3 && !token.IsCancellationRequested) { }

                await Task.Delay(TimeSpan.FromMilliseconds(attempt * 350), token);
            }
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("访问 QQ 登录跳转接口失败，未能刷新 SKey。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("QQ 登录跳转超时，未能刷新 SKey。");
        }
        finally
        {
            _refresh.Release();
        }
    }
}
