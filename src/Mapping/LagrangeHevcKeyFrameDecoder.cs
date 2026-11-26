using System.Runtime.InteropServices;
using ManagedCodecs;
using Sdcb.SimdCodecs.Heif.Hevc;

namespace ShiroBot.LagrangeAdapter;

internal sealed class LagrangeHevcKeyFrameDecoder : IVideoDecoder
{
    private readonly VideoStreamInfo _video;
    private readonly HevcDecoderConfiguration _configuration;
    private readonly HevcSps _sequence;
    private readonly CancellationToken _cancellationToken;

    public LagrangeHevcKeyFrameDecoder(VideoStreamInfo video, long maxFramePixels, CancellationToken cancellationToken)
    {
        _video = video;
        _cancellationToken = cancellationToken;
        _configuration = HevcDecoderConfiguration.Parse(video.CodecPrivate.Span);
        _sequence = _configuration.ParseSps();
        if ((long)_sequence.Width * _sequence.Height > maxFramePixels)
            throw new NotSupportedException("The coded HEVC frame exceeds the cover decoder's pixel limit.");
    }

    public IReadOnlyList<VideoFrame> Decode(Packet packet)
    {
        if (!packet.IsKeyframe || packet.Discard) return [];

        _cancellationToken.ThrowIfCancellationRequested();
        var picture = HevcImageDecoder.Decode(_configuration, packet.Data.Span, _cancellationToken);
        var format = (picture.BitDepthY, picture.BitDepthC) switch
        {
            (8, 8) => PixelFormat.Yuv420P,
            (10, 10) => PixelFormat.Yuv420P10,
            _ => throw new NotSupportedException("The HEVC cover decoder expects 8-bit or 10-bit YUV 4:2:0.")
        };
        var frame = new VideoFrame(picture.OutputWidth, picture.OutputHeight, format)
        {
            Pts = packet.Pts,
            ColorMatrix = _video.ColorMatrix != ColorMatrix.Unspecified
                ? _video.ColorMatrix : ColorCodes.MatrixOf(_sequence.MatrixCoefficients),
            ColorRange = _video.ColorRange != ColorRange.Unspecified
                ? _video.ColorRange : _sequence.VideoFullRange ? ColorRange.Full : ColorRange.Limited
        };

        for (var plane = 0; plane < 3; plane++)
        {
            int shift = plane == 0 ? 0 : 1;
            int left = picture.CropLeft >> shift;
            int top = picture.CropTop >> shift;
            int stride = picture.PlaneWidth(plane);
            var (width, height) = format.PlaneSize(plane, frame.Width, frame.Height);
            for (var row = 0; row < height; row++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var samples = picture.Planes[plane].AsSpan((top + row) * stride + left, width);
                var destination = frame.GetRow(plane, row);
                if (format == PixelFormat.Yuv420P10) samples.CopyTo(MemoryMarshal.Cast<byte, ushort>(destination));
                else for (var column = 0; column < samples.Length; column++) destination[column] = (byte)samples[column];
            }
        }
        return [frame];
    }

    public IReadOnlyList<VideoFrame> Flush() => [];
}
