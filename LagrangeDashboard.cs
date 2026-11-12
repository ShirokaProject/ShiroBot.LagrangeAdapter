using System.Text.Json.Nodes;
using Lagrange.Core.Common.Interface;
using ShiroBot.SDK.Dashboard;

namespace ShiroBot.LagrangeAdapter;

public partial class LagrangeAdapter
{
    private byte[]? _qrImage;
    private string _loginStatus = "ready";
    private string? _verificationUrl;
    private string? _loginError;

    private static string DescribeLoginFailure(Exception exception, LagrangeAdapterConfig config)
    {
        var root = exception.GetBaseException();
        var text = $"{root.GetType().Name}: {root.Message}";
        foreach (var secret in new[] { config.Password, config.SignToken }.Where(x => !string.IsNullOrEmpty(x)))
            text = text.Replace(secret!, "[已隐藏]", StringComparison.Ordinal);
        return text;
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
            ["verificationUrl"] = _verificationUrl
        };
    }

    private async Task<JsonNode?> HandleLoginPageAsync(string action, JsonNode? arguments, CancellationToken token)
    {
        if (action == "status") return LoginPageState();
        if (action == "qr-login")
        {
            var result = await ExecuteActionAsync("qr-login", token);
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
                InitializeConnection(config, true);
                _loginStatus = "logging_in";
                var stopping = _loginCancellation!.Token;
                async Task Run(CancellationToken unused)
                {
                    try { await LoginAsync(config, false, stopping); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _loginStatus = "failed"; _loginError = DescribeLoginFailure(ex, config); Logger.Error($"账号密码登录失败: {_loginError}"); }
                    finally { config.Password = ""; }
                }
                _loginTask = Lifetime is null ? Run(CancellationToken.None) : Lifetime.Run(Run);
                return LoginPageState();
            }
            if (action == "captcha")
            {
                var ticket = arguments?["ticket"]?.GetValue<string>() ?? "";
                var randstr = arguments?["randstr"]?.GetValue<string>() ?? "";
                if (ticket.Length is 0 or > 8192 || randstr.Length is 0 or > 1024 || _loginStatus != "captcha")
                    throw new ArgumentException("当前无滑块验证请求，或验证结果无效。");
                if (!_services.Context.SubmitCaptcha(ticket, randstr)) throw new InvalidOperationException("滑块验证提交失败。");
            }
            else if (action == "sms")
            {
                var code = arguments?["code"]?.GetValue<string>() ?? "";
                if (code.Length is 0 or > 16 || code.Any(c => !char.IsAsciiDigit(c)) || _loginStatus != "sms")
                    throw new ArgumentException("当前无短信验证请求，或验证码无效。");
                if (!_services.Context.SubmitSMSCode(code)) throw new InvalidOperationException("短信验证码提交失败。");
            }
            else throw new ArgumentException("未知的登录操作。");
            _loginStatus = "logging_in";
            _verificationUrl = null;
            return LoginPageState();
        }
        finally { _lifecycle.Release(); }
    }
}
