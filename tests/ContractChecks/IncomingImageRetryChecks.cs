using System.Net;
using System.Reflection;
using Lagrange.Core;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Entity;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entities;
using ShiroBot.LagrangeAdapter;

namespace ContractChecks;

internal static class IncomingImageRetryChecks
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const string ExpectedUrl = "https://image.example.test/media?rkey=fixture";

    internal static async Task RunAsync()
    {
        await CheckAsync(new HttpRequestException(HttpRequestError.ConnectionError, "fixture connection reset",
            new IOException("fixture transport error")), recover: true, expectedCalls: 2);
        await CheckAsync(new HttpRequestException(HttpRequestError.NameResolutionError, "fixture DNS error"),
            recover: false, expectedCalls: 2);
        await CheckAsync(new HttpRequestException("fixture forbidden", null, HttpStatusCode.Forbidden),
            recover: false, expectedCalls: 1);
    }

    private static async Task CheckAsync(HttpRequestException failure, bool recover, int expectedCalls)
    {
        var signer = new FixtureSigner(failure, recover);
        using var bot = BotFactory.Create(new BotConfig { SignProvider = signer });
        var assembly = typeof(BotContext).Assembly;
        var file = new ProtoWire().UInt(6, 64).UInt(7, 48).ToArray();
        var index = new ProtoWire().Bytes(1, file).Text(2, "image-resource-id").ToArray();
        var info = new ProtoWire().Bytes(1, new ProtoWire().Bytes(1, index).ToArray())
            .Bytes(2, new ProtoWire().Bytes(1, new ProtoWire().Text(2, "[图片]").ToArray()).ToArray()).ToArray();
        var image = new ProtoWire().UInt(1, 48).Bytes(2, info).UInt(3, 10).ToArray();
        var richText = new ProtoWire()
            .Bytes(2, new ProtoWire().Bytes(1, new ProtoWire().Text(1, "text beside image").ToArray()).ToArray())
            .Bytes(2, new ProtoWire().Bytes(53, image).ToArray()).ToArray();
        var routing = new ProtoWire().UInt(1, 123).Text(2, "sender").UInt(5, 999).Text(6, "receiver").ToArray();
        var content = new ProtoWire().UInt(1, 166).ToArray();
        var wire = new ProtoWire().Bytes(1, routing).Bytes(2, content)
            .Bytes(3, new ProtoWire().Bytes(1, richText).ToArray()).ToArray();

        var messageType = assembly.GetType("Lagrange.Core.Internal.Packets.Message.CommonMessage", true)!;
        var deserialize = typeof(IncomingImageRetryChecks).GetMethod(nameof(Deserialize),
            BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(messageType);
        var message = deserialize.Invoke(null, [wire])!;
        var packer = Activator.CreateInstance(assembly.GetType("Lagrange.Core.Message.MessagePacker", true)!, bot)!;
        var parsed = await ((Task<BotMessage>)packer.GetType().GetMethod("ParsePushAsync", InstanceFields)!
            .Invoke(packer, [message])!).WaitAsync(TimeSpan.FromSeconds(3));
        var entity = parsed.Entities.OfType<ImageEntity>().Single();
        if (signer.Calls != expectedCalls || entity.FileUuid != "image-resource-id" ||
            entity.FileUrl != (recover ? ExpectedUrl : string.Empty) ||
            parsed.Entities.OfType<TextEntity>().Single().Text != "text beside image")
            throw new Exception("Incoming image retry changed the link, image identity, adjacent text, or attempt count.");
    }

    private static T Deserialize<T>(byte[] wire) where T : Lagrange.Proto.IProtoSerializable<T> =>
        Lagrange.Proto.Serialization.ProtoSerializer.Deserialize<T>(wire);

    private sealed class FixtureSigner(HttpRequestException failure, bool recover) : BotSignProvider
    {
        public int Calls { get; private set; }

        public override bool IsWhiteListCommand(string cmd) => true;

        public override Task<SsoSecureInfo?> GetSecSign(long uin, string cmd, int seq, ReadOnlyMemory<byte> body)
        {
            if (cmd != "OidbSvcTrpcTcp.0x11c5_200")
                throw new InvalidOperationException($"Unexpected fixture command {cmd}.");
            if (++Calls == 1 || !recover) throw failure;

            var info = new ProtoWire().Text(1, "image.example.test").Text(2, "/media").ToArray();
            var download = new ProtoWire().Text(1, "?rkey=fixture").Bytes(3, info).ToArray();
            var reply = new ProtoWire().Bytes(4, new ProtoWire().Bytes(3, download).ToArray()).ToArray();
            var packetContext = typeof(BotContext).GetProperty("PacketContext", InstanceFields)!.GetValue(Context)!;
            var pending = packetContext.GetType().GetField("_pendingTasks", InstanceFields)!.GetValue(packetContext)!;
            var remove = pending.GetType().GetMethods()
                .Single(method => method.Name == "TryRemove" && method.GetParameters().Length == 2);
            object?[] arguments = [seq, null];
            if (!(bool)remove.Invoke(pending, arguments)!) throw new InvalidOperationException("Missing fixture request.");
            using var request = (IDisposable)arguments[1]!;
            var source = request.GetType().GetField("Source", InstanceFields)!.GetValue(request)!;
            source.GetType().GetMethod("SetResult")!.Invoke(source, [new BotSsoPacket(cmd, reply)]);

            // The request has already completed locally; stop packet construction before the QQ socket.
            throw new OperationCanceledException("Fixture request handled.");
        }
    }
}
