using System.Text.Json.Nodes;
using Lagrange.Core.Common.Interface;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Dashboard;

namespace ShiroBot.LagrangeAdapter;

public partial class LagrangeAdapter
{
    private byte[]? _qrImage;
    private string _loginStatus = "ready";
    private string? _verificationUrl;
    private string? _verificationPhone;
    private string? _verificationId;
    private string? _loginError;
    private DateTimeOffset _smsLoginNotBefore;

    private static string DescribeLoginFailure(Exception exception, LagrangeAdapterConfig config)
    {
        var root = exception.GetBaseException();
        var text = $"{root.GetType().Name}: {root.Message}";
        return LagrangeSecrets.Redact(text, config);
    }

    private void RegisterDashboard(IPluginDashboard dashboard) => dashboard.AddWebPage("login", "QQ 登录",
        new DashboardWebPage(typeof(LagrangeAdapter).Assembly, "dashboard/")
        {
            Icon = "qr_code_scanner", Description = "扫码或使用 QQ 账号密码登录，查看当前连接状态。",
            StrictDom = true, HandleAsync = HandleLoginPageAsync
        });

    private JsonObject LoginPageState()
    {
        var context = _services.ActiveContext;
        return new JsonObject
        {
            ["online"] = context?.IsOnline == true,
            ["status"] = context?.IsOnline == true ? "online" : _loginStatus,
            ["uin"] = context?.BotUin.ToString(), ["name"] = context?.BotInfo?.Name,
            ["qr"] = _qrImage is { } image ? Convert.ToBase64String(image) : null,
            ["error"] = _loginError,
            ["verificationUrl"] = _verificationUrl,
            ["verificationPhone"] = _verificationPhone,
            ["verificationId"] = _verificationId,
            ["protocol"] = CurrentConfigValue.EffectiveProtocol,
            ["qrSupported"] = !CurrentConfigValue.EffectiveProtocol.StartsWith("Android", StringComparison.OrdinalIgnoreCase),
            ["passwordlessSmsSupported"] = CurrentConfigValue.EffectiveProtocol is "AndroidPhone" or "AndroidPad"
        };
    }

    private void ClearVerification()
    {
        _verificationUrl = null;
        _verificationPhone = null;
        _verificationId = null;
    }

    private void SetVerification(Lagrange.Core.BotContext context, string status, string? url, string? phone = null)
    {
        if (!ReferenceEquals(_services.ActiveContext, context)) return;
        _verificationUrl = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0
            ? uri.AbsoluteUri : null;
        if (status == "captcha" && context.Config.Protocol is Lagrange.Core.Common.Protocols.AndroidPhone or Lagrange.Core.Common.Protocols.AndroidPad)
            _verificationUrl = AndroidCaptchaHelperUrl(_verificationUrl, context.BotUin, CurrentConfigValue.CaptchaHelperUrl);
        _verificationPhone = phone;
        _verificationId = Guid.NewGuid().ToString("N");
        _loginError = null;
        _loginStatus = status;
    }

    // Same helper/query handoff as Yogurt: the native QQ page otherwise sends
    // its ticket to the mobile app bridge instead of displaying it in a browser.
    private static string? AndroidCaptchaHelperUrl(string? url, long uin, string helperUrl)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.Host.Equals("ti.qq.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith("/safe/tools/captcha/", StringComparison.Ordinal)) return url;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        if (query["uin"] == "0" && uin > 0)
            query["uin"] = uin.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return helperUrl.TrimEnd('?') + "?" + query;
    }

    private async Task<JsonNode?> HandleLoginPageAsync(string action, JsonNode? arguments, CancellationToken token)
    {
        switch (action)
        {
            case "status":
                return LoginPageState();
            case "qr-login":
            case "quick-login":
                var result = await ExecuteActionAsync(action, token);
                if (!result.Ok) _loginError = result.Message;
                return LoginPageState();
        }

        await _lifecycle.WaitAsync(token);
        try
        {
            if (!_started) throw new InvalidOperationException("请先启动适配器实例。");
            if (action == "refresh-session")
            {
                if (!_services.Context.IsOnline) throw new InvalidOperationException("请先登录。");
                await _services.Context.RefreshSession(token);
                await PersistSessionAsync(_services.Context, ResolveKeystorePath(CurrentConfigValue));
                return LoginPageState();
            }
            if (action == "check-connection")
            {
                if (!_services.Context.IsOnline) throw new InvalidOperationException("请先登录。");
                var checks = new JsonArray();
                async Task Check(string operation, Func<Task> run)
                {
                    try { await run(); checks.Add(new JsonObject { ["operation"] = operation, ["ok"] = true }); }
                    catch (Exception ex) { checks.Add(new JsonObject { ["operation"] = operation, ["ok"] = false,
                        ["error"] = DescribeLoginFailure(ex, CurrentConfigValue) }); }
                }
                var system = new Services.LagrangeQqSystemApi(_services);
                var friend = new Services.LagrangeQqFriendApi(_services);
                var messaging = new Services.LagrangeQqMessageApi(_services);
                var group = new Services.LagrangeQqGroupApi(_services);
                await Check("login-info", async () => { await ((IQSystemApi)system).GetLoginInfoAsync(token); });
                await Check("version-info", async () => { await ((IQSystemApi)system).GetImplInfoAsync(token); });
                await Check("self-profile", async () => { await system.GetUserProfileAsync(_services.Context.BotUin.ToString(), token); });
                await Check("friends", async () => { await system.GetFriendListAsync(true, token); });
                await Check("csrf", async () => { await system.GetCsrfTokenAsync(token); });
                await Check("cookies-qun", async () => { await system.GetCookiesAsync("qun.qq.com", token); });
                await Check("groups", async () => { await system.GetGroupListAsync(true, token); });
                await Check("pins", async () => { await system.GetPeerPinsAsync(token); });
                await Check("custom-face", async () => { await system.GetCustomFaceUrlListAsync(token); });
                await Check("friend-requests", async () => { await friend.GetFriendRequestsAsync(20, false, token); });
                await Check("filtered-friend-requests", async () => { await friend.GetFriendRequestsAsync(20, true, token); });
                await Check("face-catalog", async () => { await LagrangeFaceCatalog.Create(_services.Context, 300, false); });
                if (arguments?["groupId"]?.GetValue<string>() is { Length: > 0 } groupId)
                {
                    var files = new Services.LagrangeQqFileApi(_services);
                    await Check("group-info", async () => { await group.GetGroupInfoAsync(groupId, false, token); });
                    await Check("self-member-info", async () => { await group.GetGroupMemberInfoAsync(groupId, _services.Context.BotUin.ToString(), false, token); });
                    await Check("group-notifications", async () => { await group.GetNotificationsAsync(limit: 5, cancellationToken: token); });
                    await Check("filtered-group-notifications", async () => { await group.GetNotificationsAsync(isFiltered: true, limit: 5, cancellationToken: token); });
                    await Check("group-join-requests", async () => { await group.GetJoinRequestsAsync(groupId, limit: 5, cancellationToken: token); });
                    await Check("group-history", async () => { await messaging.GetHistoryMessagesAsync(QMessageScene.Group, groupId, limit: 5, cancellationToken: token); });
                    await Check("group-members", async () => { await group.GetGroupMemberListAsync(groupId, true, token); });
                    await Check("group-files", async () => { await files.GetGroupFilesAsync(groupId, "/", token); });
                    await Check("group-announcements", async () => { await group.GetAnnouncementsAsync(groupId, token); });
                    await Check("group-essence", async () => { await group.GetEssenceMessagesPageAsync(groupId, 0, 10, token); });
                }
                var result = LoginPageState(); result["checks"] = checks; return result;
            }
            if (action is "password-login" or "sms-login")
            {
                var account = arguments?["uin"]?.GetValue<string>() ?? "";
                var password = arguments?["password"]?.GetValue<string>() ?? "";
                var smsOnly = action == "sms-login";
                if (!long.TryParse(account, out var uin) || uin <= 0 || uin > uint.MaxValue ||
                    (!smsOnly && password.Length is 0 or > 256))
                    throw new ArgumentException("请填写有效的 QQ 账号；密码登录还需要密码。");
                var selected = LoadAdapterConfig(Config);
                if (smsOnly && selected.Protocol is not ("AndroidPhone" or "AndroidPad"))
                    throw new NotSupportedException("独立短信登录仅支持 AndroidPhone / AndroidPad。");
                if (smsOnly && _smsLoginNotBefore > DateTimeOffset.UtcNow)
                    throw new InvalidOperationException("短信请求过于频繁，请一分钟后再试。");
                await ClearConnectionAsync();
                var config = selected;
                config.Uin = uin.ToString(System.Globalization.CultureInfo.InvariantCulture);
                config.Password = smsOnly ? "" : password; // This page never persists the password.
                try { await InitializeConnectionAsync(config, true); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var detail = DescribeLoginFailure(ex, config);
                    config.Password = "";
                    await ClearConnectionAsync();
                    _loginStatus = "failed";
                    _loginError = detail;
                    return LoginPageState();
                }
                _loginStatus = "logging_in";
                var stopping = _loginCancellation!.Token;
                if (smsOnly) _smsLoginNotBefore = DateTimeOffset.UtcNow.AddMinutes(1);
                _loginTask = Lifetime.Run(Run);
                return LoginPageState();

                async Task Run(CancellationToken unused)
                {
                    try
                    {
                        if (smsOnly)
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                            timeout.CancelAfter(TimeSpan.FromMinutes(5));
                            var context = _services.Context;
                            if (!await context.LoginWithSms(config.AccountUin, timeout.Token))
                                throw new InvalidOperationException("短信登录失败，请查看协议日志。");
                            await PersistSessionAsync(context, ResolveKeystorePath(config));
                            _loginStatus = "online";
                        }
                        else
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                            timeout.CancelAfter(TimeSpan.FromMinutes(5));
                            await LoginAsync(config, false, timeout.Token);
                        }
                    }
                    catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
                    catch (OperationCanceledException)
                    {
                        ClearVerification();
                        _loginStatus = "failed";
                        _loginError = "登录验证已超时，请重新发起登录。";
                    }
                    catch (Lagrange.Core.Exceptions.SmsLoginVerificationException ex)
                    {
                        SetVerification(_services.Context, "device", ex.Url);
                        _loginError = DescribeLoginFailure(ex, config);
                    }
                    catch (Exception ex)
                    {
                        if (_loginStatus != "device") _loginStatus = "failed";
                        _loginError = DescribeLoginFailure(ex, config);
                        Logger.Error($"QQ 登录失败: {_loginError}");
                    }
                    finally
                    {
                        config.Password = "";
                    }
                }
            }

            if ((action is "captcha" or "sms") &&
                (_verificationId is null || arguments?["verificationId"]?.GetValue<string>() != _verificationId))
                throw new ArgumentException("验证请求已更新，请刷新状态后重试。");
            var previousStatus = _loginStatus;
            var previousUrl = _verificationUrl;
            var previousPhone = _verificationPhone;
            var previousId = _verificationId;
            // Clear before waking the protocol task: its next event may arrive
            // immediately and must not be overwritten by this old submission.
            var submitted = false;
            try
            {
                switch (action)
                {
                    case "captcha":
                    {
                        var ticket = arguments?["ticket"]?.GetValue<string>() ?? "";
                        var randstr = arguments?["randstr"]?.GetValue<string>() ?? "";
                        if (ticket.Length is 0 or > 8192 || randstr.Length > 1024 ||
                            (randstr.Length == 0 && !_services.Context.Config.Protocol.ToString().StartsWith("Android", StringComparison.Ordinal)) || _loginStatus != "captcha")
                            throw new ArgumentException("当前无滑块验证请求，或验证结果无效。");
                        if (ticket.StartsWith("terror_", StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("验证码页面加载失败，请重新打开验证链接；terror_ 不是有效 Ticket。");
                        ClearVerification();
                        _loginStatus = "logging_in";
                        if (!_services.Context.SubmitCaptcha(ticket, randstr))
                            throw new InvalidOperationException("滑块验证提交失败。");
                        break;
                    }
                    case "sms":
                    {
                        var code = arguments?["code"]?.GetValue<string>() ?? "";
                        if (code.Length is < 4 or > 8 || code.Any(c => !char.IsAsciiDigit(c)) || _loginStatus != "sms")
                            throw new ArgumentException("当前无短信验证请求，或验证码无效。");
                        ClearVerification();
                        _loginStatus = "logging_in";
                        if (!_services.Context.SubmitSMSCode(code)) throw new InvalidOperationException("短信验证码提交失败。");
                        break;
                    }
                    default: throw new ArgumentException("未知的登录操作。");
                }
                submitted = true;
            }
            finally
            {
                if (!submitted && _verificationId is null && _loginStatus == "logging_in")
                {
                    _verificationUrl = previousUrl;
                    _verificationPhone = previousPhone;
                    _verificationId = previousId;
                    _loginStatus = previousStatus;
                }
            }
            return LoginPageState();
        }
        finally
        {
            _lifecycle.Release();
        }
    }
}
