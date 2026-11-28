using System.Reflection;
using Lagrange.Core;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entities;
using ManagedCodecs.Containers;
using ShiroBot.LagrangeAdapter;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;

namespace ContractChecks;

internal static class VideoMediaChecks
{
    private static readonly MethodInfo BuildFileInfo = typeof(BotContext).Assembly
        .GetType("Lagrange.Core.Internal.Packets.Service.NTV2RichMedia", throwOnError: true)!
        .GetMethod("BuildFileInfo", BindingFlags.Static | BindingFlags.NonPublic)!;

    internal static async Task Run()
    {
        static Task<BotMessage?> ResolveQuoted(string _) => Task.FromResult<BotMessage?>(null);

        var redUri = FixtureUri("video-red.mp4");
        var blueUri = FixtureUri("video-blue.mp4");
        var red = await LagrangeMapper.ToChainAsync([new VideoSegment(redUri)], ResolveQuoted);
        var blue = new MessageChain();
        await LagrangeMapper.AppendQqSegmentAsync(blue, new QOutgoingVideo(blueUri), ResolveQuoted);
        var redCover = await CheckVideoAsync((VideoEntity)red.Single());
        var blueCover = await CheckVideoAsync((VideoEntity)blue.Single());

        if (redCover.AsSpan().SequenceEqual(blueCover))
            throw new Exception("Distinct videos should generate distinct covers.");

        var redAgain = await LagrangeMapper.ToChainAsync([new VideoSegment(redUri)], ResolveQuoted);
        var redAgainCover = await CheckVideoAsync((VideoEntity)redAgain.Single());
        if (!redCover.AsSpan().SequenceEqual(redAgainCover))
            throw new Exception("A reused H.264 picture changed the cover after decoding a different video.");

        var supplied = new MessageChain();
        await LagrangeMapper.AppendQqSegmentAsync(supplied, new QOutgoingVideo(blueUri)
        {
            ThumbUri = "base64://" + Convert.ToBase64String(redCover)
        }, ResolveQuoted);
        var suppliedCover = await CheckVideoAsync((VideoEntity)supplied.Single());
        if (!redCover.AsSpan().SequenceEqual(suppliedCover))
            throw new Exception("The caller's cover was not retained.");

        var hevc = new MessageChain();
        await LagrangeMapper.AppendQqSegmentAsync(hevc, new QOutgoingVideo(FixtureUri("video-hevc.mp4")), ResolveQuoted);
        await CheckVideoAsync((VideoEntity)hevc.Single(), width: 64, height: 64);

        await CheckBorrowedMp4SamplesAsync();
        await CheckBase64SpoolingAsync();
        CheckRecentMessageCache();
        CheckSilkDurationFromStream();
        Console.WriteLine("Managed video metadata, QQ upload fields, generated H.264/HEVC covers and supplied cover checks passed.");
    }

    private static async Task CheckBorrowedMp4SamplesAsync()
    {
        using var source = typeof(VideoMediaChecks).Assembly.GetManifestResourceStream(
            "ContractChecks.Fixtures.video-red.mp4")!;
        using var demuxer = new Mp4Demuxer(source, leaveOpen: true);
        var expected = demuxer.ReadPackets().Where(packet => packet.StreamIndex == demuxer.Video!.StreamIndex).ToArray();
        int index = 0;
        await foreach (var sample in demuxer.ReadBorrowedVideoPacketsAsync(CancellationToken.None))
        {
            if (index >= expected.Length)
                throw new Exception("The borrowed MP4 path yielded an extra video sample.");
            var original = expected[index++];
            if (sample with { Data = default } != original with { Data = default } ||
                !sample.Data.Span.SequenceEqual(original.Data.Span))
                throw new Exception("Borrowing MP4 payloads changed sample bytes or presentation timing.");
        }
        if (index != expected.Length || index == 0)
            throw new Exception("The borrowed MP4 path dropped video samples.");
    }

    private static async Task CheckBase64SpoolingAsync()
    {
        const string scheme = "base64://";
        var data = new byte[24 * 1024 + 1];
        new Random(58).NextBytes(data);
        var encoded = Convert.ToBase64String(data, Base64FormattingOptions.InsertLineBreaks);
        string temporaryPath;
        await using (var resource = await LagrangeMapper.OpenResourceAsync(scheme + encoded))
        {
            if (resource is not FileStream file || !file.CanSeek || file.Position != 0 || file.Length != data.Length)
                throw new Exception("Base64 media must be spooled to a seekable, rewound temporary file.");
            temporaryPath = file.Name;
            var decoded = new byte[data.Length];
            await file.ReadExactlyAsync(decoded);
            if (!decoded.AsSpan().SequenceEqual(data))
                throw new Exception("Base64 spooling changed decoded bytes across block or whitespace boundaries.");
        }
        if (File.Exists(temporaryPath))
            throw new Exception("Disposing a Base64 resource did not delete the spool file.");

        var blockWithPadding = Convert.ToBase64String(new byte[12 * 1024 - 1]);
        try
        {
            await using var invalid = await LagrangeMapper.OpenResourceAsync(scheme + blockWithPadding + "YQ==");
            throw new Exception("Base64 data was accepted after a padded block.");
        }
        catch (FormatException)
        {
        }
    }

    private static void CheckRecentMessageCache()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var sourceProperty = typeof(RichMediaEntityBase).GetProperty("Stream", flags)!;
        var thumbnailProperty = typeof(VideoEntity).GetProperty("ThumbnailStream", flags)!;
        using var media = new MemoryStream(new byte[1024]);
        using var cover = new MemoryStream(new byte[512]);
        var image = new ImageEntity(media, "fixture");
        var video = new VideoEntity(media, cover) { IsBubble = true, VideoLength = 2 };
        var node = BotMessage.CreateCustomGroup(123, 1, "source", 1, [image]);
        var forward = new MultiMsgEntity([node]);
        typeof(MultiMsgEntity).GetProperty("ResId")!.SetValue(forward, "fixture-forward");
        var message = BotMessage.CreateCustomGroup(123, 1, "source", 1, [image, video, forward]);
        message.Sequence = 23;
        var session = new LagrangeSession();
        session.CacheMessage(message);
        var retained = session.FindMessage(23, 123, true)!;
        if (retained.ClientSequence != message.ClientSequence ||
            retained.Entities[0] is not ImageEntity { Summary: "fixture" } cachedImage ||
            retained.Entities[1] is not VideoEntity { IsBubble: true, VideoLength: 2 } cachedVideo ||
            retained.Entities[2] is not MultiMsgEntity { ResId: "fixture-forward", Messages.Count: 0 } cachedForward ||
            sourceProperty.GetValue(cachedImage) is not null || sourceProperty.GetValue(cachedVideo) is not null ||
            thumbnailProperty.GetValue(cachedVideo) is not null)
            throw new Exception("The recent-message cache retained input media or lost forwarding metadata.");

        var build = typeof(IMessageEntity).GetMethod("Build", flags)!;
        if (((Array)build.Invoke(cachedForward, null)!).Length != 1 ||
            sourceProperty.GetValue(image) is null || thumbnailProperty.GetValue(video) is null ||
            forward.Messages.Count != 1 || !media.CanRead || !cover.CanRead)
            throw new Exception("Making a cache snapshot changed the original message or lost its forwarding card.");

        Parallel.For(1, 4099, index =>
        {
            var item = BotMessage.CreateCustomGroup(321, 1, "source", 1, []);
            item.Sequence = (ulong)index;
            session.CacheMessage(item);
        });
        if (Enumerable.Range(1, 4098).Count(index => session.FindMessage((ulong)index, 321, true) is not null) != 4096 ||
            session.FindMessage(23, 123, true) is not null)
            throw new Exception("Concurrent caching exceeded the recent-message limit or failed to evict the oldest entry.");
    }

    private static void CheckSilkDurationFromStream()
    {
        foreach (bool tencent in new[] { false, true })
        {
            using var stream = new MemoryStream();
            if (tencent) stream.WriteByte(2);
            stream.Write("#!SILK_V3"u8);
            for (var frame = 0; frame < 60; frame++)
                stream.Write(new byte[] { 2, 0, 17, 19 });
            if (!tencent) stream.Write(new byte[] { 255, 255 });
            stream.Position = 0;
            CheckFileInfo(new RecordEntity(stream), 0, 0, 1);
            if (stream.Position != 0) throw new Exception("SILK duration probing did not rewind the audio source.");
        }
    }

    private static string FixtureUri(string name)
    {
        using var fixture = typeof(VideoMediaChecks).Assembly.GetManifestResourceStream($"ContractChecks.Fixtures.{name}")!;
        using var bytes = new MemoryStream();
        fixture.CopyTo(bytes);
        return "base64://" + Convert.ToBase64String(bytes.ToArray());
    }

    private static async Task<byte[]> CheckVideoAsync(VideoEntity video, uint width = 48, uint height = 32)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var file = ((Lazy<Stream>)typeof(VideoEntity).GetProperty("Stream", flags)!.GetValue(video)!).Value;
        using var cover = ((Lazy<Stream>)typeof(VideoEntity).GetProperty("ThumbnailStream", flags)!.GetValue(video)!).Value;
        if (file.Position != 0)
            throw new Exception("The video stream must be rewound for upload.");

        CheckFileInfo(video, width, height, 2);
        CheckFileInfo(new ImageEntity(cover), width, height, 0);
        using var jpeg = new MemoryStream();
        await cover.CopyToAsync(jpeg);
        var bytes = jpeg.ToArray();
        if (!bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xd8 }))
            throw new Exception("The cover is not a JPEG.");
        return bytes;
    }

    private static void CheckFileInfo(RichMediaEntityBase entity, uint width, uint height, uint duration)
    {
        var info = BuildFileInfo.Invoke(null, [entity])!;
        uint Field(string name) => (uint)info.GetType().GetProperty(name)!.GetValue(info)!;
        if (Field("Width") != width || Field("Height") != height || Field("Time") != duration)
            throw new Exception($"Unexpected QQ upload dimensions {Field("Width")}x{Field("Height")} and duration {Field("Time")}.");
    }
}
