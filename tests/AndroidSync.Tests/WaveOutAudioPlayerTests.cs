using Com.AndroidSync.App.Audio;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Com.AndroidSync.App.Tests;

public sealed class WaveOutAudioPlayerTests
{
    [Fact]
    public void WritePcm_BeforeInitialize_DoesNotThrow()
    {
        using var player = new WaveOutAudioPlayer(NullLogger<WaveOutAudioPlayer>.Instance);
        Assert.False(player.IsInitialized);
        player.WritePcm([0, 0, 0, 0]);
        player.IsMuted = true;
        Assert.True(player.IsMuted);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimesSafely()
    {
        var player = new WaveOutAudioPlayer(NullLogger<WaveOutAudioPlayer>.Instance);
        player.Dispose();
        player.Dispose();
        Assert.False(player.IsInitialized);
        Assert.Throws<ObjectDisposedException>(() => player.Initialize());
    }
}
