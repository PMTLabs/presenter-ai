using System.Buffers.Binary;
using PresenterAi.Application.Presenting;
using Xunit;

namespace PresenterAi.Application.Tests.Presenting;

public sealed class AudioLevelTests
{
    public static byte[] VoicedFrame(int bytes = 4800, int amplitude = 3000)
    {
        var buffer = new byte[bytes];
        var samples = bytes / 2;
        for (var i = 0; i < samples; i++)
        {
            var value = (short)Math.Floor(Math.Sin(i / 5.0) * amplitude + 0.5);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 2), value);
        }

        return buffer;
    }

    [Fact]
    public void Silence_is_not_voiced()
    {
        var silence = new byte[4800];

        Assert.Equal(0, AudioLevel.Rms(silence));
        Assert.False(AudioLevel.IsVoiced(silence));
        Assert.False(AudioLevel.IsVoiced(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Voiced_fixture_is_voiced()
    {
        Assert.True(AudioLevel.Rms(VoicedFrame()) > 1000);
        Assert.True(AudioLevel.IsVoiced(VoicedFrame()));
        Assert.False(AudioLevel.IsVoiced(VoicedFrame(4800, 40)));
    }

    [Fact]
    public void Threshold_boundary_119_vs_120()
    {
        Assert.False(AudioLevel.IsVoiced(ConstantFrame(119)));
        Assert.True(AudioLevel.IsVoiced(ConstantFrame(120)));
    }

    [Fact]
    public void Fractional_rms_values_use_the_exact_120_threshold()
    {
        var below = MixedFrame(119, 120);
        var above = MixedFrame(120, 120, 120, 121);
        // Below is sqrt((119^2 + 120^2) / 2) = 119.50104..., strictly below 120.
        var belowExpected = Math.Sqrt((119d * 119 + 120d * 120) / 2);
        // The four-sample frame is sqrt((3 * 120^2 + 121^2) / 4) = 120.25078..., just above 120.
        var aboveExpected = Math.Sqrt((3d * 120 * 120 + 121d * 121) / 4);

        Assert.InRange(belowExpected, 119, 120);
        Assert.True(belowExpected > 119 && belowExpected < 120);
        Assert.Equal(belowExpected, AudioLevel.Rms(below), 10);
        Assert.False(AudioLevel.IsVoiced(below));

        Assert.True(aboveExpected > 120);
        Assert.Equal(aboveExpected, AudioLevel.Rms(above), 10);
        Assert.True(AudioLevel.IsVoiced(above));
    }

    [Fact]
    public void Odd_length_buffer_ignores_trailing_byte()
    {
        var even = VoicedFrame();
        var odd = new byte[even.Length + 1];
        even.CopyTo(odd, 0);
        odd[^1] = 0xff;

        Assert.Equal(AudioLevel.Rms(even), AudioLevel.Rms(odd));
        Assert.Equal(AudioLevel.IsVoiced(even), AudioLevel.IsVoiced(odd));
    }

    [Fact]
    public void Stride_subsamples()
    {
        Assert.True(AudioLevel.Rms(VoicedFrame(), stride: 4) > 1000);
    }

    private static byte[] ConstantFrame(short value)
    {
        var buffer = new byte[4800];
        for (var offset = 0; offset < buffer.Length; offset += 2)
        {
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset, 2), value);
        }

        return buffer;
    }

    private static byte[] MixedFrame(params short[] pattern)
    {
        var buffer = new byte[pattern.Length * 4];
        for (var i = 0; i < pattern.Length; i++)
        {
            // Duplicate each value because IsVoiced subsamples every second PCM sample.
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 4, 2), pattern[i]);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 4 + 2, 2), pattern[i]);
        }

        return buffer;
    }
}
