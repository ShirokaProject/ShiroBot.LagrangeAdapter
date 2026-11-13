using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Lagrange.Core;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Entity;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Services;

namespace ContractChecks;

internal static class AndroidAppInfoChecks
{
    private delegate ReadOnlyMemory<byte> BuildFrame(short command, ReadOnlySpan<byte> payload);
    private delegate ReadOnlySpan<byte> ParseFrame(ReadOnlySpan<byte> frame, out ushort command);
    private delegate byte[] Tea(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key);

    internal static void Run(Assembly adapter)
    {
        var options = adapter.GetType("ShiroBot.LagrangeAdapter.LagrangeConnectionOptions")!;
        var resolve = options.GetMethod("ResolveAppInfo", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var fixture = typeof(AndroidAppInfoChecks).Assembly.GetManifestResourceStream(
            "ContractChecks.Fixtures.android-pad-9.3.80-appinfo.json")!;
        var original = JsonNode.Parse(fixture)!.AsObject();
        BotAppInfo Resolve(JsonObject data) => (BotAppInfo)resolve.Invoke(null, [Protocols.AndroidPad, data])!;
        var info = Resolve(original);
        if (info.WtLoginPublicKey != original["WtLoginPublicKey"]!.GetValue<string>() ||
            info.WtLoginKeyVersion != 2 || info.WtLoginSsoProtocol != 10 || info.SubAppId != 537400153 ||
            info.SdkInfo.SdkVersion != "6.0.0.2591" ||
            Convert.ToHexString(info.ApkSignatureMd5) != "A6B745BF24A2C277527716F6F36EB68D")
            throw new Exception("Complete production Android AppInfo was not preserved.");
        var unsigned = original.DeepClone().AsObject();
        unsigned["ApkSignatureMd5"] = new JsonArray(info.ApkSignatureMd5.Select(b => (JsonNode?)JsonValue.Create((int)b)).ToArray());
        if (!Resolve(unsigned).ApkSignatureMd5.SequenceEqual(info.ApkSignatureMd5))
            throw new Exception("Unsigned APK digest changed.");
        var base64 = original.DeepClone().AsObject();
        base64["ApkSignatureMd5"] = Convert.ToBase64String(info.ApkSignatureMd5);
        if (!Resolve(base64).ApkSignatureMd5.SequenceEqual(info.ApkSignatureMd5))
            throw new Exception("Existing base64 APK digest changed.");
        foreach (var (field, value) in new (string, JsonNode)[] {
                     ("WtLoginPublicKey", JsonValue.Create("04" + new string('0', 128))!),
                     ("WtLoginKeyVersion", JsonValue.Create(0)!),
                     ("WtLoginSsoProtocol", JsonValue.Create(99)!),
                     ("ApkSignatureMd5", new JsonArray(-129, 1)),
                     ("UnexpectedField", JsonValue.Create(1)!) })
        {
            var bad = original.DeepClone().AsObject(); bad[field] = value;
            try { Resolve(bad); throw new Exception("Invalid AppInfo accepted: " + field); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { }
        }
        CheckFrames(original, Resolve);
        CheckTransport(info);
        Console.WriteLine("Production Android AppInfo, signed/unsigned/base64 APK digest, P-256 request/reply and SSO 10 checks passed (offline).");
    }

    private static void CheckFrames(JsonObject original, Func<JsonObject, BotAppInfo> resolve)
    {
        using var server = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var data = original.DeepClone().AsObject();
        data["WtLoginPublicKey"] = Convert.ToHexString(Public(server));
        var info = resolve(data);
        using var bot = BotFactory.Create(new BotConfig { Protocol = Protocols.AndroidPad }, info);
        var sdk = typeof(BotContext).Assembly;
        var type = sdk.GetType("Lagrange.Core.Internal.Packets.Login.WtLogin")!;
        var packet = Activator.CreateInstance(type, bot)!;
        var build = type.GetMethod("BuildAndroidPacket", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate<BuildFrame>(packet);
        var parse = type.GetMethod("Parse")!.CreateDelegate<ParseFrame>(packet);
        var teaType = sdk.GetType("Lagrange.Core.Utility.Cryptography.TeaProvider")!;
        var encrypt = teaType.GetMethod("Encrypt", [typeof(ReadOnlySpan<byte>), typeof(ReadOnlySpan<byte>)])!.CreateDelegate<Tea>();
        var decrypt = teaType.GetMethod("Decrypt", [typeof(ReadOnlySpan<byte>), typeof(ReadOnlySpan<byte>)])!.CreateDelegate<Tea>();
        byte[] payload = [0, 2, 0, 1, 0xab, 0xcd];
        var request = build(0x810, payload).ToArray();
        // SDK EcdhCrypt: 0x131, explicit key version and uncompressed P-256 public point.
        if (request[0] != 2 || request[^1] != 3 || U16(request, 1) != request.Length ||
            U16(request, 7) != 1 || request[13] != 3 || request[14] != 135 ||
            BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(16)) != 2 ||
            request[28] != 2 || request[29] != 1 || U16(request, 46) != 0x131 ||
            U16(request, 48) != 2 || U16(request, 50) != 65 || request[52] != 4)
            throw new Exception("Android SDK ECDH request header mismatch.");
        var clientPublic = request.AsSpan(52, 65).ToArray();
        var key = Share(server, clientPublic);
        if (!decrypt(request.AsSpan(117, request.Length - 118), key).SequenceEqual(payload))
            throw new Exception("Platform P-256 could not decrypt Android SDK request.");
        byte[] response = [0, 2, 0, 0, 0, 1, 42];
        var normal = Response(encrypt(response, key), 0);
        if (!parse(normal, out var command).SequenceEqual(response) || command != 0x810)
            throw new Exception("Android ECDH response decoding mismatch.");
        using var rotated = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var innerCipher = encrypt(response, Share(rotated, clientPublic));
        var inner = new byte[2 + 65 + innerCipher.Length];
        BinaryPrimitives.WriteUInt16BigEndian(inner, 65);
        Public(rotated).CopyTo(inner, 2); innerCipher.CopyTo(inner, 67);
        var rotatedResponse = Response(encrypt(inner, request.AsSpan(30, 16)), 1);
        if (!parse(rotatedResponse, out _).SequenceEqual(response))
            throw new Exception("Rotated Android server key response decoding mismatch.");
        var second = build(0x810, payload).ToArray();
        if (second.AsSpan(30, 16).SequenceEqual(request.AsSpan(30, 16)))
            throw new Exception("Android request random key was reused.");
        var qr = build(0x812, payload).ToArray();
        if (qr[14] != 7) throw new Exception("Android trans_emp header mismatch.");

        // The Android key must not change PC's secp192k1/0x102 login framing.
        using var pc = BotFactory.Create(new BotConfig { Protocol = Protocols.MacOs }, info);
        var pcPacket = Activator.CreateInstance(type, pc)!;
        var pcFrame = ((ReadOnlyMemory<byte>)type.GetMethod("BuildTransEmp12")!.Invoke(pcPacket, null)!).ToArray();
        if (U16(pcFrame, 7) != 0 || U16(pcFrame, 46) != 0x102 || U16(pcFrame, 48) != 25)
            throw new Exception("PC login framing changed when Android key was present.");
    }

    private static byte[] Public(ECDiffieHellman ec)
    {
        var q = ec.ExportParameters(false).Q;
        return [4, ..q.X!, ..q.Y!];
    }

    private static byte[] Share(ECDiffieHellman server, byte[] client)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportParameters(new ECParameters { Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = client[1..33], Y = client[33..65] } });
        return MD5.HashData(server.DeriveRawSecretAgreement(peer.PublicKey).AsSpan(0, 16));
    }

    private static byte[] Response(byte[] cipher, ushort flag)
    {
        var result = new byte[17 + cipher.Length];
        result[0] = 2; result[^1] = 3;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(1), (ushort)result.Length);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(3), 8001);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(5), 0x810);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(13), flag);
        cipher.CopyTo(result, 16);
        return result;
    }

    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));

    private static void CheckTransport(BotAppInfo info)
    {
        var sdk = typeof(BotContext).Assembly;
        var sso = sdk.GetType("Lagrange.Core.Internal.Packets.Struct.SsoPacker")!;
        var service = sdk.GetType("Lagrange.Core.Internal.Packets.Struct.ServicePacker")!;
        // Bridge the SDK's internal ref-struct packet without adding test APIs to it.
        var method = new DynamicMethod("PackTransport", typeof(ReadOnlyMemory<byte>),
            [typeof(BotContext), typeof(BotSsoPacket), typeof(ServiceAttribute), typeof(int)], typeof(AndroidAppInfoChecks), true);
        var il = method.GetILGenerator();
        var buildSso = sso.GetMethod("BuildProtocol12")!;
        var local = il.DeclareLocal(buildSso.ReturnType);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Newobj, sso.GetConstructor([typeof(BotContext)])!);
        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Callvirt, buildSso); il.Emit(OpCodes.Stloc, local);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Newobj, service.GetConstructor([typeof(BotContext)])!);
        il.Emit(OpCodes.Ldloc, local); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Callvirt, service.GetMethod("BuildProtocol12")!); il.Emit(OpCodes.Ret);
        var pack = method.CreateDelegate<Func<BotContext, BotSsoPacket, ServiceAttribute, int, ReadOnlyMemory<byte>>>();
        foreach (var protocol in new[] { Protocols.AndroidPad, Protocols.MacOs })
        foreach (var command in new[] { "wtlogin.login", "OidbSvcTrpcTcp.0xfe5_2" })
        {
            using var bot = BotFactory.Create(new BotConfig { Protocol = protocol }, info);
            var legacy = protocol == Protocols.AndroidPad && command.StartsWith("wtlogin.");
            var frame = pack(bot, new BotSsoPacket(command, new byte[] { 1, 2 }),
                new ServiceAttribute(command, RequestType.D2Auth, EncryptType.NoEncrypt), legacy ? 10 : 12).ToArray();
            if (BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(4)) != (legacy ? 10 : 12))
                throw new Exception("Wrong Android SSO transport protocol.");
            var uinLength = BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(14));
            var head = frame.AsSpan(14 + uinLength + 4);
            if (BinaryPrimitives.ReadInt32BigEndian(head[8..]) != (legacy ? info.SubAppId : 2052) ||
                head[12] != (legacy ? 1 : 2) || head[22] != (legacy ? 1 : 0))
                throw new Exception("Android SSO 10 reserved header mismatch or PC regression.");
        }
    }
}
