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
}
