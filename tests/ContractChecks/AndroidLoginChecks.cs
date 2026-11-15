using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using Lagrange.Core;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Interface;
using ShiroBot.LagrangeAdapter;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Dashboard;
using ShiroBot.SDK.Plugin;

namespace ContractChecks;

internal static class AndroidLoginChecks
{
    internal static async Task Run(Assembly assembly)
    {
        AndroidAppInfoChecks.Run(assembly);
        var type = assembly.GetType("ShiroBot.LagrangeAdapter.LagrangeAndroidSignProvider")!;
        foreach (var protocol in new[] { Protocols.AndroidPhone, Protocols.AndroidPad })
        foreach (var pathToken in new[] { false, true })
        {
            var path = "/android/9.3.80-16750" + (pathToken ? "/ss_synthetic_path_token" : "");
            var handler = new AndroidHandler(path, pathToken);
            using var signer = (IDisposable)Activator.CreateInstance(type,
                new LagrangeAdapterConfig { Protocol = protocol.ToString(), SignServer = "http://localhost" + path,
                    SignToken = pathToken ? "" : "synthetic-bearer", Uin = "123" }, CancellationToken.None, handler)!;
            var provider = (AndroidBotSignProvider)signer;
            using var bot = BotFactory.Create(new BotConfig { Protocol = protocol, SignProvider = provider }, Info());
            if (!provider.IsWhiteListCommand("wtlogin.login") || !provider.IsWhiteListCommand("GDCTrpcProxy.down") ||
                provider.IsWhiteListCommand("not-a-sign-command")) throw new Exception("Android command policy mismatch.");
            var sign = await provider.GetSecSign(456, "wtlogin.login", -1, new byte[] { 0xab });
            if (!sign!.SecSign!.SequenceEqual(new byte[] { 1 }) || !sign.SecToken!.SequenceEqual(new byte[] { 2 }))
                throw new Exception("Android signature decoding mismatch.");
            if (!(await provider.GetEnergy(456, "810_2")).SequenceEqual(new byte[] { 4 }) ||
                !(await provider.GetDebugXwid(0, "810_8")).SequenceEqual(new byte[] { 5 }) || handler.Calls != 3)
                throw new Exception("Android energy/TLV forwarding mismatch.");
        }

        foreach (var reply in new[] { "{\"code\":1,\"msg\":\"rejected\",\"data\":{}}", "{\"code\":0,\"data\":{\"sign\":\"xx\",\"token\":\"\",\"extra\":\"\"}}", "{\"code\":0}", "not-json" })
        {
            using var signer = (IDisposable)Activator.CreateInstance(type,
                new LagrangeAdapterConfig { Protocol = "AndroidPad", SignServer = "http://localhost" },
                CancellationToken.None, new ReplyHandler(reply))!;
            var provider = (AndroidBotSignProvider)signer;
            using var bot = BotFactory.Create(new BotConfig { Protocol = Protocols.AndroidPad, SignProvider = provider }, Info());
            try { await provider.GetSecSign(456, "wtlogin.login", 1, new byte[] { 0 }); throw new Exception("Invalid Android reply accepted."); }
            catch (InvalidDataException) { }
        }
        using (var signer = (IDisposable)Activator.CreateInstance(type,
                   new LagrangeAdapterConfig { SignServer = "http://localhost/android/9.3.80-16750/ss_secret_path", Password = "secret-password" },
                   CancellationToken.None, new ReplyHandler("{\"msg\":\"ss_secret_path secret-password\"}", HttpStatusCode.Forbidden))!)
        {
            var provider = (AndroidBotSignProvider)signer;
            using var bot = BotFactory.Create(new BotConfig { Protocol = Protocols.AndroidPad, SignProvider = provider }, Info());
            try { await provider.GetSecSign(456, "wtlogin.login", 1, new byte[] { 0 }); throw new Exception("Rejection accepted."); }
            catch (HttpRequestException ex)
            {
                if (!ex.Message.Contains("HTTP 403") || ex.Message.Contains("ss_secret_path") || ex.Message.Contains("secret-password"))
                    throw new Exception("URL credential or password leaked in signing failure.");
            }
        }

        var optionsType = assembly.GetType("ShiroBot.LagrangeAdapter.LagrangeConnectionOptions")!;
        var appInfoHandler = new AppInfoHandler();
        var clientType = assembly.GetType("ShiroBot.LagrangeAdapter.LagrangeSignClient")!;
        using (var client = (IDisposable)Activator.CreateInstance(clientType, BindingFlags.NonPublic | BindingFlags.Instance,
                   null, [new LagrangeAdapterConfig { FetchAppInfo = true, SignServer = "http://localhost/android/9.3.80-16750/ss_synthetic" }, CancellationToken.None, appInfoHandler], null)!)
        {
            var fetched = await (Task<JsonObject>)clientType.GetMethod("RequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(client, ["appinfo", null])!;
            var resolved = (BotAppInfo)optionsType.GetMethod("ResolveAppInfo", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [Protocols.AndroidPad, fetched])!;
            if (!appInfoHandler.Checked || resolved.PtVersion != "9.3.80" || resolved.Qua != "V1_AND_SQ_9.3.80_16750_YYB_D" || resolved.SdkInfo.SdkVersion != "6.0.0.2591")
                throw new Exception("AppInfo fetch did not retain version path and dynamic protocol metadata.");
        }
        try { optionsType.GetMethod("ValidatePlatform", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
            [Protocols.AndroidPad, new BotAppInfo { Os = "Mac" }]); throw new Exception("Android accepted PC AppInfo."); }
        catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException) { }
        foreach (var config in new[] {
            new LagrangeAdapterConfig { FetchAppInfo = true },
            new LagrangeAdapterConfig { FetchAppInfo = true, SignServer = "http://localhost", AppInfo = "{}" },
            new LagrangeAdapterConfig { CaptchaHelperUrl = "http://captcha.example.com/" },
            new LagrangeAdapterConfig { CaptchaHelperUrl = "https://user:secret@captcha.example.com/" },
            new LagrangeAdapterConfig { CaptchaHelperUrl = "https://captcha.example.com/?redirect=evil" },
            new LagrangeAdapterConfig { SignServer = "http://localhost/android/v/ss_synthetic", SignToken = "duplicate" },
            new LagrangeAdapterConfig { SignServer = "http://localhost/?token=synthetic" } })
        {
            try { config.Validate(); throw new Exception("Ambiguous AppInfo/credential configuration accepted."); }
            catch (ArgumentException) { }
        }
        await Verification(assembly);
        await AutomaticLogin();
        Console.WriteLine("Android three-endpoint contract, version/path/Bearer auth, errors, redaction and verification submission checks passed (offline).");
    }

    private static async Task Verification(Assembly assembly)
    {
        using var bot = BotFactory.Create(new BotConfig { Protocol = Protocols.AndroidPad }, Info());
        var adapter = new LagrangeAdapter();
        var adapterType = typeof(LagrangeAdapter);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var session = adapterType.GetField("_services", flags)!.GetValue(adapter)!;
        session.GetType().GetProperty("ActiveContext")!.SetValue(session, bot);
        adapterType.GetField("_started", flags)!.SetValue(adapter, true);
        var set = adapterType.GetMethod("SetVerification", flags)!;
        var handle = adapterType.GetMethod("HandleLoginPageAsync", flags)!;
        async Task<JsonObject> Call(string action, JsonObject? input = null) =>
            (JsonObject)(await (Task<JsonNode?>)handle.Invoke(adapter, [action, input, CancellationToken.None])!)!;
        set.Invoke(adapter, [bot, "sms", "https://example.com/verify", "+86-138****0000"]);
        var state = await Call("status");
        var oldId = state["verificationId"]!.GetValue<string>();
        if (state["verificationPhone"]!.GetValue<string>() != "+86-138****0000") throw new Exception("SMS phone missing.");
        set.Invoke(adapter, [bot, "sms", "https://example.com/new", ""]);
        try { await Call("sms", new JsonObject { ["code"] = "123456", ["verificationId"] = oldId }); throw new Exception("Stale verification accepted."); }
        catch (ArgumentException) { }

        var eventContext = typeof(BotContext).GetProperty("EventContext", flags)!.GetValue(bot)!;
        var logicType = typeof(BotContext).Assembly.GetType("Lagrange.Core.Internal.Logic.WtExchangeLogic")!;
        var logic = eventContext.GetType().GetMethod("GetLogic")!.MakeGenericMethod(logicType).Invoke(eventContext, null)!;
        var responseType = logicType.Assembly.GetType("Lagrange.Core.Internal.Events.Login.LoginEventResp")!;
        var originalTlvs = new Dictionary<ushort, byte[]> { [0x104] = [1], [0x174] = [2], [0x178] = [3], [0x204] = [4], [0x546] = [5] };
        var updatedTlvs = new Dictionary<ushort, byte[]> { [0x104] = [9] };
        var original = Activator.CreateInstance(responseType, (byte)239, originalTlvs)!;
        var updated = Activator.CreateInstance(responseType, (byte)160, updatedTlvs)!;
        logicType.GetMethod("PreserveSmsChallenge", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [updated, original]);
        if (updatedTlvs[0x104][0] != 9 || updatedTlvs[0x174][0] != 2 || updatedTlvs[0x178][0] != 3 || updatedTlvs[0x204][0] != 4 || updatedTlvs.ContainsKey(0x546))
            throw new Exception("SMS reply must retain its updates and inherit missing challenge metadata only.");
        var sms = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        logicType.GetField("_smsSource", flags)!.SetValue(logic, sms);
        state = await Call("status");
        state = await Call("sms", new JsonObject { ["code"] = "123456", ["verificationId"] = state["verificationId"]!.GetValue<string>() });
        if (await sms.Task != "123456" || state["verificationUrl"] is not null || state["status"]!.GetValue<string>() != "logging_in")
            throw new Exception("SMS submission did not resume the protocol waiter.");
        var captcha = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        logicType.GetField("_captchaSource", flags)!.SetValue(logic, captcha);
        set.Invoke(adapter, [bot, "captcha", "https://example.com/captcha", null]);
        state = await Call("status");
        await Call("captcha", new JsonObject { ["ticket"] = "synthetic-ticket", ["randstr"] = "", ["verificationId"] = state["verificationId"]!.GetValue<string>() });
        if ((await captcha.Task).Item1 != "synthetic-ticket") throw new Exception("Android captcha was not submitted.");
        bot.Keystore.Uin = 123456789;
        set.Invoke(adapter, [bot, "captcha", "https://ti.qq.com/safe/tools/captcha/sms-verify-login?aid=16&sid=123&uin=0&cap_cd=abc%2Bdef%2A%2A", null]);
        state = await Call("status");
        var helper = new Uri(state["verificationUrl"]!.GetValue<string>());
        var query = System.Web.HttpUtility.ParseQueryString(helper.Query);
        if (helper.AbsolutePath != "/" || helper.Host != "ticket-helper.shiroka.org" || helper.Scheme != "https" || query["uin"] != "123456789" ||
            query["sid"] != "123" || query["cap_cd"] != "abc+def**")
            throw new Exception("Android captcha helper lost account or challenge parameters.");
        try { await Call("captcha", new JsonObject { ["ticket"] = "terror_1006_synthetic", ["verificationId"] = state["verificationId"]!.GetValue<string>() }); throw new Exception("Failed helper ticket accepted."); }
        catch (ArgumentException) { }
        if ((await Call("status"))["verificationId"]!.GetValue<string>() != state["verificationId"]!.GetValue<string>())
            throw new Exception("Invalid ticket consumed the current challenge.");
        adapter.CurrentConfigValue.CaptchaHelperUrl = "https://captcha.example.test/tools/ticket/";
        set.Invoke(adapter, [bot, "captcha", "https://ti.qq.com/safe/tools/captcha/sms-verify-login?aid=16&sid=123&uin=0", null]);
        if (!(await Call("status"))["verificationUrl"]!.GetValue<string>().StartsWith("https://captcha.example.test/tools/ticket/?", StringComparison.Ordinal))
            throw new Exception("Configured self-hosted helper path was ignored.");
        set.Invoke(adapter, [bot, "device", "javascript:alert(1)", null]);
        if ((await Call("status"))["verificationUrl"] is not null) throw new Exception("Unsafe verification URL accepted.");
        try { await Call("sms-login", new JsonObject { ["uin"] = "not-an-account" }); throw new Exception("Invalid SMS account accepted."); }
        catch (ArgumentException) { }
        // Keep synthetic context ownership local; no socket/login task was started.
        session.GetType().GetProperty("ActiveContext")!.SetValue(session, null);
    }

    private static async Task AutomaticLogin()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lagrange-login-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var lifetime = DispatchProxy.Create<ShiroBot.SDK.Plugin.IPluginLifetime, HeldLifetime>();
        var held = (HeldLifetime)(object)lifetime;
        var adapter = new LagrangeAdapter
        {
            Config = new AutoConfig(Path.Combine(directory, "config.toml")), Lifetime = lifetime,
            Logger = DispatchProxy.Create<IConsoleLogger, NullLogger>()
        };
        try
        {
            // The lifetime deliberately holds, rather than invokes, the login
            // delegate: this checks startup without any real login/socket call.
            await adapter.StartAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var gate = (SemaphoreSlim)typeof(LagrangeAdapter).GetField("_lifecycle", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(adapter)!;
            if (held.Runs != 1 || held.Completion.Task.IsCompleted || gate.CurrentCount != 1)
                throw new Exception("AutoLogin held the lifecycle lock while waiting for human verification.");
        }
        finally
        {
            held.Completion.TrySetResult();
            await adapter.StopAsync();
            Directory.Delete(directory, recursive: true);
        }
        Console.WriteLine("Automatic login returns control while the login task is pending (no network).");
    }

    public class HeldLifetime : DispatchProxy
    {
        internal TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Runs;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_Stopping") return CancellationToken.None;
            if (method.Name == "Run") { Runs++; return Completion.Task; }
            return null;
        }
    }

    public class NullLogger : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
    }

    private sealed class AutoConfig(string path) : ShiroBot.SDK.Config.IConfigContext
    {
        private readonly LagrangeAdapterConfig _config = new() { AutoLogin = true };
        public string ConfigPath => path;
        public T Load<T>() where T : class, new() => _config as T ?? new T();
        public void Save<T>(T value) where T : class { }
        public void SetValue(string path, object? value) { }
        public IDisposable Watch<T>(Action<T> changed, int debounceMs = 500) where T : class, new() => throw new NotSupportedException();
    }

    private static BotAppInfo Info() => new()
    {
        Os = "ANDROID", CurrentVersion = "9.3.80.555ca0cf", PtVersion = "9.3.80",
        Qua = "V1_AND_SQ_9.3.80_16750_YYB_D", AppId = 16, SubAppId = 537400153,
        SdkInfo = new WtLoginSdkInfo { SdkVersion = "6.0.0.2591" }
    };

    private sealed class ReplyHandler(string reply, HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(reply, System.Text.Encoding.UTF8, "application/json") });
    }

    private sealed class AppInfoHandler : HttpMessageHandler
    {
        internal bool Checked;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Checked = request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/android/9.3.80-16750/ss_synthetic/appinfo" && request.Headers.Authorization is null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"Os\":\"ANDROID\",\"PtVersion\":\"9.3.80\",\"Qua\":\"V1_AND_SQ_9.3.80_16750_YYB_D\",\"SdkInfo\":{\"SdkVersion\":\"6.0.0.2591\"}}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class AndroidHandler(string prefix, bool pathToken) : HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var data = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            var endpoint = request.RequestUri!.AbsolutePath[prefix.Length..];
            if (!request.RequestUri.AbsolutePath.StartsWith(prefix + "/", StringComparison.Ordinal) ||
                (pathToken ? request.Headers.Authorization is not null : request.Headers.Authorization?.ToString() != "Bearer synthetic-bearer") ||
                data["qua"]!.GetValue<string>() != "V1_AND_SQ_9.3.80_16750_YYB_D" || data["version"]!.GetValue<string>() != "9.3.80" ||
                data["guid"]!.GetValue<string>().Length != 32 || data["uin"]!.GetValue<long>() != (endpoint == "/get_tlv553" ? 123 : 456))
                throw new Exception("Android identity, authentication or base URL mismatch.");
            string reply;
            switch (endpoint)
            {
                case "/sign":
                    if (data["cmd"]!.GetValue<string>() != "wtlogin.login" || data["buffer"]!.GetValue<string>() != "ab" || data["seq"]!.GetValue<int>() != -1)
                        throw new Exception("Android sign fields mismatch.");
                    reply = "{\"code\":0,\"data\":{\"sign\":\"01\",\"token\":\"02\",\"extra\":\"03\"}}";
                    break;
                case "/energy":
                    if (data["data"]!.GetValue<string>() != "810_2" || data["ver"]!.GetValue<string>() != "6.0.0.2591") throw new Exception("Energy SDK/module mismatch.");
                    reply = "{\"code\":0,\"data\":\"04\"}";
                    break;
                case "/get_tlv553":
                    if (data["data"]!.GetValue<string>() != "810_8") throw new Exception("TLV module mismatch.");
                    reply = "{\"code\":0,\"data\":\"05\"}";
                    break;
                default: throw new Exception("Unexpected Android endpoint.");
            }
            Calls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
