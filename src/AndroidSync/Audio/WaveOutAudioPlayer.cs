using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Com.AndroidSync.App.Audio;

public sealed unsafe partial class WaveOutAudioPlayer : IAudioPlayer
{
    private const uint WaveMapper = 0xFFFF_FFFFu;
    private const uint CallbackEvent = 0x0005_0000u;
    private const ushort WaveFormatPcm = 1;
    private const uint WhdrDone = 0x0000_0001u;
    private const uint WhdrPrepared = 0x0000_0002u;
    private const int SlotCount = 8;
    private const int MaxQueuedPackets = 12;

    private readonly ILogger<WaveOutAudioPlayer> _logger;
    private readonly Channel<byte[]> _packetChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(MaxQueuedPackets)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });

    private IntPtr _waveOutHandle;
    private AutoResetEvent? _bufferDoneEvent;
    private CancellationTokenSource? _cts;
    private Task? _playbackTask;
    private WaveHdr* _nativeHeaders;
    private GCHandle[]? _pinHandles;
    private bool[]? _slotInUse;
    private volatile bool _isMuted;
    private bool _disposed;

    public WaveOutAudioPlayer(ILogger<WaveOutAudioPlayer> logger)
    {
        _logger = logger;
    }

    public bool IsInitialized => _waveOutHandle != IntPtr.Zero && !_disposed;

    public bool IsMuted
    {
        get => _isMuted;
        set => _isMuted = value;
    }

    public void Initialize(int sampleRate = 48_000, int channels = 2, int bitsPerSample = 16)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_waveOutHandle != IntPtr.Zero)
        {
            return;
        }

        var blockAlign = (ushort)(channels * (bitsPerSample / 8));
        var format = new WaveFormatEx
        {
            FormatTag = WaveFormatPcm,
            Channels = (ushort)channels,
            SamplesPerSec = (uint)sampleRate,
            AvgBytesPerSec = (uint)(sampleRate * blockAlign),
            BlockAlign = blockAlign,
            BitsPerSample = (ushort)bitsPerSample,
            Size = 0
        };

        _bufferDoneEvent = new AutoResetEvent(false);
        var result = WaveOutOpen(
            out _waveOutHandle,
            WaveMapper,
            in format,
            _bufferDoneEvent.SafeWaitHandle.DangerousGetHandle(),
            IntPtr.Zero,
            CallbackEvent);

        if (result != 0)
        {
            _bufferDoneEvent.Dispose();
            _bufferDoneEvent = null;
            _waveOutHandle = IntPtr.Zero;
            throw new InvalidOperationException($"無法開啟 Windows 音訊輸出裝置 (waveOutOpen 錯誤碼: {result})。");
        }

        _nativeHeaders = (WaveHdr*)NativeMemory.AllocZeroed((nuint)SlotCount, (nuint)sizeof(WaveHdr));
        _pinHandles = new GCHandle[SlotCount];
        _slotInUse = new bool[SlotCount];

        _cts = new CancellationTokenSource();
        _playbackTask = Task.Run(() => PlaybackLoop(_cts.Token), _cts.Token);
        _logger.LogInformation("已初始化 Windows PCM 音訊輸出 ({SampleRate}Hz, {Channels}ch, {Bits}bit)", sampleRate, channels, bitsPerSample);
    }

    public void WritePcm(ReadOnlySpan<byte> pcmData)
    {
        if (!IsInitialized || _isMuted || pcmData.IsEmpty)
        {
            return;
        }

        _packetChannel.Writer.TryWrite(pcmData.ToArray());
    }

    private void PlaybackLoop(CancellationToken cancellationToken)
    {
        if (_nativeHeaders == null || _slotInUse is null || _bufferDoneEvent is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_packetChannel.Reader.WaitToReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
                {
                    break;
                }

                while (_packetChannel.Reader.TryRead(out var pcmData))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    if (_isMuted)
                    {
                        continue;
                    }

                    var slotIndex = AcquireFreeSlot(cancellationToken);
                    if (slotIndex < 0)
                    {
                        return;
                    }

                    SubmitToSlot(slotIndex, pcmData);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "音訊播放執行緒發生錯誤");
        }
    }

    private int AcquireFreeSlot(CancellationToken cancellationToken)
    {
        if (_nativeHeaders == null || _slotInUse is null || _bufferDoneEvent is null)
        {
            return -1;
        }

        WaitHandle[] waitHandles = [_bufferDoneEvent, cancellationToken.WaitHandle];
        while (!cancellationToken.IsCancellationRequested)
        {
            for (var i = 0; i < SlotCount; i++)
            {
                if (!_slotInUse[i] || (_nativeHeaders[i].Flags & WhdrDone) != 0)
                {
                    if (_slotInUse[i])
                    {
                        UnprepareSlot(i);
                    }

                    return i;
                }
            }

            WaitHandle.WaitAny(waitHandles, 50);
        }

        return -1;
    }

    private void SubmitToSlot(int index, byte[] pcmData)
    {
        if (_nativeHeaders == null || _pinHandles is null || _slotInUse is null)
        {
            return;
        }

        if (_pinHandles[index].IsAllocated)
        {
            _pinHandles[index].Free();
        }

        var pin = GCHandle.Alloc(pcmData, GCHandleType.Pinned);
        _pinHandles[index] = pin;

        _nativeHeaders[index] = new WaveHdr
        {
            Data = pin.AddrOfPinnedObject(),
            BufferLength = (uint)pcmData.Length
        };

        var headerSize = (uint)sizeof(WaveHdr);
        var prepResult = WaveOutPrepareHeader(_waveOutHandle, &_nativeHeaders[index], headerSize);
        if (prepResult != 0)
        {
            pin.Free();
            _pinHandles[index] = default;
            _logger.LogDebug("waveOutPrepareHeader 失敗: {Code}", prepResult);
            return;
        }

        var writeResult = WaveOutWrite(_waveOutHandle, &_nativeHeaders[index], headerSize);
        if (writeResult != 0)
        {
            UnprepareSlot(index);
            _logger.LogDebug("waveOutWrite 失敗: {Code}", writeResult);
            return;
        }

        _slotInUse[index] = true;
    }

    private void UnprepareSlot(int index)
    {
        if (_nativeHeaders != null && (_nativeHeaders[index].Flags & WhdrPrepared) != 0 && _waveOutHandle != IntPtr.Zero)
        {
            _ = WaveOutUnprepareHeader(_waveOutHandle, &_nativeHeaders[index], (uint)sizeof(WaveHdr));
        }

        if (_slotInUse is not null)
        {
            _slotInUse[index] = false;
        }

        if (_pinHandles is not null && _pinHandles[index].IsAllocated)
        {
            _pinHandles[index].Free();
            _pinHandles[index] = default;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _packetChannel.Writer.TryComplete();
        _cts?.Cancel();
        _bufferDoneEvent?.Set();

        if (_waveOutHandle != IntPtr.Zero)
        {
            _ = WaveOutReset(_waveOutHandle);
        }

        try
        {
            _playbackTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore shutdown wait errors
        }

        if (_nativeHeaders != null)
        {
            for (var i = 0; i < SlotCount; i++)
            {
                UnprepareSlot(i);
            }

            NativeMemory.Free(_nativeHeaders);
            _nativeHeaders = null;
        }

        _pinHandles = null;
        _slotInUse = null;

        if (_waveOutHandle != IntPtr.Zero)
        {
            _ = WaveOutClose(_waveOutHandle);
            _waveOutHandle = IntPtr.Zero;
        }

        _bufferDoneEvent?.Dispose();
        _bufferDoneEvent = null;
        _cts?.Dispose();
        _cts = null;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [LibraryImport("winmm.dll", EntryPoint = "waveOutOpen")]
    private static partial uint WaveOutOpen(
        out IntPtr phwo,
        uint uDeviceId,
        in WaveFormatEx pwfx,
        IntPtr dwCallback,
        IntPtr dwInstance,
        uint fdwOpen);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutPrepareHeader")]
    private static partial uint WaveOutPrepareHeader(IntPtr hwo, WaveHdr* pwh, uint cbwh);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutUnprepareHeader")]
    private static partial uint WaveOutUnprepareHeader(IntPtr hwo, WaveHdr* pwh, uint cbwh);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutWrite")]
    private static partial uint WaveOutWrite(IntPtr hwo, WaveHdr* pwh, uint cbwh);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutReset")]
    private static partial uint WaveOutReset(IntPtr hwo);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutClose")]
    private static partial uint WaveOutClose(IntPtr hwo);
}
