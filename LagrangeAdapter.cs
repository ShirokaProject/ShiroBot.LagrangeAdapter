using System.Text.Json;
using System.Text.Json.Nodes;
using Lagrange.Core;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Events.EventArgs;
using ShiroBot.LagrangeAdapter.Services;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Dashboard;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;

[assembly: RequiresShiroBotPackage("shirobot.model.qq", MinimumVersion = "1.0.2")]

namespace ShiroBot.LagrangeAdapter;

[BotAdapter("lagrange",
    Name = "LagrangeAdapter",
    Version = "0.2.0",
    Description = "In-process QQ adapter powered by LagrangeV2 (Lagrange.Core)",
    Protocol = "lagrange-v2")]
public partial class LagrangeAdapter : IBotAdapter, ILifetimeAwareAdapter, IPluginActionProvider, IConfigurableAdapter, IConfigurableComponent<LagrangeAdapterConfig>
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _loginCancellation;
    private Task? _loginTask;
    private LagrangeSignProvider? _signProvider;
    private TaskCompletionSource<string>? _qrReady;
    private bool _started;
    private string? _automaticDeviceName;
    private LagrangeSession? _session;
    private readonly LagrangeSession _services = new();
    private readonly LagrangeEventService _eventService = new();

    private IMessageService? _message;
    private IChannelService? _channel;
    private IUserService? _user;

    private LagrangeQqFriendApi? _qqFriendApi;
    private LagrangeQqGroupApi? _qqGroupApi;
    private LagrangeQqFileApi? _qqFileApi;
    private LagrangeQqSystemApi? _qqSystemApi;
    private LagrangeQqMessageApi? _qqMessageApi;

    public LagrangeAdapterConfig CurrentConfigValue { get; private set; } = new();
    public ConfigApplyMode ApplyMode => ConfigApplyMode.RestartComponent;

    public async Task<object> InitializeConfigAsync(IConfigContext context, CancellationToken cancellationToken = default)
    {
        var config = LoadAdapterConfig(context);
        await OnConfigLoadedAsync(config, cancellationToken);
        return config;
    }

    private static LagrangeAdapterConfig LoadAdapterConfig(IConfigContext context)
    {
        // Read the old scalar before typed deserialization; do not rewrite unrelated fields.
        var raw = context.Load<Dictionary<string, object>>();
        if (raw.TryGetValue("uin", out var value) && value is long or int or uint or ulong)
        {
            var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;
            context.SetValue("uin", text == "0" ? "" : text);
        }
        return context.Load<LagrangeAdapterConfig>();
    }

    public Task OnConfigLoadedAsync(LagrangeAdapterConfig config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        config.Validate();
        CurrentConfigValue = config;
        Config.Save(config);
        return Task.CompletedTask;
    }

    public Task OnConfigChangedAsync(LagrangeAdapterConfig previous, LagrangeAdapterConfig current, CancellationToken cancellationToken) =>
        OnConfigLoadedAsync(current, cancellationToken);

    public IPluginLifetime Lifetime { get; set; } = null!;

    public string Platform => LagrangeMapper.PlatformId;

    public IMessageService Message => _message ??= new LagrangeMessageService(_services);
    public IChannelService Channel => _channel ??= new LagrangeChannelService(_services);
    public IUserService User => _user ??= new LagrangeUserService(_services);
    public IEventService Event => _eventService;
    public IConfigContext Config { get; set; } = null!;
    public IConsoleLogger Logger { get; set; } = null!;

    /// <summary>BotContext 本身也作为扩展暴露,插件可直接使用 Lagrange 原生 API。</summary>
    public TService? GetExtension<TService>() where TService : class =>
        this as TService
        ?? new Action<IPluginDashboard>(RegisterDashboard) as TService
        ?? _qqFriendApi as TService
        ?? _qqGroupApi as TService
        ?? _qqFileApi as TService
        ?? _qqSystemApi as TService
        ?? _qqMessageApi as TService
        ?? _session?.Context as TService;

    public IReadOnlyList<PluginActionDescriptor> Actions { get; } =
    [new("qr-login", "扫码登录", "重新获取二维码，使用手机 QQ 扫描确认。")];

    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_started) throw new InvalidOperationException("适配器已经启动。");
            var config = LoadAdapterConfig(Config);
            config.Validate();
            CurrentConfigValue = config;
            Config.Save(config);
            InitializeConnection(config, false);
            _started = true;
            Lifetime?.Defer(() => new ValueTask(StopAsync()));
            if (config.AutoLogin)
            {
                _loginTask = LoginAsync(config, false, _loginCancellation!.Token);
                await _loginTask;
            }
            else Logger.Info("Lagrange 已准备就绪，请通过适配器的扫码登录 Action 登录。");
        }
        catch
        {
            await ClearConnectionAsync();
            _started = false;
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    private void InitializeConnection(LagrangeAdapterConfig config, bool fresh)
    {
        config.Validate();
        var keystorePath = ResolveKeystorePath(config);
        if (!Enum.TryParse<Protocols>(config.Protocol, true, out var protocol))
            throw new ArgumentException($"未知 Lagrange 协议: {config.Protocol}");
        JsonObject? appInfo = string.IsNullOrWhiteSpace(config.AppInfo) ? null
            : JsonNode.Parse(config.AppInfo) as JsonObject ?? throw new InvalidDataException("AppInfo 必须是 JSON 对象。");
        if (!string.IsNullOrWhiteSpace(config.AppInfoPath))
        {
            if (appInfo is not null) throw new ArgumentException("AppInfo 与 AppInfoPath 只能设置一个。");
            appInfo = JsonNode.Parse(File.ReadAllText(ResolveConfigPath(config.AppInfoPath))) as JsonObject
                ?? throw new InvalidDataException("AppInfo 文件必须是 JSON 对象。");
        }
        if (appInfo?.Any(x => x.Key.Equals("NTLoginType", StringComparison.OrdinalIgnoreCase)) == true)
            Logger.Warning("AppInfo 中旧版 NTLoginType 字段在当前上游 V2 中没有对应项，已忽略；建议改用 V2 AppInfo 模板。");
        var resolved = LagrangeConnectionOptions.ResolveAppInfo(protocol, appInfo);
        _loginCancellation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime?.Stopping ?? CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(config.SignServer))
            _signProvider = new LagrangeSignProvider(config, _loginCancellation.Token);
        var botConfig = config.CreateBotConfig(protocol);
        botConfig.SignProvider = _signProvider;
        Directory.CreateDirectory(Path.GetDirectoryName(keystorePath)!);
        var keystore = !fresh && File.Exists(keystorePath)
            ? JsonSerializer.Deserialize<BotKeystore>(File.ReadAllText(keystorePath))
                ?? throw new InvalidDataException("Keystore 内容为空。")
            : BotKeystore.CreateEmpty();
        if (string.IsNullOrWhiteSpace(config.DeviceName))
        {
            _automaticDeviceName = LagrangeConnectionOptions.ResolveDeviceName(_automaticDeviceName ?? keystore.DeviceName);
            keystore.DeviceName = _automaticDeviceName;
        }
        else keystore.DeviceName = config.DeviceName.Trim();
        var context = BotFactory.Create(botConfig, keystore, resolved);
        _services.ActiveContext = context;
        _session = _services;
        _message = new LagrangeMessageService(_session);
        _channel = new LagrangeChannelService(_session);
        _user = new LagrangeUserService(_session);
        _qqFriendApi = new LagrangeQqFriendApi(_session);
        _qqGroupApi = new LagrangeQqGroupApi(_session);
        _qqFileApi = new LagrangeQqFileApi(_session);
        _qqSystemApi = new LagrangeQqSystemApi(_session);
        _qqMessageApi = new LagrangeQqMessageApi(_session);

        RegisterLifecycleEvents(context, keystorePath, config);
        _eventService.Attach(_session);

    }

    private async Task LoginAsync(LagrangeAdapterConfig config, bool qr, CancellationToken token)
    {
        var context = _services.Context;
        LagrangeConnectionOptions.ValidatePlatform(context.Config.Protocol, context.AppInfo);
        _loginStatus = qr ? "waiting_scan" : "logging_in";
        Logger.Info($"Lagrange 开始登录(协议: {config.Protocol})...");
        var success = qr || string.IsNullOrWhiteSpace(config.Password) || config.AccountUin == 0
            ? await context.Login(token)
            : await context.Login(config.AccountUin, config.Password, token);
        if (!success) throw new InvalidOperationException("Lagrange 登录失败，请查看协议日志。");
        _loginStatus = "online";
        Logger.Success($"Lagrange 登录成功 - {context.BotInfo?.Name} ({context.BotUin})");
    }

    public async Task<PluginActionResult> ExecuteActionAsync(string actionId, CancellationToken cancellationToken = default)
    {
        if (actionId != "qr-login") return new(false, $"未知 Action: {actionId}");
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (!_started) return new(false, "请先启动适配器。");
            await ClearConnectionAsync();
            var config = LoadAdapterConfig(Config);
            InitializeConnection(config, true); // Explicit QR login bypasses password and saved tickets.
            _qrReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Run(CancellationToken unused)
            {
                try { await LoginAsync(config, true, _loginCancellation!.Token); }
                catch (OperationCanceledException) { _qrReady?.TrySetCanceled(); }
                catch (Exception ex) { _loginStatus = "failed"; _loginError = DescribeLoginFailure(ex, config); _qrReady?.TrySetException(ex); Logger.Error($"Lagrange 登录失败: {_loginError}"); }
            }
            _loginTask = Lifetime is null ? Run(CancellationToken.None) : Lifetime.Run(Run);
            try
            {
                var path = await _qrReady.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
                return new(true, "二维码已生成，请使用手机 QQ 扫码。") { Details = path, Title = "扫码登录" };
            }
            catch (TimeoutException) { return new(true, "正在获取二维码，请查看日志中的二维码路径。"); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = _loginError ?? ex.GetBaseException().Message;
            await ClearConnectionAsync();
            _loginStatus = "failed";
            _loginError = error;
            return new(false, $"扫码登录失败: {error}");
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        try { _loginCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        await _lifecycle.WaitAsync();
        try { _started = false; await ClearConnectionAsync(); }
        finally { _lifecycle.Release(); }
    }

    private async Task ClearConnectionAsync()
    {
        _loginCancellation?.Cancel();
        if (_loginTask is not null)
        {
            try { await _loginTask; } catch (Exception ex) { Logger?.Warning($"登录任务结束: {ex.Message}"); }
        }
        _loginTask = null;
        _qrReady = null;
        _qrImage = null;
        _loginError = null;
        _verificationUrl = null;
        _loginStatus = "ready";
        var context = _session?.ActiveContext;
        _session = null;
        _eventService.Detach();
        _services.ActiveContext = null;
        _services.Clear();
        _qqFriendApi = null;
        _qqGroupApi = null;
        _qqFileApi = null;
        _qqSystemApi = null;
        _qqMessageApi = null;
        context?.Dispose();
        _signProvider?.Dispose();
        _signProvider = null;
        _loginCancellation?.Dispose();
        _loginCancellation = null;
    }

    private string ResolveConfigPath(string path) =>
        Path.GetFullPath(path, Path.GetDirectoryName(Path.GetFullPath(Config.ConfigPath))!);

    private void RegisterLifecycleEvents(BotContext context, string keystorePath, LagrangeAdapterConfig config)
    {
        context.EventInvoker.RegisterEvent<BotLogEvent>((_, e) =>
        {
            if (e.Level < context.Config.LogLevel) return;
            var text = $"[Lagrange][{e.Tag}] {e.Message}";
            switch (e.Level)
            {
                case Lagrange.Core.Events.EventArgs.LogLevel.Critical:
                case Lagrange.Core.Events.EventArgs.LogLevel.Error:
                    Logger.Error(text);
                    break;
                case Lagrange.Core.Events.EventArgs.LogLevel.Warning:
                    Logger.Warning(text);
                    break;
                case Lagrange.Core.Events.EventArgs.LogLevel.Information:
                    Logger.Info(text);
                    break;
                default:
                    Logger.Log(text);
                    break;
            }
        });

        context.EventInvoker.RegisterEvent<BotQrCodeEvent>(async (_, e) =>
        {
            if (!ReferenceEquals(_services.ActiveContext, context)) return;
            var pngPath = Path.Combine(Path.GetDirectoryName(keystorePath)!, "qrcode.png");
            await File.WriteAllBytesAsync(pngPath, e.Image);
            _qrImage = e.Image.ToArray();
            _loginStatus = "waiting_scan";
            _qrReady?.TrySetResult(pngPath);
            if (config.ConsoleQrCode)
            {
                try { Logger.Info("请使用手机 QQ 扫描下方二维码：\n" + LagrangeConsoleQrCode.Render(e.Url)); }
                catch (Exception ex) { Logger.Warning($"控制台二维码输出失败: {ex.Message}，请使用 PNG 或登录页面。"); }
            }
            Logger.Warning($"请扫码登录 QQ。二维码已保存: {pngPath}");
            Logger.Warning($"或使用链接生成二维码: {e.Url}");
        });

        context.EventInvoker.RegisterEvent<BotQrCodeQueryEvent>((_, e) =>
        {
            if (e.State == BotQrCodeQueryEvent.TransEmpState.WaitingForConfirm)
            {
                _loginStatus = "waiting_confirm";
                Logger.Info("二维码已扫描,等待手机确认...");
            }
            else if (e.State == BotQrCodeQueryEvent.TransEmpState.CodeExpired)
            {
                _loginStatus = "expired";
                Logger.Warning("二维码已过期,请重新执行扫码登录 Action。");
            }
        });

        context.EventInvoker.RegisterEvent<BotCaptchaEvent>((_, e) =>
        {
            _verificationUrl = e.CaptchaUrl;
            _loginStatus = "captcha";
            Logger.Warning("登录需要滑块验证，请在登录页面查看验证地址并提交结果。");
        });
        context.EventInvoker.RegisterEvent<BotSMSEvent>((_, e) =>
        {
            _verificationUrl = e.Url;
            _loginStatus = "sms";
            Logger.Warning("登录需要短信验证，请在登录页面提交验证码。");
        });

        context.EventInvoker.RegisterEvent<BotNewDeviceVerifyEvent>((_, e) =>
        {
            _verificationUrl = e.Url;
            _loginStatus = "device";
            Logger.Warning("登录需要新设备验证，请在登录页面查看验证地址。");
        });
        context.EventInvoker.RegisterEvent<BotOfflineEvent>((_, e) =>
        {
            _loginStatus = "offline";
            _qrImage = null;
        });

        context.EventInvoker.RegisterEvent<BotRefreshKeystoreEvent>(async (_, e) =>
        {
            if (!ReferenceEquals(_services.ActiveContext, context)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(keystorePath)!);
            await File.WriteAllTextAsync(keystorePath, JsonSerializer.Serialize(e.Keystore));
        });

        context.EventInvoker.RegisterEvent<BotOnlineEvent>((_, e) =>
            Logger.Success($"Lagrange 已上线({e.Reason})。"));
    }

    private string ResolveKeystorePath(LagrangeAdapterConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.KeystorePath))
        {
            return Path.GetFullPath(config.KeystorePath, Path.GetDirectoryName(Path.GetFullPath(Config.ConfigPath))!);
        }

        var configDir = Path.GetDirectoryName(Config.ConfigPath) ?? AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(configDir, "lagrange-keystore.json"));
    }

}

[ConfigModel]
public sealed class LagrangeAdapterConfig
{
    /// <summary>启动后自动恢复会话或登录；false 时由 Action 触发扫码。</summary>
    [ConfigField("启动后恢复会话或登录；关闭时通过登录页面手动扫码或输入账号密码。", Label = "自动登录", Group = "login", GroupLabel = "登录", GroupOrder = 10, Order = 10)]
    public bool AutoLogin { get; set; } = false;

    /// <summary>覆盖当前协议内置 AppInfo 的字段，可只提供需要修改的字段。</summary>
    [ConfigField("JSON 格式的局部覆盖值；与 AppInfo 文件路径二选一。留空使用平台默认值。", Label = "内联 AppInfo", Group = "protocol", GroupLabel = "协议与 AppInfo", GroupOrder = 20, Order = 20, Type = "textarea", Placeholder = "{\"CurrentVersion\":\"...\"}")]
    public string? AppInfo { get; set; } = "";

    /// <summary>AppInfo JSON 文件路径，相对于适配器配置目录。与 AppInfo 互斥。</summary>
    [ConfigField("相对适配器配置目录解析，例如 appinfo.example.json。JSON 可只填写需要覆盖的字段。", Label = "AppInfo 文件路径", Group = "protocol", GroupLabel = "协议与 AppInfo", GroupOrder = 20, Order = 30, Placeholder = "appinfo.example.json")]
    public string? AppInfoPath { get; set; } = "";

    /// <summary>Lagrange V2 签名基础地址，自动追加 api/sign/sec-sign；留空使用上游默认服务。</summary>
    [ConfigField("填写基础地址，自动追加 /api/sign/sec-sign；留空使用上游内置服务。", Label = "签名服务地址", Group = "sign", GroupLabel = "签名服务", GroupOrder = 30, Order = 20, Placeholder = "http://127.0.0.1:8080")]
    public string? SignServer { get; set; } = "";

    /// <summary>签名请求的 Bearer Token，留空不发送 Authorization。</summary>
    [ConfigField("以 Authorization: Bearer 发送。需要同时填写签名服务地址。", Label = "签名服务 Token", Group = "sign", GroupLabel = "签名服务", GroupOrder = 30, Order = 30, Type = "password")]
    public string SignToken { get; set; } = "";
    /// <summary>签名服务 HTTP(S) 代理地址，留空使用系统代理设置。</summary>
    [ConfigField("签名请求使用的 HTTP(S) 代理；留空使用系统代理设置。", Label = "签名服务代理", Group = "sign", GroupLabel = "签名服务", GroupOrder = 30, Order = 40, Placeholder = "http://127.0.0.1:7890")]
    public string SignProxyUrl { get; set; } = "";
    [ConfigField("自定义签名服务的请求超时，单位秒。", Label = "签名请求超时", Group = "sign", GroupLabel = "签名服务", GroupOrder = 30, Order = 50, Min = 1, Max = 600)]
    public int SignTimeoutSeconds { get; set; } = 30;

    [ConfigField("连接中断后由上游协议库自动重新连接。", Label = "断线自动重连", Group = "network", GroupLabel = "连接与日志", GroupOrder = 40, Order = 20)]
    public bool AutoReconnect { get; set; } = true;
    [ConfigField("在会话到期前自动刷新登录票据。", Label = "自动刷新登录凭据", Group = "network", GroupLabel = "连接与日志", GroupOrder = 40, Order = 10)]
    public bool AutoReLogin { get; set; } = true;
    [ConfigField("仅在当前网络支持 IPv6 时开启。", Label = "使用 IPv6", Group = "network", GroupLabel = "连接与日志", GroupOrder = 40, Order = 30)]
    public bool UseIpv6Network { get; set; } = false;
    [ConfigField("查询较优的腾讯接入服务器；关闭时使用上游内置地址。", Label = "自动选择服务器", Group = "network", GroupLabel = "连接与日志", GroupOrder = 40, Order = 40)]
    public bool GetOptimumServer { get; set; } = true;
    [ConfigField("低于所选等级的协议日志不输出；排查问题时可选择 Debug 或 Trace。", Label = "协议日志等级", Group = "network", GroupLabel = "连接与日志", GroupOrder = 40, Order = 50, Type = "select", Options = new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical" })]
    public string LogLevel { get; set; } = "Information";
    /// <summary>每个上传分块大小，最大 1048576 字节。</summary>
    [ConfigField("单位字节，最大 1 MiB。", Label = "上传分块大小", Group = "upload", GroupLabel = "媒体上传", GroupOrder = 50, Order = 10, Min = 1, Max = 1048576)]
    public uint HighwayChunkSize { get; set; } = 1048576;
    [ConfigField("媒体上传并发数；上传不稳定时可改为 1。", Label = "上传并发数", Group = "upload", GroupLabel = "媒体上传", GroupOrder = 50, Order = 20, Min = 1)]
    public uint HighwayConcurrent { get; set; } = 4;

    internal long AccountUin
    {
        get
        {
            var value = Uin?.Trim() ?? "";
            if (value.Length == 0) return 0;
            if (value.Length > 19 || value.Any(c => !char.IsAsciiDigit(c)) || !long.TryParse(value, out var uin))
                throw new ArgumentException("QQ 账号只能填写有效的数字账号。");
            return uin;
        }
    }

    public void Validate()
    {
        _ = AccountUin;
        if (DeviceName.Length > 64 || DeviceName.Any(char.IsControl))
            throw new ArgumentException("设备名称最多 64 个字符，不能包含控制字符。");
        if (!Enum.TryParse<Protocols>(Protocol, true, out var protocol) || !BotAppInfo.ProtocolToAppInfo.ContainsKey(protocol))
            throw new ArgumentException($"未知协议: {Protocol}");
        if (!Enum.TryParse<Lagrange.Core.Events.EventArgs.LogLevel>(LogLevel, true, out var level) || !Enum.IsDefined(level))
            throw new ArgumentException($"未知日志等级: {LogLevel}");
        if (SignTimeoutSeconds <= 0 || SignTimeoutSeconds > 600) throw new ArgumentOutOfRangeException(nameof(SignTimeoutSeconds));
        if (HighwayChunkSize == 0 || HighwayChunkSize > 1048576) throw new ArgumentOutOfRangeException(nameof(HighwayChunkSize));
        if (HighwayConcurrent == 0) throw new ArgumentOutOfRangeException(nameof(HighwayConcurrent));
        if (!string.IsNullOrWhiteSpace(AppInfo) && !string.IsNullOrWhiteSpace(AppInfoPath))
            throw new ArgumentException("AppInfo 与 AppInfoPath 只能设置一个。");
        foreach (var address in new[] { SignServer, SignProxyUrl }.Where(x => !string.IsNullOrWhiteSpace(x)))
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new ArgumentException("签名服务和代理必须为完整 HTTP(S) 地址。");
        if (string.IsNullOrWhiteSpace(SignServer) && (!string.IsNullOrWhiteSpace(SignToken) || !string.IsNullOrWhiteSpace(SignProxyUrl)))
            throw new ArgumentException("设置签名 Token 或代理前，请先填写 SignServer。");
    }

    internal BotConfig CreateBotConfig(Protocols protocol) => new()
    {
        Protocol = protocol,
        AutoReconnect = AutoReconnect,
        AutoReLogin = AutoReLogin,
        UseIPv6Network = UseIpv6Network,
        GetOptimumServer = GetOptimumServer,
        LogLevel = Enum.Parse<Lagrange.Core.Events.EventArgs.LogLevel>(LogLevel, true),
        HighwayChunkSize = HighwayChunkSize,
        HighwayConcurrent = HighwayConcurrent
    };

    /// <summary>QQ 号。配合 Password 使用密码登录;留 0 使用二维码登录。</summary>
    [ConfigField("自动密码登录使用的 QQ 号；手动登录可在登录页面填写。", Label = "QQ 账号", Group = "login", GroupLabel = "登录", GroupOrder = 10, Order = 20, Type = "string", Placeholder = "请输入 QQ 账号")]
    public string Uin { get; set; } = "";

    [ConfigField("获取登录二维码时，在宿主控制台输出可扫描的字符二维码；页面和 PNG 文件仍正常提供。终端需使用等宽字体并有足够宽度。", Label = "控制台显示二维码", Group = "login", GroupLabel = "登录", GroupOrder = 10, Order = 36)]
    public bool ConsoleQrCode { get; set; } = true;

    [ConfigField("留空自动生成 Shiro- 加随机 4–8 位数字；自动名称随登录凭据保存，刷新二维码时保持不变。", Label = "设备名称", Group = "login", GroupLabel = "登录", GroupOrder = 10, Order = 35, Placeholder = "Shiro-123456")]
    public string DeviceName { get; set; } = "";

    /// <summary>密码。留空使用二维码登录(推荐,首次扫码后 keystore 自动续期)。</summary>
    [ConfigField("仅自动密码登录需要。建议通过登录页面输入，避免把密码保存到配置。", Label = "登录密码", Group = "login", GroupLabel = "登录", GroupOrder = 10, Order = 30, Type = "password")]
    public string? Password { get; set; } = "";

    /// <summary>协议: Linux / Windows / MacOs / AndroidPhone / AndroidPad / AndroidWatch。默认 Linux。</summary>
    [ConfigField("选择与签名服务和 AppInfo 对应的平台。", Label = "客户端平台", Group = "protocol", GroupLabel = "协议与 AppInfo", GroupOrder = 20, Order = 10, Type = "select", Options = new[] { "Linux", "Windows", "MacOs", "AndroidPhone", "AndroidPad", "AndroidWatch" })]
    public string Protocol { get; set; } = "Linux";

    /// <summary>keystore 保存路径,留空则保存在适配器配置目录。</summary>
    [ConfigField("留空保存在适配器配置目录。凭据用于下次恢复会话，不应公开。", Label = "登录凭据路径", Group = "login", GroupLabel = "登录", GroupOrder = 10, Order = 40)]
    public string? KeystorePath { get; set; } = "";
}
