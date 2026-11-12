using System.Text.Json;
using System.Text.Json.Nodes;
using Lagrange.Core;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Events.EventArgs;
using ShiroBot.LagrangeAdapter.Services;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;

[assembly: RequiresShiroBotPackage("shirobot.model.qq", MinimumVersion = "1.0.2")]

namespace ShiroBot.LagrangeAdapter;

[BotAdapter("lagrange",
    Name = "LagrangeAdapter",
    Version = "0.2.0",
    Description = "In-process QQ adapter powered by LagrangeV2 (Lagrange.Core)",
    Protocol = "lagrange-v2")]
public class LagrangeAdapter : IBotAdapter, ILifetimeAwareAdapter, IPluginActionProvider
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _loginCancellation;
    private Task? _loginTask;
    private LagrangeSignProvider? _signProvider;
    private TaskCompletionSource<string>? _qrReady;
    private bool _started;
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
            var config = Config.Load<LagrangeAdapterConfig>();
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
        var resolved = LagrangeConnectionOptions.ResolveAppInfo(protocol, appInfo);
        _loginCancellation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime?.Stopping ?? CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(config.SignServer))
            _signProvider = new LagrangeSignProvider(config.SignServer, _loginCancellation.Token);
        var botConfig = new BotConfig { Protocol = protocol, AutoReconnect = true, AutoReLogin = true, SignProvider = _signProvider };
        Directory.CreateDirectory(Path.GetDirectoryName(keystorePath)!);
        var context = !fresh && File.Exists(keystorePath)
            ? BotFactory.Create(botConfig, JsonSerializer.Deserialize<BotKeystore>(File.ReadAllText(keystorePath))
                ?? throw new InvalidDataException("Keystore 内容为空。"), resolved)
            : BotFactory.Create(botConfig, resolved);
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

        RegisterLifecycleEvents(context, keystorePath);
        _eventService.Attach(_session);

    }

    private async Task LoginAsync(LagrangeAdapterConfig config, bool qr, CancellationToken token)
    {
        var context = _services.Context;
        Logger.Info($"Lagrange 开始登录(协议: {config.Protocol})...");
        var success = qr || string.IsNullOrWhiteSpace(config.Password) || config.Uin == 0
            ? await context.Login(token)
            : await context.Login(config.Uin, config.Password, token);
        if (!success) throw new InvalidOperationException("Lagrange 登录失败，请查看协议日志。");
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
            var config = Config.Load<LagrangeAdapterConfig>();
            InitializeConnection(config, true); // Explicit QR login bypasses password and saved tickets.
            _qrReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Run(CancellationToken unused)
            {
                try { await LoginAsync(config, true, _loginCancellation!.Token); }
                catch (OperationCanceledException) { _qrReady?.TrySetCanceled(); }
                catch (Exception ex) { _qrReady?.TrySetException(ex); Logger.Error($"Lagrange 登录失败: {ex.Message}"); }
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
            await ClearConnectionAsync();
            return new(false, $"扫码登录失败: {ex.Message}");
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

    private void RegisterLifecycleEvents(BotContext context, string keystorePath)
    {
        context.EventInvoker.RegisterEvent<BotLogEvent>((_, e) =>
        {
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
                    // Debug/Trace 不刷屏
            }
        });

        context.EventInvoker.RegisterEvent<BotQrCodeEvent>(async (_, e) =>
        {
            if (!ReferenceEquals(_services.ActiveContext, context)) return;
            var pngPath = Path.Combine(Path.GetDirectoryName(keystorePath)!, "qrcode.png");
            await File.WriteAllBytesAsync(pngPath, e.Image);
            _qrReady?.TrySetResult(pngPath);
            Logger.Warning($"请扫码登录 QQ。二维码已保存: {pngPath}");
            Logger.Warning($"或使用链接生成二维码: {e.Url}");
        });

        context.EventInvoker.RegisterEvent<BotQrCodeQueryEvent>((_, e) =>
        {
            if (e.State == BotQrCodeQueryEvent.TransEmpState.WaitingForConfirm)
            {
                Logger.Info("二维码已扫描,等待手机确认...");
            }
            else if (e.State == BotQrCodeQueryEvent.TransEmpState.CodeExpired)
            {
                Logger.Warning("二维码已过期,请重新执行扫码登录 Action。");
            }
        });

        context.EventInvoker.RegisterEvent<BotCaptchaEvent>((_, e) =>
            Logger.Error($"登录需要滑块验证,暂不支持交互式提交。CaptchaUrl: {e.CaptchaUrl}"));

        context.EventInvoker.RegisterEvent<BotNewDeviceVerifyEvent>((_, e) =>
            Logger.Warning($"新设备验证,请用手机 QQ 扫码: {e.Url}"));

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

public sealed class LagrangeAdapterConfig
{
    /// <summary>启动后自动恢复会话或登录；false 时由 Action 触发扫码。</summary>
    public bool AutoLogin { get; set; } = true;

    /// <summary>覆盖当前协议内置 AppInfo 的字段，可只提供需要修改的字段。</summary>
    public string? AppInfo { get; set; }

    /// <summary>AppInfo JSON 文件路径，相对于适配器配置目录。与 AppInfo 互斥。</summary>
    public string? AppInfoPath { get; set; }

    /// <summary>完整签名 POST 端点，留空使用 Lagrange 默认签名服务。</summary>
    public string? SignServer { get; set; }

    /// <summary>QQ 号。配合 Password 使用密码登录;留 0 使用二维码登录。</summary>
    public long Uin { get; set; }

    /// <summary>密码。留空使用二维码登录(推荐,首次扫码后 keystore 自动续期)。</summary>
    public string? Password { get; set; }

    /// <summary>协议: Linux / Windows / MacOs / AndroidPhone / AndroidPad / AndroidWatch。默认 Linux。</summary>
    public string Protocol { get; set; } = "Linux";

    /// <summary>keystore 保存路径,留空则保存在适配器配置目录。</summary>
    public string? KeystorePath { get; set; }
}
