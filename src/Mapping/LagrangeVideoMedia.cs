using System.Numerics;
using ManagedCodecs;
using ManagedCodecs.Containers;
using ManagedCodecs.H264;
using ManagedCodecs.Mjpeg;
using ManagedCodecs.Thumbnails;

namespace ShiroBot.LagrangeAdapter;

internal static class LagrangeVideoMedia
{
    private const int CoverSize = 320;
    private const long MaxFramePixels = 16_777_216;
    private static readonly SemaphoreSlim PreparationSlot = new(1, 1);
    // Accessed while holding PreparationSlot. Idle workspaces are eligible for collection.
    private static readonly WeakReference<Picture?> H264CoverPicture = new(null);

    internal static async Task<(Vector2 Size, uint Duration, Stream? Thumbnail)> PrepareAsync(
        Stream source, bool generateThumbnail, CancellationToken cancellationToken)
    {
        await PreparationSlot.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var demuxer = Demuxer.Open(source, leaveOpen: true);
            if (demuxer.Video is not { } video) return default;

            var (width, height) = FrameSize(video);
            var (displayWidth, displayHeight) = VideoProbe.DisplaySize(width, height, video.SampleAspectRatio, video.Rotation);
            var duration = video.Codec is CodecIds.H264 or CodecIds.Mjpeg
                ? VideoProbe.Duration(demuxer, cancellationToken).TotalSeconds
                : 0;
            if (duration <= 0) duration = video.Duration * video.TimeBase.ToDouble();
            var thumbnail = generateThumbnail && displayWidth > 0 && displayHeight > 0
                ? await TryCreateThumbnailAsync(demuxer, video, width, height, cancellationToken).ConfigureAwait(false)
                : null;
            return (new Vector2(displayWidth, displayHeight), DurationSeconds(duration), thumbnail);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or OverflowException)
        {
            return default;
        }
        finally
        {
            try
            {
                source.Position = 0;
            }
            finally
            {
                PreparationSlot.Release();
            }
        }
    }

    private static uint DurationSeconds(double seconds) =>
        double.IsFinite(seconds) && seconds > 0 ? (uint)Math.Min(uint.MaxValue, Math.Ceiling(seconds)) : 0;

    private static async Task<Stream?> TryCreateThumbnailAsync(IDemuxer demuxer, VideoStreamInfo video,
        int width, int height, CancellationToken cancellationToken)
    {
        if (video.Codec is not (CodecIds.H264 or CodecIds.Mjpeg or CodecIds.Hevc)) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        H264Decoder? h264 = null;
        try
        {
            CheckFrameSize(width, height);
            IVideoDecoder decoder = video.Codec switch
            {
                CodecIds.H264 => h264 = CreateH264CoverDecoder(video),
                CodecIds.Hevc => new LagrangeHevcKeyFrameDecoder(video, MaxFramePixels, timeout.Token),
                _ => new MjpegDecoder(video)
            };
            byte[]? damagedCover = null;
            bool firstVideoPacket = true;

            var packets = demuxer is Mp4Demuxer mp4Video
                ? mp4Video.ReadBorrowedVideoPacketsAsync(timeout.Token)
                : demuxer.ReadPacketsAsync(timeout.Token);
            await foreach (var packet in packets.ConfigureAwait(false))
            {
                if (packet.StreamIndex != video.StreamIndex || (packet.Discard && video.Codec == CodecIds.Mjpeg)) continue;
                if (video.Codec == CodecIds.Mjpeg && MjpegDecoder.ImageSize(packet.Data.Span) is { } image)
                    CheckFrameSize(image.Width, image.Height);

                IReadOnlyList<VideoFrame> frames;
                try
                {
                    frames = decoder.Decode(packet);
                }
                catch (InvalidDataException) when (video.Codec != CodecIds.H264)
                {
                    continue;
                }

                if (firstVideoPacket && frames.Count == 0 && h264 is not null &&
                    packet is { IsKeyframe: true, Discard: false } && demuxer is Mp4Demuxer mp4 &&
                    IsFirstPresentedSample(mp4, packet, timeout.Token) && h264.TakeQueuedCoverFrame(packet.Pts) is { } firstFrame)
                {
                    frames = [firstFrame];
                }
                firstVideoPacket = false;

                var cover = RenderFirstFrame(frames, video, ref damagedCover, timeout.Token);
                if (cover is not null) return new MemoryStream(cover, writable: false);
            }

            var finalCover = RenderFirstFrame(decoder.Flush(), video, ref damagedCover, timeout.Token) ?? damagedCover;
            return finalCover is null ? null : new MemoryStream(finalCover, writable: false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException
            or OverflowException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (h264 is not null)
                H264CoverPicture.SetTarget(h264.CoverPictureForReuse);
        }
    }

    private static H264Decoder CreateH264CoverDecoder(VideoStreamInfo video)
    {
        H264CoverPicture.TryGetTarget(out var picture);
        return new H264Decoder(video, picture) { ConcealErrors = true, BorrowFrames = true };
    }

    private static bool IsFirstPresentedSample(Mp4Demuxer demuxer, Packet candidate, CancellationToken cancellationToken)
    {
        // MP4 has presentation timestamps for each sample without reading compressed frames.
        // Leave reordering to the decoder for edits, ties, and leading pictures.
        foreach (var sample in demuxer.ReadPacketInfo())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sample.StreamIndex == candidate.StreamIndex && sample.Sequence != candidate.Sequence &&
                !sample.Discard && sample.Pts <= candidate.Pts)
                return false;
        }
        return true;
    }

    private static (int Width, int Height) FrameSize(VideoStreamInfo video) =>
        video.Codec == CodecIds.H264 && H264Decoder.FrameSize(video) is { } size ? size : (video.Width, video.Height);

    private static void CheckFrameSize(int width, int height)
    {
        if ((long)width * height > MaxFramePixels)
            throw new NotSupportedException($"Video frame {width}x{height} exceeds the cover decoder's pixel limit.");
    }

    private static byte[]? RenderFirstFrame(IReadOnlyList<VideoFrame> frames, VideoStreamInfo video,
        ref byte[]? damagedCover, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var frame in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!frame.IsDamaged) return RenderCover(frame, video);
                damagedCover ??= RenderCover(frame, video);
            }

            return null;
        }
        finally
        {
            foreach (var frame in frames) frame.Dispose();
        }
    }

    private static byte[] RenderCover(VideoFrame frame, VideoStreamInfo video)
    {
        CheckFrameSize(frame.Width, frame.Height);
        var aspect = frame.SampleAspectRatio is { Numerator: > 0, Denominator: > 0 } ratio ? ratio : video.SampleAspectRatio;
        var (displayWidth, displayHeight) = VideoProbe.DisplaySize(frame.Width, frame.Height, aspect, video.Rotation);
        var (frameWidth, frameHeight) = video.Rotation is 90 or 270 ? (frame.Height, frame.Width) : (frame.Width, frame.Height);
        var scale = Math.Min((double)Math.Min(CoverSize, frameWidth) / displayWidth,
            (double)Math.Min(CoverSize, frameHeight) / displayHeight);
        var width = Math.Clamp((int)Math.Round(displayWidth * scale), 1, Math.Min(CoverSize, frameWidth));
        var height = Math.Clamp((int)Math.Round(displayHeight * scale), 1, Math.Min(CoverSize, frameHeight));
        var converter = RgbaConverter.For(frame, video.Codec, null, null);
        var pixels = RgbaImage.Render(frame, width, height, video.Rotation, converter);
        using var jpeg = new MemoryStream();
        Jpeg.Write(jpeg, pixels, width, height, quality: 85);
        return jpeg.ToArray();
    }
}
