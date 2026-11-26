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

    public static (Vector2 Size, uint Duration) ReadMetadata(Stream source)
    {
        try
        {
            using var demuxer = Demuxer.Open(source, leaveOpen: true);
            if (demuxer.Video is not { } video) return default;

            var (width, height) = FrameSize(video);
            var (displayWidth, displayHeight) = VideoProbe.DisplaySize(width, height, video.SampleAspectRatio, video.Rotation);
            var duration = video.Codec is CodecIds.H264 or CodecIds.Mjpeg ? VideoProbe.Duration(demuxer).TotalSeconds : 0;
            if (duration <= 0) duration = video.Duration * video.TimeBase.ToDouble();
            return (new Vector2(displayWidth, displayHeight), DurationSeconds(duration));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or OverflowException)
        {
            return default;
        }
        finally
        {
            source.Position = 0;
        }
    }

    private static uint DurationSeconds(double seconds) =>
        double.IsFinite(seconds) && seconds > 0 ? (uint)Math.Min(uint.MaxValue, Math.Ceiling(seconds)) : 0;

    public static async Task<Stream?> TryCreateThumbnailAsync(Stream source, Vector2 videoSize)
    {
        if (videoSize.X <= 0 || videoSize.Y <= 0) return null;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var demuxer = Demuxer.Open(source, leaveOpen: true);
            if (demuxer.Video is not { } video || video.Codec is not (CodecIds.H264 or CodecIds.Mjpeg)) return null;

            var (width, height) = FrameSize(video);
            CheckFrameSize(width, height);
            IVideoDecoder decoder = video.Codec == CodecIds.H264
                ? new H264Decoder(video) { ConcealErrors = true }
                : new MjpegDecoder(video);
            byte[]? damagedCover = null;

            await foreach (var packet in demuxer.ReadPacketsAsync(timeout.Token).ConfigureAwait(false))
            {
                if (packet.StreamIndex != video.StreamIndex || (packet.Discard && video.Codec == CodecIds.Mjpeg)) continue;
                if (video.Codec == CodecIds.Mjpeg && MjpegDecoder.ImageSize(packet.Data.Span) is { } image)
                    CheckFrameSize(image.Width, image.Height);

                IReadOnlyList<VideoFrame> frames;
                try
                {
                    frames = decoder.Decode(packet);
                }
                catch (InvalidDataException) when (video.Codec == CodecIds.Mjpeg)
                {
                    continue;
                }

                var cover = RenderFirstFrame(frames, video, ref damagedCover, timeout.Token);
                if (cover is not null) return new MemoryStream(cover, writable: false);
            }

            var finalCover = RenderFirstFrame(decoder.Flush(), video, ref damagedCover, timeout.Token) ?? damagedCover;
            return finalCover is null ? null : new MemoryStream(finalCover, writable: false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException
            or OverflowException or OperationCanceledException)
        {
            return null;
        }
        finally
        {
            source.Position = 0;
        }
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
