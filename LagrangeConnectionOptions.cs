using System.Text.Json;
using System.Text.Json.Nodes;
using Lagrange.Core.Common;

namespace ShiroBot.LagrangeAdapter;

internal static class LagrangeConnectionOptions
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    internal static BotAppInfo ResolveAppInfo(Protocols protocol, JsonObject? overrides)
    {
        if (!BotAppInfo.ProtocolToAppInfo.TryGetValue(protocol, out var defaults))
            throw new ArgumentException($"不支持的协议: {protocol}");
        var node = JsonSerializer.SerializeToNode(defaults)!.AsObject();
        if (overrides is not null)
        {
            var modern = new JsonObject();
            foreach (var (key, value) in overrides)
            {
                var sdkKey = key.ToLowerInvariant() switch
                {
                    "miscbitmap" => "MiscBitMap", "mainsigmap" => "MainSigMap", "subsigmap" => "SubSigMap",
                    "wtloginsdk" or "sdkversion" => "SdkVersion", "sdkbuildtime" => "SdkBuildTime", _ => null
                };
                if (sdkKey is not null)
                    Merge(node["SdkInfo"]!.AsObject(), new JsonObject { [sdkKey] = value?.DeepClone() });
                else if (!key.Equals("NTLoginType", StringComparison.OrdinalIgnoreCase)) modern[key] = value?.DeepClone();
            }
            Merge(node, modern);
        }
        return node.Deserialize<BotAppInfo>(Json) ?? throw new InvalidDataException("AppInfo 不能为空。");
    }

    internal static void ValidatePlatform(Protocols protocol, BotAppInfo info)
    {
        var os = info.Os?.ToLowerInvariant();
        var mismatch = protocol switch
        {
            Protocols.Linux => os != "linux",
            Protocols.MacOs => os is not ("mac" or "macos" or "osx"),
            Protocols.Windows => os is not ("windows" or "win"),
            _ => false
        };
        if (mismatch) throw new ArgumentException($"客户端平台 {protocol} 与 AppInfo.Os={info.Os} 不一致，请使用同一平台的 AppInfo 和签名服务。");
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            var actual = target.Select(x => x.Key).FirstOrDefault(x => x.Equals(key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"未知 AppInfo 字段: {key}");
            if (value is null) throw new InvalidDataException($"AppInfo.{key} 不能为空。");
            if (target[actual] is JsonObject child && value is JsonObject patch) Merge(child, patch);
            else target[actual] = value.DeepClone();
        }
    }
}
