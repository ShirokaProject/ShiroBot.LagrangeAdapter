using System.Net;
using Lagrange.Core;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Exceptions;
using ShiroBot.LagrangeAdapter;

namespace ContractChecks;

internal static class WebTicketChecks
{
    public static async Task Run()
    {
        using var bot = BotFactory.Create(new BotConfig());
        await CheckWebCookiesAsync(bot);
        await CheckFallbackAsync(bot);
        await CheckRejectedDomainAsync(bot);
        Console.WriteLine("QQ web cookies, domain scoping and OIDB fallback checks passed (offline).");
    }

    private static async Task CheckWebCookiesAsync(BotContext bot)
    {
        var hops = 0;
        using var client = new HttpClient(new ScriptedHandler(uri =>
        {
            if (hops++ == 0)
            {
                if (uri.Host != "ssl.ptlogin2.qq.com" || uri.AbsolutePath != "/jump" ||
                    !Uri.UnescapeDataString(uri.Query).Contains("u1=https://qun.qq.com/0/infocenter", StringComparison.Ordinal))
                    throw new Exception("The login request did not target the requested QQ domain.");
                return Reply(HttpStatusCode.Found, "https://qun.qq.com/0/infocenter", "skey=web-session; Domain=.qq.com; Path=/; Secure",
                    "p_skey=unscoped; Domain=.qq.com; Path=/; Secure");
            }
            if (uri.Host != "qun.qq.com") throw new Exception("Unexpected login redirect.");
            return Reply(HttpStatusCode.OK, null, "p_skey=web-domain; Path=/; Secure");
        }));
        var tickets = new LagrangeWebTickets(bot, client, _ => Task.FromResult("test-client-key"),
            (_, _) => throw new Exception("OIDB fallback must not run after successful web login."));

        var cookies = await tickets.GetCookiesAsync("QUN.QQ.COM", default);
        if (hops != 2 || cookies != "uin=o0; p_uin=o0; skey=web-session; p_skey=web-domain")
            throw new Exception("The website cookies were not returned together.");
    }

    private static async Task CheckFallbackAsync(BotContext bot)
    {
        var hops = 0;
        var fallbackCalls = 0;
        using var client = new HttpClient(new ScriptedHandler(uri =>
        {
            if (hops++ != 0 || uri.Host != "ssl.ptlogin2.qq.com")
                throw new Exception("The login flow followed a redirect outside QQ.");
            return Reply(HttpStatusCode.Found, "https://outside.example.test/", "skey=web-session; Domain=.qq.com; Path=/; Secure",
                "p_skey=unscoped; Domain=.qq.com; Path=/; Secure", "p_skey=sibling; Domain=mail.qq.com; Path=/; Secure");
        }));
        var tickets = new LagrangeWebTickets(bot, client, _ => Task.FromResult("test-client-key"), (domain, _) =>
        {
            fallbackCalls++;
            return Task.FromResult(new Dictionary<string, string> { [domain] = "oidb-domain" });
        });

        var cookies = await tickets.GetCookiesAsync("qun.qq.com", default);
        if (fallbackCalls != 1 || cookies != "uin=o0; p_uin=o0; skey=web-session; p_skey=oidb-domain")
            throw new Exception("An unrelated web cookie replaced the OIDB domain ticket.");
    }

    private static async Task CheckRejectedDomainAsync(BotContext bot)
    {
        using var client = new HttpClient(new ScriptedHandler(_ => Reply(HttpStatusCode.Forbidden, null)));
        var tickets = new LagrangeWebTickets(bot, client, _ => Task.FromResult("test-client-key"),
            (_, _) => Task.FromException<Dictionary<string, string>>(new OperationException(1003, "domain no permission")));
        try
        {
            await tickets.GetCookiesAsync("v.qq.com", default);
            throw new Exception("Expected to preserve QQ's refusal to issue a domain ticket.");
        }
        catch (OperationException ex) when (ex.Result == 1003) { }

        try
        {
            await tickets.GetCookiesAsync("PAY.QQ.COM.", default);
            throw new Exception("The payment domain should be rejected before requesting any tickets.");
        }
        catch (ArgumentException ex) when (ex.ParamName == "domain") { }
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string? location, params string[] cookies)
    {
        var response = new HttpResponseMessage(status);
        if (location is not null) response.Headers.Location = new Uri(location);
        if (cookies.Length > 0) response.Headers.TryAddWithoutValidation("Set-Cookie", cookies);
        return response;
    }

    private sealed class ScriptedHandler(Func<Uri, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request.RequestUri!));
    }
}
