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

foreach (var invalid in new[]
{
    new LagrangeAdapterConfig { HighwayChunkSize = 0 },
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
Console.WriteLine("Connection option propagation and config validation checks passed.");

var configKeys = typeof(LagrangeAdapterConfig).GetProperties().Select(x =>
    System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(x.Name)).ToHashSet();
foreach (var requiredKey in new[] { "sign_server", "sign_token", "sign_proxy_url", "sign_timeout_seconds", "app_info_path", "use_ipv6_network", "auto_re_login" })
    if (!configKeys.Contains(requiredKey)) throw new Exception($"Configuration key was renamed unexpectedly: {requiredKey}");
if (adapter is not ShiroBot.SDK.Config.IConfigurableAdapter ||
    adapter is not ShiroBot.SDK.Config.IConfigurableComponent<LagrangeAdapterConfig>)
    throw new Exception("Adapter must expose the typed config schema.");
Console.WriteLine("Host configuration key and typed schema checks passed.");

foreach (var property in typeof(LagrangeAdapterConfig).GetProperties())
{
    var field = property.GetCustomAttribute<ShiroBot.SDK.Config.ConfigFieldAttribute>();
    if (field is null || string.IsNullOrWhiteSpace(field.Label) || string.IsNullOrWhiteSpace(field.Description) || string.IsNullOrWhiteSpace(field.Group))
        throw new Exception($"Missing dashboard config annotation: {property.Name}");
}
var registerPage = adapter.GetExtension<Action<ShiroBot.SDK.Dashboard.IPluginDashboard>>();
if (registerPage is null) throw new Exception("Adapter must expose a Dashboard registration callback.");
Console.WriteLine("Chinese config annotations and adapter dashboard registration checks passed.");

if (typeof(LagrangeAdapterConfig).GetProperty("Uin")!.PropertyType != typeof(string) ||
    typeof(LagrangeAdapterConfig).GetProperty("Uin")!.GetCustomAttribute<ShiroBot.SDK.Config.ConfigFieldAttribute>()!.Type != "string")
    throw new Exception("QQ account must use a text input, not a numeric stepper.");
try { new LagrangeAdapterConfig { Uin = "123abc" }.Validate(); throw new Exception("Malformed QQ account accepted."); }
catch (ArgumentException) { }
Console.WriteLine("QQ account text input and validation checks passed.");

var legacyApp = (Lagrange.Core.Common.BotAppInfo)resolve.Invoke(null,
    [Lagrange.Core.Common.Protocols.Linux, System.Text.Json.Nodes.JsonNode.Parse("{\"MiscBitmap\":123,\"WtLoginSdk\":\"legacy-sdk\",\"MainSigMap\":1,\"SubSigMap\":2,\"NTLoginType\":1}")!.AsObject()])!;
if (legacyApp.SdkInfo.MiscBitMap != 123 || legacyApp.SdkInfo.SdkVersion != "legacy-sdk")
    throw new Exception("Legacy flat AppInfo fields were not moved into SdkInfo.");
Console.WriteLine("Legacy AppInfo compatibility checks passed.");

var rejectedSigner = (IDisposable)Activator.CreateInstance(signType,
    [new LagrangeAdapterConfig { SignServer = "http://localhost/sign", SignToken = "secret-test" }, CancellationToken.None, new RejectSignHandler()])!;
using (rejectedSigner)
{
    var rejectedProvider = (Lagrange.Core.Common.BotSignProvider)rejectedSigner;
    using var rejectedBot = Lagrange.Core.Common.Interface.BotFactory.Create(new Lagrange.Core.Common.BotConfig { SignProvider = rejectedProvider });
    try
    {
        await rejectedProvider.GetSecSign(1, "wtlogin.login", 1, new byte[] { 1 });
        throw new Exception("Signing rejection was accepted.");
    }
    catch (HttpRequestException ex)
    {
        if (!ex.Message.Contains("HTTP 400") || !ex.Message.Contains("签名参数无效") || ex.Message.Contains("secret-test"))
            throw new Exception("Signing errors must be readable and redact tokens.");
    }
}
Console.WriteLine("Signing failure reporting and token redaction checks passed.");

foreach (var (protocol, expected) in new[]
{
    (Lagrange.Core.Common.Protocols.Linux, "LNX"), (Lagrange.Core.Common.Protocols.MacOs, "MAC"),
    (Lagrange.Core.Common.Protocols.Windows, "WIN")
})
{
    var handlerY = new YogurtSignHandler($"V1_{expected}_NQ_7.0.3_54395_GW_B");
    using var signerY = (IDisposable)Activator.CreateInstance(signType,
        [new LagrangeAdapterConfig { SignServer = "http://localhost", SignToken = "test", Uin = "123" }, CancellationToken.None, handlerY])!;
    var providerY = (Lagrange.Core.Common.BotSignProvider)signerY;
    using var botY = Lagrange.Core.Common.Interface.BotFactory.Create(new Lagrange.Core.Common.BotConfig { Protocol = protocol, SignProvider = providerY },
        new Lagrange.Core.Common.BotAppInfo { CurrentVersion = "7.0.3-54395" });
    var resultY = await providerY.GetSecSign(999, "wtlogin.login", 100, new byte[] { 0xab });
    if (!handlerY.Checked || resultY!.SecSign!.Length != 0) throw new Exception("Yogurt endpoint, configured UIN, QUA or empty HEX contract mismatch.");
}
Console.WriteLine("Lagrange V2 PC signing endpoint, numeric account, three platform QUA and empty HEX checks passed.");

var resolveDevice = optionsType.GetMethod("ResolveDeviceName", BindingFlags.Static | BindingFlags.NonPublic)!;
for (var n = 0; n < 100; n++)
{
    var name = (string)resolveDevice.Invoke(null, ["Lagrange-ABCDEF"])!;
    if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^Shiro-[0-9]{4,8}$")) throw new Exception("Invalid automatic device name.");
    if ((string)resolveDevice.Invoke(null, [name])! != name) throw new Exception("Automatic device name was not retained.");
}
Console.WriteLine("Default device name format and stability checks passed.");

var renderQr = assembly.GetType("ShiroBot.LagrangeAdapter.LagrangeConsoleQrCode")!
    .GetMethod("Render", BindingFlags.Static | BindingFlags.NonPublic)!;
const string qrUrl = "https://example.com/qq-login?token=test";
var qrText = (string)renderQr.Invoke(null, [qrUrl])!;
var qrReference = Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(qrUrl, Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Low);
var qrRows = qrText.Replace("\r", "").TrimEnd('\n').Split('\n');
if (!new LagrangeAdapterConfig().ConsoleQrCode || qrRows.Any(row => row.Length != qrReference.Size + 8))
    throw new Exception("Console QR default or quiet zone width is incorrect.");
for (var row = 0; row < qrRows.Length; row++)
for (var column = 0; column < qrRows[row].Length; column++)
{
    var glyph = qrRows[row][column];
    var top = glyph is '█' or '▀';
    var bottom = glyph is '█' or '▄';
    if (top != qrReference.GetModule(column - 4, row * 2 - 4) ||
        bottom != qrReference.GetModule(column - 4, row * 2 - 3))
        throw new Exception("Console QR render changed the encoded login URL.");
}
Console.WriteLine("Console QR module fidelity, quiet zone and default checks passed.");

if (!adapter.Actions.Any(x => x.Id == "quick-login")) throw new Exception("Quick login action missing.");
if ((await adapter.ExecuteActionAsync("quick-login")).Ok) throw new Exception("Stopped adapter accepted quick login.");
var sessionCheck = optionsType.GetMethod("HasSession", BindingFlags.Static | BindingFlags.NonPublic)!;
var savedSession = new Lagrange.Core.Common.BotKeystore { Uin = 123 };
if ((bool)sessionCheck.Invoke(null, [savedSession])!) throw new Exception("Empty tickets accepted.");
savedSession.WLoginSigs.A2 = [1]; savedSession.WLoginSigs.D2 = [2];
if (!(bool)sessionCheck.Invoke(null, [savedSession])!) throw new Exception("Saved session rejected.");
var storage = assembly.GetType("ShiroBot.LagrangeAdapter.LagrangeStorage")!;
var ensureStorage = storage.GetMethod("EnsurePath", BindingFlags.NonPublic | BindingFlags.Static)!;
var rootStorage = Path.Combine(Path.GetTempPath(), "lagrange-storage-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(rootStorage);
try
{
    var configFile = Path.Combine(rootStorage, "config.toml");
    var firstConfig = new LagrangeAdapterConfig(); var secondConfig = new LagrangeAdapterConfig();
    ensureStorage.Invoke(null, [firstConfig, configFile]); ensureStorage.Invoke(null, [secondConfig, configFile]);
    var firstPath = firstConfig.KeystorePath!; var secondPath = secondConfig.KeystorePath!;
    if (firstPath == secondPath || Path.GetDirectoryName(firstPath) == Path.GetDirectoryName(secondPath))
        throw new Exception("Instance session or QR directories collide.");
    ensureStorage.Invoke(null, [firstConfig, configFile]);
    if (firstConfig.KeystorePath != firstPath) throw new Exception("Session storage changed across reload.");
    var fullPath = Path.Combine(rootStorage, firstPath);
    var acquire = storage.GetMethod("Acquire", BindingFlags.NonPublic | BindingFlags.Static)!;
    using (var lease = (IDisposable)acquire.Invoke(null, [fullPath])!)
    {
        try { using var duplicate = (IDisposable)acquire.Invoke(null, [fullPath])!; throw new Exception("Duplicate session path was accepted."); }
        catch (TargetInvocationException ex) when (ex.InnerException is IOException) { }
        var configA = new StoredConfigContext(Path.Combine(rootStorage, "config.toml"));
        var configB = new StoredConfigContext(configA.ConfigPath);
        var saveSession = storage.GetMethod("SaveSession", BindingFlags.NonPublic | BindingFlags.Static)!;
        saveSession.Invoke(null, [configA, savedSession]);
        var readSession = storage.GetMethod("ReadSession", BindingFlags.NonPublic | BindingFlags.Static)!;
        var restored = (Lagrange.Core.Common.BotKeystore)readSession.Invoke(null, [configA, firstConfig])!;
        if (restored.Uin != 123 || restored.WLoginSigs.A2[0] != 1 || configB.Values.ContainsKey("session_store"))
            throw new Exception("Inline session storage crossed instance boundaries or lost tickets.");
        if (File.Exists(fullPath)) throw new Exception("Inline session unexpectedly created a keystore JSON file.");
    }
    using var reopened = (IDisposable)acquire.Invoke(null, [fullPath])!;
}
finally { Directory.Delete(rootStorage, true); }
Console.WriteLine("Quick login tickets, per-instance storage, inline config save and exclusive path checks passed.");

var qrCleanupRoot = Path.Combine(Path.GetTempPath(), "lagrange-qr-cleanup-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(qrCleanupRoot);
try
{
    using var cleanupBot = Lagrange.Core.Common.Interface.BotFactory.Create(new Lagrange.Core.Common.BotConfig());
    var cleanupAdapter = new LagrangeAdapter { Config = new StoredConfigContext(Path.Combine(qrCleanupRoot, "config.toml")) };
    var services = typeof(LagrangeAdapter).GetField("_services", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cleanupAdapter)!;
    sessionType.GetProperty("ActiveContext")!.SetValue(services, cleanupBot);
    typeof(LagrangeAdapter).GetField("_qrImage", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cleanupAdapter, new byte[] { 1 });
    var png = Path.Combine(qrCleanupRoot, "qrcode.png");
    await File.WriteAllBytesAsync(png, new byte[] { 1 });
    await (Task)typeof(LagrangeAdapter).GetMethod("PersistSessionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(cleanupAdapter, [cleanupBot, Path.Combine(qrCleanupRoot, "keystore.json")])!;
    if (File.Exists(png) || typeof(LagrangeAdapter).GetField("_qrImage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cleanupAdapter) is not null ||
        !((StoredConfigContext)cleanupAdapter.Config).Values.ContainsKey("session_store"))
        throw new Exception("Successful login did not save inline session or clear its QR.");
}
finally { Directory.Delete(qrCleanupRoot, true); }
Console.WriteLine("Login completion inline persistence and QR file/memory cleanup checks passed.");

sealed class RejectSignHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        { Content = new StringContent("{\"message\":\"签名参数无效 secret-test\"}", System.Text.Encoding.UTF8, "application/json") });
}

sealed class YogurtSignHandler(string expectedQua) : HttpMessageHandler
{
    public bool Checked { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var data = System.Text.Json.Nodes.JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
        Checked = request.RequestUri!.AbsolutePath == "/api/sign/sec-sign" && request.Headers.Authorization?.ToString() == "Bearer test" &&
            data["uin"]!.GetValue<long>() == 123 && data["command"]!.GetValue<string>() == "wtlogin.login" &&
            data["seq"]!.GetValue<int>() == 100 && data["body"]!.GetValue<string>() == "ab" && data["qua"]!.GetValue<string>() == expectedQua;
        return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"code\":0,\"value\":{\"sec_sign\":\"\",\"sec_token\":\"\",\"sec_extra\":\"\"}}", System.Text.Encoding.UTF8, "application/json") };
    }
}

sealed class StoredConfigContext(string path) : ShiroBot.SDK.Config.IConfigContext
{
    public Dictionary<string, object> Values { get; } = new();
    public string ConfigPath => path;
    public T Load<T>() where T : class, new() => Values as T ?? new T();
    public void Save<T>(T value) where T : class => throw new Exception("Session persistence must update only its own field.");
    public void SetValue(string keyPath, object? value) => Values[keyPath] = value!;
    public IDisposable Watch<T>(Action<T> changed, int debounceMs = 500) where T : class, new() => throw new NotSupportedException();
}
