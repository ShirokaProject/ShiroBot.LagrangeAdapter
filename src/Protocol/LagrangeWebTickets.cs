using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Lagrange.Core;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Interface;

namespace ShiroBot.LagrangeAdapter;

internal sealed class LagrangeWebTickets(BotContext context)
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

    internal async Task<string> GetPSKeyAsync(string domain, CancellationToken token)
    {
        await _refresh.WaitAsync(token);
        try
        {
            if (_pskeys.TryGetValue(domain, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
                return cached.Key;

            var keys = await context.FetchCookies(domain).WaitAsync(token);
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

            var (clientKey, _) = await context.FetchClientKey().WaitAsync(token);
            if (string.IsNullOrWhiteSpace(clientKey))
                throw new InvalidOperationException("QQ 未返回用于获取 SKey 的 clientKey。");

            using var query = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["ptlang"] = "1033",
                ["clientuin"] = context.BotUin.ToString(CultureInfo.InvariantCulture),
                ["clientkey"] = clientKey,
                ["u1"] = "https://h5.qzone.qq.com/qqnt/qzoneinpcqq/friend?refresh=0&clientuin=0&darkMode=0",
                ["keyindex"] = "19",
                ["random"] = "2599"
            });
            var uri = new UriBuilder(LoginEndpoint) { Query = await query.ReadAsStringAsync(token) }.Uri;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
                    var cookies = new CookieContainer();
                    if (response.Headers.TryGetValues("Set-Cookie", out var headers))
                    {
                        foreach (var header in headers)
                        {
                            try { cookies.SetCookies(uri, header); }
                            catch (CookieException) { }
                        }
                    }

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
