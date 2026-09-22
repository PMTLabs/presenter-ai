using System.Runtime.InteropServices;

namespace PresenterAi.Application.Presenting;

public static class AudioLevel
{
    public const int VoiceThreshold = 120;

    public static double Rms(ReadOnlySpan<byte> pcm16, int stride = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stride, 0);

        var samples = MemoryMarshal.Cast<byte, short>(pcm16);
        if (samples.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        var count = 0;
        for (var i = 0; i < samples.Length; i += stride)
        {
            var sample = samples[i];
            sum += (double)sample * sample;
            count++;
        }

        return Math.Sqrt(sum / count);
    }

    public static bool IsVoiced(ReadOnlySpan<byte> pcm16, int threshold = VoiceThreshold) =>
        Rms(pcm16) >= threshold;
}
