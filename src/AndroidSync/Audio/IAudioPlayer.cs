namespace Com.AndroidSync.App.Audio;

public interface IAudioPlayer : IDisposable
{
    bool IsInitialized { get; }

    bool IsMuted { get; set; }

    void Initialize(int sampleRate = 48_000, int channels = 2, int bitsPerSample = 16);

    void WritePcm(ReadOnlySpan<byte> pcmData);
}
