using MicOptimizer.Audio;
using NAudio.Wave;
using Xunit;

namespace MicOptimizer.Tests;

public class SampleFormatTests
{
    [Fact]
    public void Plain_float_format_is_float() =>
        Assert.True(AudioEngine.IsFloat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));

    [Fact]
    public void Extensible_float_format_is_float()
    {
        // This is how Windows reports most shared-mode devices; it was once misread as integer audio.
        var fmt = new WaveFormatExtensible(48000, 32, 2);
        Assert.Equal(WaveFormatEncoding.Extensible, fmt.Encoding);
        Assert.True(AudioEngine.IsFloat(fmt));
    }

    [Fact]
    public void Pcm_formats_are_not_float()
    {
        Assert.False(AudioEngine.IsFloat(new WaveFormat(48000, 16, 2)));
        Assert.False(AudioEngine.IsFloat(new WaveFormat(48000, 24, 2)));
    }
}
