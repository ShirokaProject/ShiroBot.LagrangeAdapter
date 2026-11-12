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
        if (overrides is not null) Merge(node, overrides);
        return node.Deserialize<BotAppInfo>(Json) ?? throw new InvalidDataException("AppInfo 不能为空。");
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
