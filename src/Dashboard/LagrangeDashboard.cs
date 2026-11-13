using System.Text.Json.Nodes;
using Lagrange.Core.Common.Interface;
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
            ["protocol"] = CurrentConfigValue.Protocol,
            ["qrSupported"] = !CurrentConfigValue.Protocol.StartsWith("Android", StringComparison.OrdinalIgnoreCase),
            ["passwordlessSmsSupported"] = false
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
        _verificationPhone = phone;
        _verificationId = Guid.NewGuid().ToString("N");
        _loginError = null;
        _loginStatus = status;
    }

    private async Task<JsonNode?> HandleLoginPageAsync(string action, JsonNode? arguments, CancellationToken token)
    {
        switch (action)
        {
            case "status":
                return LoginPageState();
            case "sms-login":
                throw new NotSupportedException("当前 Lagrange 内核未实现免密码短信发码与登录；短信二次验证请在登录请求出现后提交。");
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
            if (action == "password-login")
            {
                var account = arguments?["uin"]?.GetValue<string>() ?? "";
                var password = arguments?["password"]?.GetValue<string>() ?? "";
                if (!long.TryParse(account, out var uin) || uin <= 0 || password.Length is 0 or > 256)
                    throw new ArgumentException("请填写有效的 QQ 账号与密码。");
                await ClearConnectionAsync();
                var config = LoadAdapterConfig(Config);
                config.Uin = uin.ToString(System.Globalization.CultureInfo.InvariantCulture);
                config.Password = password; // This page never persists the password.
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
                _loginTask = Lifetime.Run(Run);
                return LoginPageState();

                async Task Run(CancellationToken unused)
                {
                    try
                    {
                        await LoginAsync(config, false, stopping);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        _loginStatus = "failed";
                        _loginError = DescribeLoginFailure(ex, config);
                        Logger.Error($"账号密码登录失败: {_loginError}");
                    }
                    finally
                    {
                        config.Password = "";
                    }
                }
            }

            if (action is "captcha" or "sms" &&
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
