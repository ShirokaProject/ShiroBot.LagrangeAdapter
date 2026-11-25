using System.Reflection;
using System.Text.Json.Nodes;
using Lagrange.Core;
using Lagrange.Core.Events.EventArgs;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entities;
using ShiroBot.LagrangeAdapter;
using ShiroBot.Model.QQ;

namespace ContractChecks;

internal static class ParityChecks
{
    internal static async Task Run()
    {
        ReaderBoundsChecks();
        WriterBoundsChecks();
        await ForwardMediaPreparationChecks();
        // Known protobuf wire fixtures, including unknown fixed fields and malformed lengths.
        var wire = new ProtoWire().UInt(1, 150).Text(2, "A").ToArray();
        Equal(wire, "089601120141");
        if (ProtoWire.Number(ProtoWire.Parse(wire), 1) != 150) throw new Exception("Varint decode failed.");
        foreach (var broken in new[] { "00", "08", "1204aa", "08ffffffffffffffffffff02", "0b" })
        {
            try { ProtoWire.Parse(Convert.FromHexString(broken)); throw new Exception("Malformed protobuf accepted."); }
            catch (InvalidDataException) { }
        }
        var sent = new List<(string Command, byte[] Body)>();
        var reply = new ProtoWire().UInt(1, 0x7e5).UInt(2, 104).UInt(3, 0).Bytes(4, []).ToArray();
        var protocol = new LagrangeProtocol((command, body, _) => { sent.Add((command, body)); return Task.FromResult(reply); });
        await protocol.Like("u", 3, default);
        if (sent[^1].Command != "OidbSvcTrpcTcp.0x7e5_104") throw new Exception("Like command mismatch.");
        Equal(ProtoWire.Data(ProtoWire.Parse(sent[^1].Body), 4), "5a017560476803");
        await protocol.Profile(123, 20002, "N", default);
        Equal(ProtoWire.Data(ProtoWire.Parse(sent[^1].Body), 4), "087b120708a29c0112014e");
        await protocol.FriendRequest("self", "u", true, false, default);
        Equal(ProtoWire.Data(ProtoWire.Parse(sent[^1].Body), 4), "0803120175");
        await protocol.FriendRequest("s", "u", true, true, default);
        Equal(ProtoWire.Data(ProtoWire.Parse(sent[^1].Body), 4), "0a0173120175");
        await protocol.SetAdmin(123, "u", true, default);
        Equal(ProtoWire.Data(ProtoWire.Parse(sent[^1].Body), 4), "087b1201751801");
        await protocol.PersistFile(123, "f", default);
        var envelope = ProtoWire.Parse(sent[^1].Body);
        if (ProtoWire.Number(envelope, 12) != 1) throw new Exception("Legacy OIDB flag missing.");
        Equal(ProtoWire.Data(envelope, 4), "0a07087b1866220166");
        await protocol.Invitation(123, 99, false, default);
        Equal(ProtoWire.Data(ProtoWire.Parse(sent[^1].Body), 4), "0802120808631002187b2200");
        reply = new ProtoWire().UInt(3, 154).Text(5, "denied").ToArray();
        try { await protocol.Like("u", 1, default); throw new Exception("OIDB rejection reported success."); }
        catch (InvalidOperationException e) when (e.Message.Contains("154")) { }
        reply = new ProtoWire().Bytes(4, new ProtoWire().Message(5, new ProtoWire().UInt(1, 9)).ToArray()).ToArray();
        try { await protocol.RenameFile(123, "f", "/", "n", default); throw new Exception("Inner file error ignored."); }
        catch (InvalidOperationException e) when (e.Message.Contains("9")) { }
        reply = new ProtoWire().Bytes(4, new ProtoWire().Message(14, new ProtoWire().Message(30,
            new ProtoWire().Text(20, "files.example.test").UInt(40, 80).Text(50, "/download?a=1%2F2"))).ToArray()).ToArray();
        var url = await protocol.PrivateFile("u", "f", "h", default);
        if (url != "http://files.example.test/download?a=1%2F2&isthumb=0") throw new Exception("Private file URL mangled.");
        reply = new ProtoWire().Message(4, new ProtoWire().Text(1, "a/b").Text(3, "b")).ToArray();
        if ((await protocol.Faces(123, default)).Single() != "https://p.qpic.cn/b/123/a%2Fb/0") throw new Exception("Face URL mapping failed.");
        await protocol.Read(123, null, 42, 0, default);
        Equal(sent[^1].Body, "0a04087b102a");
        await protocol.Read(123, "u", 42, 10, default);
        Equal(sent[^1].Body, "1207120175180a202a");
        reply = new ProtoWire().Bytes(4, new ProtoWire().Message(1, new ProtoWire().UInt(1, 7)).ToArray()).ToArray();
        try { await protocol.PersistFile(123, "f", default); throw new Exception("Persist file inner error ignored."); }
        catch (InvalidOperationException e) when (e.Message.Contains("7")) { }
        using (var bot = Lagrange.Core.Common.Interface.BotFactory.Create(new Lagrange.Core.Common.BotConfig { Protocol = Lagrange.Core.Common.Protocols.AndroidPad }))
        {
            IQSystemApi system = new ShiroBot.LagrangeAdapter.Services.LagrangeQqSystemApi(new LagrangeSession { ActiveContext = bot });
            var info = await system.GetImplInfoAsync();
            if (info.QqProtocolType != "android_pad" || info.QqProtocolVersion != bot.AppInfo.PtVersion) throw new Exception("Android implementation info is not Yogurt compatible.");
            await ColdCachePrivateMessage(bot);
        }
        var selfContact = new Lagrange.Core.Common.Entity.BotFriend(999, "", "self", "", "", "", new(0, "", 0, 0));
        var peerContact = new Lagrange.Core.Common.Entity.BotFriend(123, "", "peer", "", "", "", new(0, "", 0, 0));
        var messageCtor = typeof(Lagrange.Core.Message.BotMessage).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null,
            [typeof(Lagrange.Core.Common.Entity.BotContact), typeof(Lagrange.Core.Common.Entity.BotContact), typeof(long)], null)!;
        var sentFile = (Lagrange.Core.Message.BotMessage)messageCtor.Invoke([selfContact, peerContact, 1700000000L]);
        var receivedFile = (Lagrange.Core.Message.BotMessage)messageCtor.Invoke([peerContact, selfContact, 1700000000L]);
        foreach (var (message, isSelf) in new[] { (sentFile, true), (receivedFile, false) })
        {
            var upload = ShiroBot.LagrangeAdapter.Services.LagrangeEventService.PrivateFileNotice(message, new PrivateFileEntity { FileId = "f" }, "999");
            if (upload.Channel?.Id != "123" || upload.Raw is not QFriendFileUpload { UserId: "123", FileId: "f" } file || file.IsSelf != isSelf)
                throw new Exception("Private file event does not identify the conversation peer.");
        }
        var announcements = LagrangeGroupWeb.ParseAnnouncements(JsonNode.Parse("{\"feeds\":[{\"fid\":\"f\",\"u\":123,\"pubt\":1700000000,\"msg\":{\"text\":\"a&amp;b\"}}]}")!.AsObject(), "456");
        if (announcements.Single().Content != "a&b" || announcements.Single().UserId != "123") throw new Exception("Announcement mapping failed.");
        var essence = LagrangeGroupWeb.ParseEssence(JsonNode.Parse("{\"data\":{\"is_end\":true,\"msg_list\":[{\"msg_seq\":42,\"sender_time\":1700000000,\"msg_content\":[{\"msg_type\":1,\"text\":\"test\"},{\"msg_type\":2,\"face_index\":14}]}]}}")!.AsObject(), "456");
        if (!essence.IsEnd || essence.Messages.Single().Segments.Count != 2) throw new Exception("Essence pagination/segments failed.");
        if (LagrangeGroupWeb.Csrf("test") != "2090756197") throw new Exception("CSRF hash failed.");
        await NoticeChecks();
        var face = new FaceEntity(14);
        var build = typeof(IMessageEntity).GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var built = (Array)build.Invoke(face, null)!;
        var elem = built.GetValue(0)!;
        var serialize = typeof(BotContext).Assembly.GetType("Lagrange.Core.Utility.ProtoHelper")!.GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Single(m => m.Name == "Serialize" && m.GetParameters().Length == 1).MakeGenericMethod(elem.GetType());
        Equal(((ReadOnlyMemory<byte>)serialize.Invoke(null, [elem])!).ToArray(), "1202080e");
        var chain = await LagrangeMapper.ToChainAsync([new ShiroBot.SDK.Models.EmojiSegment("14")], _ => Task.FromResult<Lagrange.Core.Message.BotMessage?>(null));
        if (chain.Single() is not FaceEntity { FaceId: 14 }) throw new Exception("Generic emoji send mapping failed.");
        Console.WriteLine("Yogurt parity wire fixtures, protocol rejections, files, Web mappings, event mapping and actual FaceEntity encoding passed (offline).");
    }
    private static async Task ColdCachePrivateMessage(BotContext bot)
    {
        var assembly = typeof(BotContext).Assembly;
        object Packet(string name) => Activator.CreateInstance(
            assembly.GetType($"Lagrange.Core.Internal.Packets.Message.{name}", throwOnError: true)!)!;
        static void Set(object target, string name, object value) =>
            target.GetType().GetProperty(name)!.SetValue(target, value);

        var routing = Packet("RoutingHead");
        Set(routing, "FromUin", 123L);
        Set(routing, "FromUid", "sender");
        Set(routing, "ToUin", 999L);
        Set(routing, "ToUid", "receiver");
        var content = Packet("ContentHead");
        Set(content, "Type", 166);
        var message = Packet("CommonMessage");
        Set(message, "RoutingHead", routing);
        Set(message, "ContentHead", content);

        var packer = Activator.CreateInstance(
            assembly.GetType("Lagrange.Core.Message.MessagePacker", throwOnError: true)!, bot)!;
        var parsed = await (Task<Lagrange.Core.Message.BotMessage>)packer.GetType()
            .GetMethod("Parse")!.Invoke(packer, [message])!;
        if (parsed.Contact.Uin != 123 || parsed.Contact.Uid != "sender" ||
            parsed.Receiver.Uin != 999 || parsed.Receiver.Uid != "receiver")
            throw new Exception("Private message routing must work with an empty friend cache.");
        var mapped = LagrangeMapper.ToMessageEvent(parsed, "999");
        if (mapped.Channel?.Id != "123" || mapped.Raw is not QFriendMessage { SenderId: "123" })
            throw new Exception("Private message mapping must work without cached friend metadata.");

        var messaging = Activator.CreateInstance(
            assembly.GetType("Lagrange.Core.Internal.Logic.MessagingLogic", throwOnError: true)!, bot)!;
        var replyTarget = await (Task<Lagrange.Core.Common.Entity.BotFriend>)messaging.GetType()
            .GetMethod("ResolvePrivateMessageContact", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(messaging, [123L])!;
        if (replyTarget.Uid != "sender")
            throw new Exception("A private reply must reuse the UID learned from the inbound message.");
    }

    private static async Task ForwardMediaPreparationChecks()
    {
        using var bot = BotFactory.Create(new Lagrange.Core.Common.BotConfig());
        var image = new ForwardProbeImage();
        var node = BotMessage.CreateCustomGroup(0, 123, "node", 1, [image]);
        var nested = new MultiMsgEntity([node]);
        var forward = new MultiMsgEntity([BotMessage.CreateCustomGroup(0, 456, "parent", 1, [nested])]);
        var envelope = BotMessage.CreateCustomGroup(789, 456, "sender", 2, [forward]);
        try
        {
            await forward.Preprocess(bot, envelope);
            throw new Exception("Forwarded media was not preprocessed before the long-message request.");
        }
        catch (StopBeforeForwardUpload)
        {
            if (!ReferenceEquals(image.Envelope, envelope))
                throw new Exception("Forwarded media must use the enclosing destination, including nested forwards.");
        }
    }

    private sealed class ForwardProbeImage() : ImageEntity(Stream.Null)
    {
        public BotMessage? Envelope { get; private set; }

        public override Task Preprocess(BotContext context, BotMessage message)
        {
            Envelope = message;
            throw new StopBeforeForwardUpload();
        }
    }

    private sealed class StopBeforeForwardUpload : Exception;

    private static void ReaderBoundsChecks()
    {
        foreach (var hex in new[] { "", "80", "808080808080808080", "ffffffffffffffffff02" })
        {
            var reader = new Lagrange.Proto.Primitives.ProtoReader(Convert.FromHexString(hex));
            try { reader.DecodeVarIntUnsafe<ulong>(); throw new Exception("Truncated/overflowing native varint accepted."); }
            catch (InvalidDataException) { }
        }
        var signed = new Lagrange.Proto.Primitives.ProtoReader(Convert.FromHexString("ffffffffffffffffff01"));
        if (signed.DecodeVarIntUnsafe<int>() != -1 || !signed.IsCompleted) throw new Exception("Sign-extended protobuf int32 changed.");
        var badLength = new Lagrange.Proto.Primitives.ProtoReader(Convert.FromHexString("ffffffff0f"));
        try { badLength.SkipField(Lagrange.Proto.Serialization.WireType.LengthDelimited); throw new Exception("Negative protobuf length accepted."); }
        catch (ArgumentOutOfRangeException) { }
        var random = new Random(19380);
        for (var i = 0; i < 20000; i++)
        {
            var data = new byte[random.Next(65)]; random.NextBytes(data);
            var reader = new Lagrange.Proto.Primitives.ProtoReader(data);
            try { while (!reader.IsCompleted) reader.DecodeVarIntUnsafe<ulong>(); }
            catch (InvalidDataException) { }
        }
        Console.WriteLine("Bounded protobuf tail/skip, signed int32, overflowing lengths and 20,000 malformed-input checks passed.");
    }

    private static void WriterBoundsChecks()
    {
        foreach (var encode in new Action<Lagrange.Proto.Primitives.ProtoWriter>[]
        {
            writer => writer.EncodeVarInt(300UL),
            writer => writer.EncodeTwo32VarIntUnsafe(300u, 1u)
        })
        {
            var output = new GuardedProtoBuffer();
            using var writer = new Lagrange.Proto.Primitives.ProtoWriter(output);
            writer.WriteRawBytes(new byte[54]);
            encode(writer);
            writer.Flush();
            if (output.Bytes.AsSpan(64).ContainsAnyExcept((byte)0xa5))
                throw new Exception("Protobuf varint writer crossed the destination span boundary.");
            if (output.Bytes[54] != 0xac || output.Bytes[55] != 0x02)
                throw new Exception("Protobuf varint changed at the end of a bounded span.");
        }

        var signed = new System.Buffers.ArrayBufferWriter<byte>();
        using var signedWriter = new Lagrange.Proto.Primitives.ProtoWriter(signed);
        signedWriter.EncodeVarInt(-1);
        signedWriter.Flush();
        if (!signed.WrittenSpan.SequenceEqual(Convert.FromHexString("ffffffffffffffffff01")))
            throw new Exception("Protobuf writer lost the sign extension for negative int32.");
    }

    private sealed class GuardedProtoBuffer : System.Buffers.IBufferWriter<byte>
    {
        public byte[] Bytes { get; } = Enumerable.Repeat((byte)0xa5, 80).ToArray();
        public void Advance(int count) { }
        public Memory<byte> GetMemory(int sizeHint = 0) => sizeHint <= 64 ? Bytes.AsMemory(0, 64)
            : throw new InvalidOperationException("Unexpected protobuf buffer growth.");
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }

    private static async Task NoticeChecks()
    {
        Task<string> Resolve(string uid) => Task.FromResult(uid == "u" ? "123" : "456");
        var pin = new ProtoWire().Message(1, new ProtoWire().Message(20, new ProtoWire().Message(1,
            new ProtoWire().UInt(2, 10).Message(400, new ProtoWire().Bytes(2, [1])))));
        if ((await LagrangeNoticeMapper.Map(new BotExtendedNoticeEvent(528, 39, 0, pin.ToArray()), "999", Resolve))?.Raw
            is not QPeerPinChange { Scene: QMessageScene.Group, PeerId: "10", IsPinned: true }) throw new Exception("Group pin notice mapping failed.");
        var unpin = new ProtoWire().Message(1, new ProtoWire().Message(20, new ProtoWire().Message(1, new ProtoWire().Text(1, "u"))));
        var unpinEvent = await LagrangeNoticeMapper.Map(new BotExtendedNoticeEvent(528, 39, 0, unpin.ToArray()), "999", Resolve);
        if (unpinEvent?.Raw is not QPeerPinChange { PeerId: "123", IsPinned: false } || unpinEvent.Channel?.Id != "123") throw new Exception("Friend unpin notice mapping failed.");
        var mute = new ProtoWire().UInt(1, 10).Text(4, "op").Message(5, new ProtoWire().Message(3, new ProtoWire().Text(1, "u").UInt(2, 60)));
        var result = await LagrangeNoticeMapper.Map(new BotExtendedNoticeEvent(732, 12, 0, mute.ToArray()), "999", Resolve);
        if (result?.Raw is not QGroupMute { UserId: "123", OperatorId: "456" } m || m.Duration != TimeSpan.FromSeconds(60)) throw new Exception("Mute notice mapping failed.");
        var admin = new ProtoWire().UInt(1, 10).Message(4, new ProtoWire().Message(2, new ProtoWire().Text(1, "u")));
        if ((await LagrangeNoticeMapper.Map(new BotExtendedNoticeEvent(44, 0, 0, admin.ToArray()), "999", Resolve))?.Raw is not QGroupAdminChange { IsSet: true, UserId: "123" }) throw new Exception("Admin notice mapping failed.");
        var inner = new ProtoWire().UInt(13, 12).UInt(4, 10).Text(21, "op").Message(5, new ProtoWire().Text(2, "name")).ToArray();
        var wrapped = new byte[inner.Length + 7];System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(wrapped, 10);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(wrapped.AsSpan(5), (ushort)inner.Length);inner.CopyTo(wrapped, 7);
        if ((await LagrangeNoticeMapper.Map(new BotExtendedNoticeEvent(732, 16, 0, wrapped), "999", Resolve))?.Raw is not QGroupNameChange { NewGroupName: "name", OperatorId: "456" }) throw new Exception("Wrapped group notice mapping failed.");
    }
    private static void Equal(byte[] actual, string expected)
    {
        if (!actual.SequenceEqual(Convert.FromHexString(expected))) throw new Exception($"Wire fixture mismatch: {Convert.ToHexString(actual)} != {expected}");
    }
}
