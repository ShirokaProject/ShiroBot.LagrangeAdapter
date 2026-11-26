using System.Text.Json.Nodes;
using Lagrange.Core.Common;

namespace ShiroBot.LagrangeAdapter;

/// <summary>Yogurt Android /sign, /energy and /get_tlv553 contract.</summary>
internal sealed class LagrangeAndroidSignProvider : AndroidBotSignProvider, IDisposable
{
    private readonly LagrangeSignClient _client;
    private readonly LagrangeAdapterConfig _options;

    public LagrangeAndroidSignProvider(LagrangeAdapterConfig options, CancellationToken stopping,
        HttpMessageHandler? handler = null)
    {
        _options = options;
        _client = new LagrangeSignClient(options, stopping, handler);
    }

    public override bool IsWhiteListCommand(string cmd) => LagrangeSignProvider.IsSigningCommand(cmd) || cmd is
        "GDCTrpcProxy.down" or "GDCTrpcProxy.service" or "OidbSvcTrpcTcp.0x758_1" or
        "OidbSvcTrpcTcp.0x7c2_5" or "OidbSvcTrpcTcp.0x88d_0" or "OidbSvcTrpcTcp.0x8a1_7";

    private JsonObject Identity(long uin)
    {
        var info = Context.AppInfo;
        LagrangeConnectionOptions.ValidatePlatform(Context.Config.Protocol, info);
        if (string.IsNullOrWhiteSpace(info.Qua) || !info.Qua.StartsWith("V1_AND_", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(info.PtVersion))
            throw new InvalidDataException("Android AppInfo 必须包含匹配版本的 Qua 和 PtVersion。");
        return new JsonObject
        {
            ["uin"] = uin > 0 ? uin : Context.BotUin > 0 ? Context.BotUin : _options.AccountUin,
            ["guid"] = Convert.ToHexString(Context.Keystore.Guid).ToLowerInvariant(),
            ["version"] = info.PtVersion,
            ["qua"] = info.Qua
        };
    }

    public override async Task<SsoSecureInfo?> GetSecSign(long uin, string cmd, int seq, ReadOnlyMemory<byte> body)
    {
        var payload = Identity(uin);
        payload["cmd"] = cmd;
        payload["seq"] = seq;
        payload["buffer"] = Convert.ToHexString(body.Span).ToLowerInvariant();
        var root = await _client.RequestWithCancellationAsync("sign", payload, Context.MessageOperationCancellation);
        var value = root["data"] as JsonObject ?? throw new InvalidDataException("签名响应缺少 data 对象。");
        return new SsoSecureInfo
        {
            SecSign = LagrangeSignClient.Hex(value["sign"], "sign", false),
            SecToken = LagrangeSignClient.Hex(value["token"], "token"),
            SecExtra = LagrangeSignClient.Hex(value["extra"], "extra")
        };
    }

    public override async Task<byte[]> GetEnergy(long uin, string data)
    {
        var payload = Identity(uin);
        var sdk = Context.AppInfo.SdkInfo?.SdkVersion;
        if (string.IsNullOrWhiteSpace(sdk)) throw new InvalidDataException("Android AppInfo 缺少 SdkInfo.SdkVersion。");
        payload["data"] = data;
        payload["ver"] = sdk;
        var root = await _client.RequestAsync("energy", payload);
        return LagrangeSignClient.Hex(root["data"], "data", false);
    }

    public override async Task<byte[]> GetDebugXwid(long uin, string data)
    {
        var payload = Identity(uin);
        payload["data"] = data;
        var root = await _client.RequestAsync("get_tlv553", payload);
        return LagrangeSignClient.Hex(root["data"], "data", false);
    }

    public void Dispose() => _client.Dispose();
}
