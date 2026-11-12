using System.Reflection;
using ShiroBot.LagrangeAdapter;

if (args is ["--export-appinfo", var output])
{
    File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(
        Lagrange.Core.Common.BotAppInfo.ProtocolToAppInfo[Lagrange.Core.Common.Protocols.Linux],
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return;
}

var assembly = typeof(LagrangeAdapter).Assembly;
var failures = new List<string>();
foreach (var type in assembly.GetTypes().Where(t => t.Namespace == "ShiroBot.LagrangeAdapter.Services"))
{
    var interfaces = type.GetInterfaces();
    var methods = interfaces.SelectMany(i => i.GetMethods()).ToArray();
    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                 .Where(m => !m.IsSpecialName && m.Name.EndsWith("Async")))
    {
        if (!methods.Any(m => m.Name == method.Name && m.ReturnType == method.ReturnType &&
            m.GetParameters().Select(p => p.ParameterType).SequenceEqual(method.GetParameters().Select(p => p.ParameterType))))
            failures.Add($"{type.Name}.{method}");
    }
}
if (failures.Count > 0) throw new Exception("Methods do not implement SDK contracts:\n" + string.Join("\n", failures));
Console.WriteLine("All service methods match SDK contracts.");

var adapter = new LagrangeAdapter();
if (adapter.Message is null || adapter.Channel is null || adapter.User is null || adapter.Event is null)
    throw new Exception("Services must be available before startup.");
await adapter.StopAsync();
await adapter.StopAsync();
using var cancellation = new CancellationTokenSource();
cancellation.Cancel();
try
{
    await adapter.User.GetSelfAsync(cancellation.Token);
    throw new Exception("Canceled calls must stop before accessing the connection.");
}
catch (OperationCanceledException) { }

var sessionType = assembly.GetType("ShiroBot.LagrangeAdapter.LagrangeSession")!;
var session = Activator.CreateInstance(sessionType, nonPublic: true)!;
var cache = sessionType.GetMethod("CacheMessage")!;
var find = sessionType.GetMethod("FindMessage")!;
var first = Lagrange.Core.Message.BotMessage.CreateCustomGroup(100, 1, "one", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), []);
var second = Lagrange.Core.Message.BotMessage.CreateCustomGroup(200, 2, "two", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), []);
first.Sequence = second.Sequence = 42;
cache.Invoke(session, [first]);
cache.Invoke(session, [second]);
if (!ReferenceEquals(find.Invoke(session, [42UL, 100L, true]), first) ||
    !ReferenceEquals(find.Invoke(session, [42UL, 200L, true]), second))
    throw new Exception("Message sequences from different groups must not collide.");
var mapped = LagrangeMapper.ToMessageEvent(first, "123");
if (mapped.SelfId != "123" || mapped.Channel.Id != "100" || mapped.MessageId != "42")
    throw new Exception("Message identity mapping is incorrect.");
Console.WriteLine("Lifecycle, cancellation, message cache isolation and mapping checks passed.");

var eventServiceType = assembly.GetType("ShiroBot.LagrangeAdapter.Services.LagrangeEventService")!;
var events = (ShiroBot.SDK.Adapter.IEventService)Activator.CreateInstance(eventServiceType, nonPublic: true)!;
var received = 0;
events.EventReceived += async _ => { await Task.Delay(10); Interlocked.Increment(ref received); };
events.EventReceived += _ => { Interlocked.Increment(ref received); return Task.CompletedTask; };
await (Task)eventServiceType.GetMethod("PublishAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
    .Invoke(events, [mapped])!;
if (received != 2) throw new Exception("All asynchronous event subscribers must be awaited.");
Console.WriteLine("Event subscriber dispatch checks passed.");

var optionsType = assembly.GetType("ShiroBot.LagrangeAdapter.LagrangeConnectionOptions")!;
var resolve = optionsType.GetMethod("ResolveAppInfo", BindingFlags.Static | BindingFlags.NonPublic)!;
var patch = System.Text.Json.Nodes.JsonNode.Parse("{\"CurrentVersion\":\"custom\",\"SdkInfo\":{\"SdkVersion\":\"custom-sdk\"}}")!.AsObject();
var appInfo = (Lagrange.Core.Common.BotAppInfo)resolve.Invoke(null, [Lagrange.Core.Common.Protocols.Linux, patch])!;
if (appInfo.CurrentVersion != "custom" || appInfo.SdkInfo.SdkVersion != "custom-sdk" || appInfo.AppId == 0)
    throw new Exception("AppInfo overrides must retain protocol defaults.");
if (Lagrange.Core.Common.BotAppInfo.ProtocolToAppInfo[Lagrange.Core.Common.Protocols.Linux].CurrentVersion == "custom")
    throw new Exception("AppInfo overrides must not mutate shared defaults.");
var signType = assembly.GetType("ShiroBot.LagrangeAdapter.LagrangeSignProvider")!;
var handler = new SignHandler();
using (var signer = (IDisposable)Activator.CreateInstance(signType, ["http://localhost/sign", CancellationToken.None, handler])!)
{
    var provider = (Lagrange.Core.Common.BotSignProvider)signer;
    var sign = await provider.GetSecSign(123, "wtlogin.login", 7, new byte[] { 0xab });
    if (Convert.ToHexString(sign!.SecSign!) != "AABB" || !provider.IsWhiteListCommand("wtlogin.login") || !handler.Checked)
        throw new Exception("Custom sign provider request/response mismatch.");
}
var stopped = await ((ShiroBot.SDK.Plugin.IPluginActionProvider)adapter).ExecuteActionAsync("qr-login");
if (stopped.Ok) throw new Exception("QR login must reject stopped adapters.");
Console.WriteLine("Custom AppInfo, signing API and login action checks passed.");

// Exercise newly implemented SDK dispatch with cancellation before connection access.
var groups = (ShiroBot.Model.QQ.IQGroupApi)Activator.CreateInstance(
    assembly.GetType("ShiroBot.LagrangeAdapter.Services.LagrangeQqGroupApi")!, session)!;
var files = (ShiroBot.Model.QQ.IQFileApi)Activator.CreateInstance(
    assembly.GetType("ShiroBot.LagrangeAdapter.Services.LagrangeQqFileApi")!, session)!;
var nativeMessages = (ShiroBot.Model.QQ.IQMessageApi)Activator.CreateInstance(
    assembly.GetType("ShiroBot.LagrangeAdapter.Services.LagrangeQqMessageApi")!, session)!;
foreach (var call in new Func<Task>[]
{
    () => groups.MuteMemberAsync("100", "1", TimeSpan.Zero, cancellation.Token),
    () => groups.KickMemberAsync("100", "1", cancellationToken: cancellation.Token),
    () => groups.SetWholeMuteAsync("100", true, cancellation.Token),
    () => groups.GetJoinRequestsAsync("100", cancellationToken: cancellation.Token),
    () => groups.SetEssenceMessageAsync("100", "42", cancellationToken: cancellation.Token),
    () => groups.SetMemberMutesAsync("100", [], cancellation.Token),
    () => files.GetGroupFilesAsync("100", cancellationToken: cancellation.Token),
    () => files.CreateGroupFolderAsync("100", "folder", cancellation.Token),
    () => files.RenameGroupFolderAsync("100", "folder", "new", cancellation.Token),
    () => files.DeleteGroupFolderAsync("100", "folder", cancellation.Token),
    () => nativeMessages.SendMessageDetailedAsync(ShiroBot.Model.QQ.QMessageScene.Group, "100", [], cancellation.Token),
    () => nativeMessages.GetForwardedMessagesAsync("forward", cancellation.Token)
})
{
    try { await call(); throw new Exception("SDK method ignored cancellation."); }
    catch (OperationCanceledException) { }
}
var operations = assembly.GetType("ShiroBot.LagrangeAdapter.LagrangeOperations")!;
var muteSeconds = operations.GetMethod("MuteSeconds", BindingFlags.NonPublic | BindingFlags.Static)!;
if ((uint)muteSeconds.Invoke(null, [TimeSpan.FromMilliseconds(100)])! != 1)
    throw new Exception("Mute duration must not truncate to unmute.");
try { muteSeconds.Invoke(null, [TimeSpan.FromSeconds(-1)]); throw new Exception("Negative mute duration accepted."); }
catch (TargetInvocationException ex) when (ex.InnerException is ArgumentOutOfRangeException) { }
var requireSuccess = operations.GetMethod("RequireSuccessAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
try
{
    await (Task)requireSuccess.Invoke(null, [Task.FromResult(false), "test", CancellationToken.None])!;
    throw new Exception("Protocol false was reported as success.");
}
catch (InvalidOperationException) { }
first.Time = 1700000000;
if (LagrangeMapper.ToMessageEvent(first).Timestamp != DateTimeOffset.FromUnixTimeSeconds(1700000000))
    throw new Exception("Current upstream Unix timestamp conversion is incorrect.");
Console.WriteLine("New upstream SDK dispatch, protocol failure, mute validation and Unix time checks passed.");

var nativeFile = (Lagrange.Core.Common.Entity.BotFileEntry)Activator.CreateInstance(
    typeof(Lagrange.Core.Common.Entity.BotFileEntry), BindingFlags.Instance | BindingFlags.NonPublic, null,
    ["file-id", "file.txt", "/", 123UL, 0L, 1700000001L, 456L, 1700000000L, 7U], null)!;
var mappedFile = LagrangeMapper.ToQq(nativeFile, "100");
if (mappedFile.FileSize != 123 || mappedFile.UploaderId != "456" || mappedFile.ExpireTime is not null ||
    mappedFile.UploadedTime != DateTimeOffset.FromUnixTimeSeconds(1700000000) || mappedFile.DownloadedTimes != 7)
    throw new Exception("Group file metadata mapping is incorrect.");
var notification = new Lagrange.Core.Common.Entity.BotGroupInviteNotification(100, 42, 456, "uid", 
    Lagrange.Core.Common.Entity.BotGroupNotificationState.Wait, null, null, 789, "inviter", true);
var mappedRequest = (ShiroBot.Model.QQ.QGroupJoinRequest)typeof(LagrangeMapper)
    .GetMethod("ToJoinRequest", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [notification])!;
if (mappedRequest.RequestId != "42" || !mappedRequest.IsInvited || !mappedRequest.IsFiltered ||
    mappedRequest.InviterId != "789" || mappedRequest.UserId != "456")
    throw new Exception("Group request approval credentials were not preserved.");
Console.WriteLine("File metadata and group request credential mapping checks passed.");

var milkyHandler = new MilkySignHandler();
var milkyOptions = new LagrangeAdapterConfig { SignApi = "Milky", SignServer = "http://localhost/api", SignToken = "test-token", Uin = 123 };
using (var signer = (IDisposable)Activator.CreateInstance(signType, [milkyOptions, CancellationToken.None, milkyHandler])!)
{
    var provider = (Lagrange.Core.Common.BotSignProvider)signer;
    using var bot = Lagrange.Core.Common.Interface.BotFactory.Create(new Lagrange.Core.Common.BotConfig { SignProvider = provider });
    var signed = await provider.GetSecSign(0, "wtlogin.login", 7, new byte[] { 0xab });
    if (!milkyHandler.Checked || Convert.ToHexString(signed!.SecSign!) != "AABB") throw new Exception("Milky sign protocol mismatch.");
}
foreach (var invalid in new[]
{
    new LagrangeAdapterConfig { HighwayChunkSize = 0 },
    new LagrangeAdapterConfig { SignApi = "unknown" },
    new LagrangeAdapterConfig { SignToken = "without-server" },
    new LagrangeAdapterConfig { AppInfo = "{}", AppInfoPath = "other.json" }
})
{
    try { invalid.Validate(); throw new Exception("Invalid connection options accepted."); }
    catch (ArgumentException) { }
}
var networkOptions = new LagrangeAdapterConfig
{
    AutoReconnect = false, AutoReLogin = false, UseIpv6Network = true, GetOptimumServer = false,
    LogLevel = "Debug", HighwayChunkSize = 1024, HighwayConcurrent = 2
};
var nativeConfig = (Lagrange.Core.Common.BotConfig)typeof(LagrangeAdapterConfig)
    .GetMethod("CreateBotConfig", BindingFlags.NonPublic | BindingFlags.Instance)!
    .Invoke(networkOptions, [Lagrange.Core.Common.Protocols.Linux])!;
if (nativeConfig.AutoReconnect || nativeConfig.AutoReLogin || !nativeConfig.UseIPv6Network || nativeConfig.GetOptimumServer ||
    nativeConfig.HighwayChunkSize != 1024 || nativeConfig.HighwayConcurrent != 2 || nativeConfig.LogLevel.ToString() != "Debug")
    throw new Exception("Connection options were not propagated.");
Console.WriteLine("Milky signing, connection option propagation and config validation checks passed.");

var configKeys = typeof(LagrangeAdapterConfig).GetProperties().Select(x =>
    System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(x.Name)).ToHashSet();
foreach (var requiredKey in new[] { "sign_server", "sign_api", "sign_token", "sign_proxy_url", "sign_timeout_seconds", "app_info_path", "use_ipv6_network", "auto_re_login" })
    if (!configKeys.Contains(requiredKey)) throw new Exception($"Configuration key was renamed unexpectedly: {requiredKey}");
if (adapter is not ShiroBot.SDK.Config.IConfigurableAdapter ||
    adapter is not ShiroBot.SDK.Config.IConfigurableComponent<LagrangeAdapterConfig>)
    throw new Exception("Adapter must expose the typed config schema.");
Console.WriteLine("Host configuration key and typed schema checks passed.");

sealed class SignHandler : HttpMessageHandler
{
    public bool Checked { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var body = System.Text.Json.Nodes.JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
        Checked = request.Method == HttpMethod.Post && body["cmd"]!.GetValue<string>() == "wtlogin.login" &&
            body["seq"]!.GetValue<int>() == 7 && body["src"]!.GetValue<string>() == "AB";
        return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"value\":{\"sign\":\"aabb\",\"token\":\"cc\",\"extra\":\"dd\"}}", System.Text.Encoding.UTF8, "application/json") };
    }
}

sealed class MilkySignHandler : HttpMessageHandler
{
    public bool Checked { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var body = System.Text.Json.Nodes.JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
        Checked = request.RequestUri!.AbsolutePath == "/api/sign/sec-sign" &&
            request.Headers.Authorization?.ToString() == "Bearer test-token" && body["uin"]!.GetValue<long>() == 123 &&
            body["command"]!.GetValue<string>() == "wtlogin.login" && body["body"]!.GetValue<string>() == "ab" &&
            !string.IsNullOrEmpty(body["guid"]!.GetValue<string>()) && !string.IsNullOrEmpty(body["qua"]!.GetValue<string>());
        return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"code\":0,\"value\":{\"sec_sign\":\"aabb\",\"sec_token\":\"cc\",\"sec_extra\":\"dd\"}}", System.Text.Encoding.UTF8, "application/json") };
    }
}
