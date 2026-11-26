using System.Reflection;
using System.Text.Json;
using Lagrange.Core;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entities;
using ShiroBot.LagrangeAdapter;
using ShiroBot.LagrangeAdapter.Services;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;

namespace ContractChecks;

internal static class QqModelChecks
{
    internal static async Task Run()
    {
        CheckInterface<IQFileApi, LagrangeQqFileApi>(nameof(IQFileApi.ForwardGroupFileToGroupAsync),
            nameof(IQFileApi.ForwardGroupFileToFriendAsync), nameof(IQFileApi.UploadFlashFilesAsync),
            nameof(IQFileApi.DownloadFlashFilesAsync), nameof(IQFileApi.GetFlashFileSetAsync),
            nameof(IQFileApi.ReshareFlashFilesAsync));
        CheckInterface<IQSystemApi, LagrangeQqSystemApi>(nameof(IQSystemApi.GetOnlineClientsAsync));
        CheckInterface<IQMessageApi, LagrangeQqMessageApi>(nameof(IQMessageApi.ForwardSingleMessageAsync));
        await CheckFileCallsAsync();
        await CheckOnlinePushAsync();
        await CheckPrivateHistoryDirectionAsync();
        CheckSegments();
        Console.WriteLine("QQ Model 1.2 interface dispatch, file OIDB payloads, online device push and new incoming segments passed (offline).");
    }

    private static void CheckInterface<TInterface, TImplementation>(params string[] methods)
    {
        var map = typeof(TImplementation).GetInterfaceMap(typeof(TInterface));
        foreach (var method in methods)
        {
            var index = Array.FindIndex(map.InterfaceMethods, candidate => candidate.Name == method);
            if (index < 0 || map.TargetMethods[index].DeclaringType != typeof(TImplementation))
                throw new Exception($"QQ Model method uses a default implementation: {method}");
        }
    }

    private static async Task CheckFileCallsAsync()
    {
        var request = new List<ProtoWire.Field>();
        var command = "";
        var reply = new ProtoWire();
        var transport = new LagrangeProtocol((cmd, envelope, _) =>
        {
            command = cmd;
            request = ProtoWire.Child(ProtoWire.Parse(envelope), 4);
            return Task.FromResult(new ProtoWire().Message(4, reply).ToArray());
        });

        reply = new ProtoWire().Message(3, new ProtoWire().Text(4, "/copied").UInt(5, 102));
        var group = await GroupFiles.CopyAsync(transport, 123, "/source", 103, 456, false, default);
        var copy = ProtoWire.Child(request, 3);
        if (group != new GroupFiles.CopiedFile("/copied", 102) || command != "OidbSvcTrpcTcp.0x6d9_2" ||
            ProtoWire.Number(copy, 3) != 103 || ProtoWire.Number(copy, 5) != 102 || ProtoWire.Number(copy, 6) != 456)
            throw new Exception("Group copy did not preserve the source bus or target.");
        reply = new ProtoWire().Message(3, new ProtoWire().Text(4, "/private"));
        var friend = await GroupFiles.CopyAsync(transport, 123, "/source", 103, 789, true, default);
        if (friend != new GroupFiles.CopiedFile("/private", 3) || ProtoWire.Number(ProtoWire.Child(request, 3), 5) != 3)
            throw new Exception("Private copy did not select the private file bus.");

        var flash = new LagrangeFlashProtocol(transport);
        reply = new ProtoWire().Text(1, "set-123");
        if (await flash.ResolveIdAsync("https://qfile.qq.com/q/f15k/?from=share", default) != "set-123" ||
            command != "OidbSvcTrpcTcp.0x93eb_1" || ProtoWire.String(request, 1) != "f15k")
            throw new Exception("Flash link did not resolve the share code.");

        var file = new LagrangeFlashProtocol.Entry("uuid", "a.dat", 100, new string('a', 40), new string('b', 32), "", 11);
        reply = new ProtoWire().Message(1, new ProtoWire().Text(3, "success"))
            .Message(2, new ProtoWire().Message(6, new ProtoWire()
                .Message(1, new ProtoWire().Message(1, new ProtoWire().Text(2, "ticket").UInt(4, 11).UInt(5, 22)))));
        var ticket = await flash.PreflightAsync(file, default);
        var scene = ProtoWire.Child(ProtoWire.Child(request, 1), 2);
        if (ticket.Token != "ticket" || ticket.Time != 11 || ticket.Ttl != 22 ||
            ProtoWire.Number(scene, 102) != 4 || ProtoWire.Number(scene, 103) != 22 || ProtoWire.Number(scene, 200) != 5)
            throw new Exception("Flash upload preflight or token decoding changed.");
        reply = new ProtoWire().Message(1, new ProtoWire().UInt(3, 0));
        await flash.CommitAsync("set-123", file, ticket, default);
        var commit = ProtoWire.Child(request, 12);
        var target = ProtoWire.Child(commit, 10);
        if (command != "OidbSvcTrpcTcp.0x12a9_103" || ProtoWire.String(target, 1) != "set-123" ||
            ProtoWire.String(target, 2) != "set-123" || ProtoWire.String(target, 3) != "uuid" ||
            ProtoWire.String(ProtoWire.Child(commit, 1), 2) != "ticket")
            throw new Exception("Flash commit did not bind the uploaded file to the share.");
        reply = new ProtoWire().Message(1, new ProtoWire().Text(3, "success"))
            .Message(3, new ProtoWire().Text(1, "&rkey=key")
                .Message(3, new ProtoWire().Text(1, "multimedia.qfile.qq.com").Text(2, "/download?appid=14901")));
        var url = await flash.GetDownloadUrlAsync("set-123", file, default);
        var downloadRequest = ProtoWire.Child(request, 3);
        var downloadInfo = ProtoWire.Child(ProtoWire.Child(downloadRequest, 1), 1);
        if (url.Host != "multimedia.qfile.qq.com" || url.Query != "?appid=14901&rkey=key" ||
            ProtoWire.String(downloadInfo, 3) != file.Sha1 ||
            ProtoWire.String(ProtoWire.Child(ProtoWire.Child(downloadRequest, 2), 10), 1) != "set-123")
            throw new Exception("Flash download did not encode the SHA-1 in hexadecimal UTF-8 or retained an invalid target.");
        reply = new ProtoWire().Message(1, new ProtoWire().UInt(3, 100000));
        try
        {
            await flash.CommitAsync("set-123", file, ticket, default);
            throw new Exception("Flash commit ignored the media error.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("100000", StringComparison.Ordinal)) { }

        var groupTarget = new ProtoWire().UInt(1, 2).Message(3, new ProtoWire().UInt(1, 123));
        reply = new ProtoWire().Message(1, new ProtoWire().Message(3, groupTarget));
        await flash.SendCardAsync("set-123", 123, null, default);
        var sentTo = ProtoWire.Child(request, 1);
        if (command != "OidbSvcTrpcTcp.0x93d7_1" || ProtoWire.String(request, 2) != "set-123" ||
            ProtoWire.Number(sentTo, 1) != 2 || ProtoWire.Number(ProtoWire.Child(sentTo, 3), 1) != 123)
            throw new Exception("Flash card did not target the group with its uploaded file set.");
        reply = new ProtoWire().Message(1, new ProtoWire().UInt(1, 403).Text(2, "target rejected").Message(3, groupTarget));
        try
        {
            await flash.SendCardAsync("set-123", 123, null, default);
            throw new Exception("Flash card ignored the target's send status.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("403", StringComparison.Ordinal)) { }
    }

    private static async Task CheckOnlinePushAsync()
    {
        using var bot = BotFactory.Create(new BotConfig());
        typeof(BotContext).GetProperty(nameof(BotContext.IsOnline))!.SetValue(bot, true);
        var provider = new LagrangeQqSystemApi(new LagrangeSession { ActiveContext = bot });
        var waiting = provider.GetOnlineClientsAsync();
        var push = typeof(BotContext).Assembly.GetType("Lagrange.Core.Internal.Services.System.PushParamsService")!;
        var handler = Activator.CreateInstance(push)!;
        var parse = push.GetMethod("Parse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Receive(ProtoWire wire) => parse.Invoke(handler, [(ReadOnlyMemory<byte>)wire.ToArray(), bot]);
        Receive(new ProtoWire().Message(4, new ProtoWire().UInt(1, 30504).Text(5, "mobile").Text(7, "Phone"))
            .Text(7, "additional QQ client status text"));
        if ((await waiting).Single() is not { AppId: 30504, DeviceKind: "mobile", DeviceName: "Phone" })
            throw new Exception("Online device push did not fill the SDK model.");
        Receive(new ProtoWire());
        if ((await provider.GetOnlineClientsAsync()).Count != 0)
            throw new Exception("The empty device push did not replace the old device snapshot.");
    }

    private static async Task CheckPrivateHistoryDirectionAsync()
    {
        using var bot = BotFactory.Create(new BotConfig());
        var core = typeof(BotContext).Assembly;
        var requestType = core.GetType("Lagrange.Core.Internal.Events.Message.GetRoamMessageEventReq")!;
        var serviceType = core.GetType("Lagrange.Core.Internal.Services.Message.GetRoamMessageService")!;
        var service = Activator.CreateInstance(serviceType)!;
        var request = Activator.CreateInstance(requestType, "friend-uid", 1_700_000_000u, 20u);
        var build = serviceType.GetMethod("Build", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var data = await (ValueTask<ReadOnlyMemory<byte>>)build.Invoke(service, [request, bot])!;
        var fields = ProtoWire.Parse(data.Span);
        if (ProtoWire.String(fields, 1) != "friend-uid" || ProtoWire.Number(fields, 2) != 1_700_000_000u ||
            ProtoWire.Number(fields, 4) != 20u || ProtoWire.Number(fields, 5) != 1u)
            throw new Exception("Private history did not request older messages from the supplied timestamp.");
    }

    private static void CheckSegments()
    {
        var card = JsonSerializer.Serialize(new
        {
            busId = "FlashTransfer",
            attributes = new
            {
                attributes = new[]
                {
                    new
                    {
                        viewId = "file",
                        schema = "mqqapi://qfile/open?fileset_id=set-123&scene_type=2",
                        attributes = new[] { new { viewId = "title", text = "文件" } }
                    }
                }
            }
        });
        var content = $"[闪传](mqqapi://markdown/node?json={Uri.EscapeDataString(card)})";
        var core = typeof(MarkdownEntity).Assembly;
        var elemType = core.GetType("Lagrange.Core.Internal.Packets.Message.Elem", throwOnError: true)!;
        var commonElemType = core.GetType("Lagrange.Core.Internal.Packets.Message.CommonElem", throwOnError: true)!;
        var commonElem = Activator.CreateInstance(commonElemType)!;
        commonElemType.GetProperty("ServiceType")!.SetValue(commonElem, 45u);
        commonElemType.GetProperty("BusinessType")!.SetValue(commonElem, 3u);
        commonElemType.GetProperty("PbElem")!.SetValue(commonElem, (ReadOnlyMemory<byte>)new ProtoWire().Text(1, content).ToArray());
        var elem = Activator.CreateInstance(elemType)!;
        elemType.GetProperty("CommonElem")!.SetValue(elem, commonElem);
        var parse = typeof(IMessageEntity).GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var markdown = parse.Invoke(new MarkdownEntity(), [null, elem]) as MarkdownEntity
            ?? throw new Exception("QQ business type 3 lost its FlashTransfer Markdown card.");
        var mapped = LagrangeMapper.ToQqSegments([markdown, new MarkdownEntity("[闪传](bad)")]);
        if (mapped[0] is not QIncomingFlashFile { FileSetId: "set-123", SceneType: 2, Title: "文件" } ||
            mapped[1] is not QIncomingText ||
            LagrangeMapper.ToSegments([markdown]).Single() is not RawSegment { Payload: QIncomingFlashFile })
            throw new Exception("Flash Markdown card segment mapping failed.");
        var video = new VideoEntity { IsBubble = true, VideoSize = new(480, 640), VideoLength = 3 };
        if (LagrangeMapper.ToQqSegments([video]).Single() is not QIncomingBubbleVideo { Width: 480, Height: 640 } bubble ||
            bubble.Duration != TimeSpan.FromSeconds(3))
            throw new Exception("Bubble video was mapped as ordinary video or lost its dimensions.");
    }
}
