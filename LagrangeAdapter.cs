using System.Text.Json;
using Lagrange.Core;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Events.EventArgs;
using ShiroBot.LagrangeAdapter.Services;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.LagrangeAdapter;

[BotAdapter("lagrange",
    Name = "LagrangeAdapter",
    Version = "0.1.0",
    Description = "In-process QQ adapter powered by LagrangeV2 (Lagrange.Core)",
    Protocol = "lagrange-v2")]
public class LagrangeAdapter : IBotAdapter
{
    private LagrangeSession? _session;
    private readonly LagrangeEventService _eventService = new();

    private IMessageService? _message;
    private IChannelService? _channel;
    private IUserService? _user;

    private LagrangeQqFriendApi? _qqFriendApi;
    private LagrangeQqGroupApi? _qqGroupApi;
    private LagrangeQqFileApi? _qqFileApi;
    private LagrangeQqSystemApi? _qqSystemApi;
    private LagrangeQqMessageApi? _qqMessageApi;

    public string Platform => LagrangeMapper.PlatformId;

    public IMessageService Message => _message ?? throw NotStarted();
    public IChannelService Channel => _channel ?? throw NotStarted();
    public IUserService User => _user ?? throw NotStarted();
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

    public async Task StartAsync()
    {
        var config = Config.Load<LagrangeAdapterConfig>();
        Config.Save(config);

        var keystorePath = ResolveKeystorePath(config);
        var botConfig = new BotConfig
        {
            Protocol = Enum.TryParse<Protocols>(config.Protocol, ignoreCase: true, out var protocol)
                ? protocol
                : Protocols.Linux,
            AutoReconnect = true,
            AutoReLogin = true
        };

        var context = File.Exists(keystorePath)
            ? BotFactory.Create(botConfig, JsonSerializer.Deserialize<BotKeystore>(
                await File.ReadAllTextAsync(keystorePath))!)
            : BotFactory.Create(botConfig);

        _session = new LagrangeSession { Context = context };
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

        Logger.Info($"Lagrange 开始登录(协议: {botConfig.Protocol})...");

        var success = string.IsNullOrWhiteSpace(config.Password) || config.Uin == 0
            ? await context.Login()          // 二维码 或 已有 keystore session
            : await context.Login(config.Uin, config.Password);

        if (!success)
        {
            Logger.Error("Lagrange 登录失败,请查看上方日志(二维码/验证码/密码错误)。");
            throw new InvalidOperationException("Lagrange 登录失败。");
        }

        LagrangeMapper.SelfId = context.BotUin.ToString();
        Logger.Success($"Lagrange 登录成功 - {context.BotInfo?.Name} ({context.BotUin})");
    }

    public async Task StopAsync()
    {
        if (_session is { Context: var context })
        {
            try
            {
                await context.Logout();
            }
            catch
            {
                // 登出失败不阻塞停止流程
            }

            context.Dispose();
        }
    }

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
            var pngPath = Path.Combine(Path.GetDirectoryName(keystorePath)!, "qrcode.png");
            await File.WriteAllBytesAsync(pngPath, e.Image);
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
                Logger.Warning("二维码已过期,请重启适配器重新获取。");
            }
        });

        context.EventInvoker.RegisterEvent<BotCaptchaEvent>((_, e) =>
            Logger.Error($"登录需要滑块验证,暂不支持交互式提交。CaptchaUrl: {e.CaptchaUrl}"));

        context.EventInvoker.RegisterEvent<BotNewDeviceVerifyEvent>((_, e) =>
            Logger.Warning($"新设备验证,请用手机 QQ 扫码: {e.Url}"));

        context.EventInvoker.RegisterEvent<BotRefreshKeystoreEvent>(async (_, e) =>
        {
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
            return config.KeystorePath;
        }

        var configDir = Path.GetDirectoryName(Config.ConfigPath) ?? AppContext.BaseDirectory;
        return Path.Combine(configDir, "lagrange-keystore.json");
    }

    private static InvalidOperationException NotStarted() =>
        new("Lagrange 适配器尚未启动。");
}

public sealed class LagrangeAdapterConfig
{
    /// <summary>QQ 号。配合 Password 使用密码登录;留 0 使用二维码登录。</summary>
    public long Uin { get; set; }

    /// <summary>密码。留空使用二维码登录(推荐,首次扫码后 keystore 自动续期)。</summary>
    public string? Password { get; set; }

    /// <summary>协议: Linux / Windows / MacOs / AndroidPhone / AndroidPad / AndroidWatch。默认 Linux。</summary>
    public string Protocol { get; set; } = "Linux";

    /// <summary>keystore 保存路径,留空则保存在适配器配置目录。</summary>
    public string? KeystorePath { get; set; }
}
