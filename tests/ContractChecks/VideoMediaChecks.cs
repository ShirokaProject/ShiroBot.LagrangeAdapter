using System.Reflection;
using Lagrange.Core;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entities;
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

        var supplied = new MessageChain();
        await LagrangeMapper.AppendQqSegmentAsync(supplied, new QOutgoingVideo(blueUri)
        {
            ThumbUri = "base64://" + Convert.ToBase64String(redCover)
        }, ResolveQuoted);
        var suppliedCover = await CheckVideoAsync((VideoEntity)supplied.Single());
        if (!redCover.AsSpan().SequenceEqual(suppliedCover))
            throw new Exception("The caller's cover was not retained.");

        Console.WriteLine("Managed video metadata, QQ upload fields, generated covers and supplied cover checks passed.");
    }

    private static string FixtureUri(string name)
    {
        using var fixture = typeof(VideoMediaChecks).Assembly.GetManifestResourceStream($"ContractChecks.Fixtures.{name}")!;
        using var bytes = new MemoryStream();
        fixture.CopyTo(bytes);
        return "base64://" + Convert.ToBase64String(bytes.ToArray());
    }

    private static async Task<byte[]> CheckVideoAsync(VideoEntity video)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var file = ((Lazy<Stream>)typeof(VideoEntity).GetProperty("Stream", flags)!.GetValue(video)!).Value;
        using var cover = ((Lazy<Stream>)typeof(VideoEntity).GetProperty("ThumbnailStream", flags)!.GetValue(video)!).Value;
        if (file.Position != 0)
            throw new Exception("The video stream must be rewound for upload.");

        CheckFileInfo(video, 48, 32, 2);
        CheckFileInfo(new ImageEntity(cover), 48, 32, 0);
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
