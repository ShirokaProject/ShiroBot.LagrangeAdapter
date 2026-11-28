using ManagedCodecs.Containers;

namespace ManagedCodecs.Thumbnails;

internal static class VideoProbe
{
    /// <summary>
    /// The size a frame is shown at: the coded size stretched by the sample aspect ratio (wider for a ratio above 1,
    /// taller below it, so never smaller than coded), rounded to whole pixels, then turned by the rotation.
    /// </summary>
    internal static (int Width, int Height) DisplaySize(int width, int height, Rational sampleAspectRatio, int rotation)
    {
        var ratio = Ratio(sampleAspectRatio);
        long w = width;
        long h = height;
        if (ratio.Numerator > ratio.Denominator)
        {
            w = ((2L * width * ratio.Numerator) + ratio.Denominator) / (2L * ratio.Denominator);
        }
        else if (ratio.Numerator < ratio.Denominator)
        {
            h = ((2L * height * ratio.Denominator) + ratio.Numerator) / (2L * ratio.Numerator);
        }

        return rotation is 90 or 270 ? ((int)h, (int)w) : ((int)w, (int)h);
    }

    // A container's "unknown" 0:1 is square pixels, as ffprobe's display aspect ratio takes it.
    private static Rational Ratio(Rational sampleAspectRatio) =>
        sampleAspectRatio.Numerator > 0 && sampleAspectRatio.Denominator > 0 ? sampleAspectRatio : new Rational(1, 1);

    /// <summary>
    /// As ffprobe's <c>format=duration</c> (FFmpeg's update_stream_timings): from the earliest stream start to the
    /// latest stream end, a stream starting at its first packet shown (packets an edit only decodes left out; past the
    /// samples it skips, MP3's encoder delay inside its first packet; every audio stream, not only the first) and
    /// lasting its container duration (<see cref="VideoStreamInfo.Duration"/>), or to the end of its last packet where
    /// the container gives none (except in AVI, where such a stream, audio with a dwSampleSize, ends nothing: FFmpeg gives it
    /// no duration and reads no packets for one). Matroska's: the Segment's Duration, or, in a file without one (a live recording, for
    /// which ffprobe gives none), from the first packet shown to the end of the last. MXF's streams last their header's
    /// track durations, which a file written to a pipe lacks: there its packets'.
    /// </summary>
    internal static TimeSpan Duration(IDemuxer demuxer, CancellationToken cancellationToken = default)
    {
        var streams = new Dictionary<int, (Rational TimeBase, long Duration, long Start, long End)>();
        if (demuxer.Video is { } video)
        {
            streams[video.StreamIndex] = (video.TimeBase, video.Duration, long.MaxValue, long.MinValue);
        }

        // Every audio stream, as ffprobe's file duration counts every stream, read in one pass; then Audio alone is read
        // again, as a demuxer just opened reads it (both callers open their own).
        var tracks = demuxer.AudioTracks;
        var sampleRates = new Dictionary<int, int>();
        foreach (var audio in tracks)
        {
            streams[audio.StreamIndex] = (audio.TimeBase, audio.Duration, long.MaxValue, long.MinValue);
            sampleRates[audio.StreamIndex] = audio.SampleRate;
        }

        int[]? chosen = tracks.Count > 1 && demuxer.Audio is { } unasked ? [unasked.StreamIndex] : null;
        if (chosen is not null)
        {
            demuxer.SelectAudio([.. tracks.Select(t => t.StreamIndex)]);
        }

        try
        {
            var packets = demuxer switch
            {
                Mp4Demuxer mp4 => mp4.ReadPacketInfo(),
                AviDemuxer avi => avi.ReadPacketInfo(),
                MatroskaDemuxer mkv => mkv.ReadPacketInfo(),
                MxfDemuxer mxf => mxf.ReadPacketInfo(),
                _ => [],
            };

            // SkipSamples counts decoded samples; the packets' times are in the stream's time base.
            foreach (var packet in packets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!packet.Discard && streams.TryGetValue(packet.StreamIndex, out var s))
                {
                    int sampleRate = sampleRates.GetValueOrDefault(packet.StreamIndex);
                    long skip = packet.SkipSamples > 0 && sampleRate > 0 && s.TimeBase.Numerator > 0
                        ? packet.SkipSamples * (long)s.TimeBase.Denominator / ((long)sampleRate * s.TimeBase.Numerator)
                        : 0;
                    streams[packet.StreamIndex] = s with { Start = Math.Min(s.Start, packet.Pts + skip), End = Math.Max(s.End, packet.Pts + packet.Duration) };
                }
            }
        }
        finally
        {
            if (chosen is not null)
            {
                demuxer.SelectAudio(chosen);
            }
        }

        // Matroska's Duration is the Segment's (its streams' Duration carries it), which FFmpeg takes as the file's
        // duration itself, from the earliest stream's start.
        bool segmentDuration = demuxer is MatroskaDemuxer;
        bool durationsOnly = demuxer is AviDemuxer;
        double start = double.PositiveInfinity;
        double end = double.NegativeInfinity;
        foreach (var (timeBase, duration, first, last) in streams.Values)
        {
            if (first == long.MaxValue)
            {
                continue; // no packet shown
            }

            start = Math.Min(start, first * timeBase.ToDouble());
            if (durationsOnly && duration <= 0)
            {
                continue;
            }

            end = Math.Max(end, (duration > 0 && !segmentDuration ? first + duration : last) * timeBase.ToDouble());
        }

        if (segmentDuration && streams.Values.FirstOrDefault().Duration is > 0 and var segment && double.IsFinite(start))
        {
            return Time(segment * streams.Values.First().TimeBase.ToDouble());
        }

        return end > start ? Time(end - start) : TimeSpan.Zero;
    }

    /// <summary>
    /// <paramref name="seconds"/> as a <see cref="TimeSpan"/>, the ticks truncated as .NET's
    /// <see cref="TimeSpan.FromSeconds(double)"/> does them. Not that method itself: .NET Standard's (Mono's, in the Unity
    /// build) rounds to the millisecond, so a time 0.2002 s in would read 0.2 s there and 8 s, as a double just under it,
    /// 7.9999999 s here (tools/unity-check.sh compares the two).
    /// </summary>
    /// <exception cref="OverflowException">The time is longer than a <see cref="TimeSpan"/> holds.</exception>
    internal static TimeSpan Time(double seconds)
    {
        double ticks = seconds * TimeSpan.TicksPerSecond;
        if (!(ticks >= long.MinValue && ticks < long.MaxValue))
        {
            throw new OverflowException("The time is longer than a TimeSpan holds.");
        }

        return TimeSpan.FromTicks((long)ticks);
    }
}
