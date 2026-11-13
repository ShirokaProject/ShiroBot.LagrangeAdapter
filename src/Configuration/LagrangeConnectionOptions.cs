using System.Text.Json;
using System.Text.Json.Nodes;
using Lagrange.Core.Common;

namespace ShiroBot.LagrangeAdapter;

internal static class LagrangeConnectionOptions
{
    // Equivalent options otherwise share a framework metadata cache across collectible loads.
    // A resolver owned by this assembly keeps another running instance from pinning our types.
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = new LagrangeJsonContext()
    };

    internal static readonly JsonSerializerOptions SessionJson = new()
    {
        TypeInfoResolver = new LagrangeJsonContext()
    };

    internal static BotAppInfo ResolveAppInfo(Protocols protocol, JsonObject? overrides)
    {
        if (!BotAppInfo.ProtocolToAppInfo.TryGetValue(protocol, out var defaults))
            throw new ArgumentException($"不支持的协议: {protocol}");
        var node = JsonSerializer.SerializeToNode(defaults, Json)!.AsObject();
        if (overrides is null)
            return node.Deserialize<BotAppInfo>(Json) ?? throw new InvalidDataException("AppInfo 不能为空。");
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
                else if (!key.Equals("NTLoginType", StringComparison.OrdinalIgnoreCase))
                    modern[key] = value?.DeepClone();
            }

            // Kotlin serializes ByteArray as signed JSON numbers; .NET expects base64.
            var signatureKey = modern.Select(x => x.Key).FirstOrDefault(x =>
                x.Equals("ApkSignatureMd5", StringComparison.OrdinalIgnoreCase));
            if (signatureKey is not null && modern[signatureKey] is JsonArray signature)
            {
                if (signature.Count != 16) throw new InvalidDataException("AppInfo.ApkSignatureMd5 必须为 16 字节。");
                var bytes = new byte[signature.Count];
                for (var i = 0; i < bytes.Length; i++)
                {
                    if (signature[i] is not JsonValue element || !element.TryGetValue<int>(out var number) ||
                        number is < -128 or > 255)
                        throw new InvalidDataException("AppInfo.ApkSignatureMd5 包含无效字节。");
                    bytes[i] = unchecked((byte)number);
                }
                modern[signatureKey] = Convert.ToBase64String(bytes);
            }
            Merge(node, modern);
        }
        var info = node.Deserialize<BotAppInfo>(Json) ?? throw new InvalidDataException("AppInfo 不能为空。");
        ValidateAndroidLogin(protocol, info);
        return info;
    }

    private static void ValidateAndroidLogin(Protocols protocol, BotAppInfo info)
    {
        if (protocol is not (Protocols.AndroidPhone or Protocols.AndroidPad)) return;
        if (info.WtLoginSsoProtocol is not (10 or 12))
            throw new InvalidDataException("AppInfo.WtLoginSsoProtocol 仅支持 10 或 12。");
        if (info.WtLoginPublicKey is not { } key) return;
        try
        {
            var bytes = Convert.FromHexString(key);
            if (bytes.Length != 65 || bytes[0] != 4 || info.WtLoginKeyVersion == 0)
                throw new FormatException();
            using var ec = System.Security.Cryptography.ECDiffieHellman.Create();
            ec.ImportParameters(new System.Security.Cryptography.ECParameters
            {
                Curve = System.Security.Cryptography.ECCurve.NamedCurves.nistP256,
                Q = new System.Security.Cryptography.ECPoint { X = bytes[1..33], Y = bytes[33..65] }
            });
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            throw new InvalidDataException("AppInfo.WtLoginPublicKey 必须是有效的 P-256 公钥，且 KeyVersion 不能为 0。", ex);
        }
    }

    internal static void ValidatePlatform(Protocols protocol, BotAppInfo info)
    {
        var os = (info.Os ?? "").ToLowerInvariant();
        var mismatch = protocol switch
        {
            Protocols.Linux => os != "linux",
            Protocols.MacOs => os is not ("mac" or "macos" or "osx"),
            Protocols.Windows => os is not ("windows" or "win"),
            Protocols.AndroidPhone or Protocols.AndroidPad => os != "android",
            _ => false
        };
        if (mismatch)
            throw new ArgumentException($"客户端平台 {protocol} 与 AppInfo.Os={info.Os} 不一致，请使用同一平台的 AppInfo 和签名服务。");
    }

    internal static string ResolveDeviceName(string? previous)
    {
        if (previous is not null && System.Text.RegularExpressions.Regex.IsMatch(previous, "^Shiro-[0-9]{4,8}$",
                System.Text.RegularExpressions.RegexOptions.NonBacktracking))
            return previous;
        var digits = System.Security.Cryptography.RandomNumberGenerator.GetInt32(4, 9);
        var minimum = (int)Math.Pow(10, digits - 1);
        var number = System.Security.Cryptography.RandomNumberGenerator.GetInt32(minimum, minimum * 10);
        return "Shiro-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static bool HasSession(BotKeystore keystore) =>
        keystore is { Uin: > 0, WLoginSigs: { A2.Length: > 0, D2.Length: > 0 } };

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            var actual = target.Select(x => x.Key)
                             .FirstOrDefault(x => x.Equals(key, StringComparison.OrdinalIgnoreCase))
                         ?? throw new InvalidDataException($"未知 AppInfo 字段: {key}");
            if (value is null) throw new InvalidDataException($"AppInfo.{key} 不能为空。");
            if (target[actual] is JsonObject child && value is JsonObject patch) Merge(child, patch);
            else target[actual] = value.DeepClone();
        }
    }
}
