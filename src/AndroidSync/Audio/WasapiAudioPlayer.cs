using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Com.AndroidSync.App.Audio;

public sealed unsafe class WasapiAudioPlayer : IAudioPlayer
{
    private const uint ClsctxAll = 0x17;
    private const uint ERender = 0;
    private const uint EConsole = 0;
    private const uint AudclntShareModeShared = 0;
    private const uint AudclntStreamFlagsEventCallback = 0x0004_0000;
    private const uint AudclntStreamFlagsAutoConvertPcm = 0x8000_0000;
    private const uint AudclntStreamFlagsSrcDefaultQuality = 0x0800_0000;
    private const ushort WaveFormatPcm = 1;

    private const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);
    private const int AudclntEServiceNotRunning = unchecked((int)0x88890010);

    private const long BufferDurationHns = 200_000; // 20ms low-latency shared buffer
    private const int RingBufferCapacityBytes = 65_536;
    private const int MaxAudioLatencyMs = 80;

    private static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidImmDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidIAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidIAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    private readonly ILogger<WasapiAudioPlayer> _logger;
    private PcmRingBuffer? _ringBuffer;

    private int _sampleRate = 48_000;
    private int _channels = 2;
    private int _bitsPerSample = 16;
    private int _bytesPerFrame = 4;

    private CancellationTokenSource? _cts;
    private Thread? _playbackThread;
    private volatile bool _isInitialized;
    private volatile bool _isMuted;
    private bool _disposed;

    public WasapiAudioPlayer(ILogger<WasapiAudioPlayer> logger)
    {
        _logger = logger;
    }

    public bool IsInitialized => _isInitialized && !_disposed;

    public bool IsMuted
    {
        get => _isMuted;
        set => _isMuted = value;
    }

    public void Initialize(int sampleRate = 48_000, int channels = 2, int bitsPerSample = 16)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isInitialized)
        {
            return;
        }

        _sampleRate = sampleRate;
        _channels = channels;
        _bitsPerSample = bitsPerSample;
        _bytesPerFrame = Math.Max(1, channels * (bitsPerSample / 8));
        var maxLatencyBytes = (_sampleRate * _bytesPerFrame * MaxAudioLatencyMs) / 1000;
        _ringBuffer = new PcmRingBuffer(RingBufferCapacityBytes, _bytesPerFrame, maxLatencyBytes);

        // Probe endpoint creation synchronously on a COM-initialized helper so callers get immediate failure
        // if no audio output device exists (allowing graceful fallback to WaveOutAudioPlayer).
        Exception? initError = null;
        using var ready = new ManualResetEventSlim(false);
        _cts = new CancellationTokenSource();

        _playbackThread = new Thread(() => PlaybackThreadMain(_cts.Token, ready, out initError))
        {
            IsBackground = true,
            Name = "AndroidSync.WasapiAudio",
            Priority = ThreadPriority.AboveNormal
        };
        _playbackThread.Start();

        ready.Wait(TimeSpan.FromSeconds(3));
        if (initError is not null)
        {
            _cts.Cancel();
            _playbackThread.Join(TimeSpan.FromSeconds(1));
            _playbackThread = null;
            _cts.Dispose();
            _cts = null;
            throw initError;
        }

        _isInitialized = true;
        _logger.LogInformation(
            "已初始化 WASAPI 低延遲音訊輸出 ({SampleRate}Hz, {Channels}ch, {Bits}bit)",
            _sampleRate,
            _channels,
            _bitsPerSample);
    }

    public void WritePcm(ReadOnlySpan<byte> pcmData)
    {
        if (!IsInitialized || _isMuted || pcmData.IsEmpty)
        {
            return;
        }

        _ringBuffer?.Write(pcmData);
    }

    private void PlaybackThreadMain(CancellationToken token, ManualResetEventSlim ready, out Exception? initError)
    {
        initError = null;
        var comHr = CoInitializeEx(null, 0); // COINIT_MULTITHREADED
        var comInitialized = comHr >= 0;

        try
        {
            if (!TryOpenWasapiClient(out var audioClient, out var renderClient, out var bufferFrameCount, out var sampleEvent, out var openError))
            {
                initError = openError ?? new InvalidOperationException("無法初始化 WASAPI 音訊裝置。");
                ready.Set();
                return;
            }

            ready.Set();

            try
            {
                RunPlaybackLoop(ref audioClient, ref renderClient, ref bufferFrameCount, ref sampleEvent, token);
            }
            finally
            {
                CloseWasapiClient(ref audioClient, ref renderClient, ref sampleEvent);
            }
        }
        finally
        {
            if (!ready.IsSet)
            {
                ready.Set();
            }

            if (comInitialized)
            {
                CoUninitialize();
            }
        }
    }

    private void RunPlaybackLoop(
        ref void* audioClient,
        ref void* renderClient,
        ref uint bufferFrameCount,
        ref AutoResetEvent? sampleEvent,
        CancellationToken token)
    {
        var ringBuffer = _ringBuffer;
        var waitHandles = new WaitHandle[2];
        waitHandles[1] = token.WaitHandle;
        AutoResetEvent? boundSampleEvent = null;

        while (!token.IsCancellationRequested)
        {
            if (sampleEvent is null || audioClient is null || renderClient is null)
            {
                boundSampleEvent = null;
                if (!TryReopenAfterDeviceSwitch(ref audioClient, ref renderClient, ref bufferFrameCount, ref sampleEvent, token))
                {
                    break;
                }

                continue;
            }

            if (!ReferenceEquals(boundSampleEvent, sampleEvent))
            {
                waitHandles[0] = sampleEvent;
                boundSampleEvent = sampleEvent;
            }

            var signaled = WaitHandle.WaitAny(waitHandles, 40);
            if (token.IsCancellationRequested || signaled == 1)
            {
                break;
            }

            uint paddingFrames = 0;
            var hr = AudioClientGetCurrentPadding(audioClient, &paddingFrames);
            if (IsDeviceInvalidated(hr))
            {
                _logger.LogInformation("偵測到 Windows 預設音訊裝置變更，正在自動切換 WASAPI 輸出...");
                CloseWasapiClient(ref audioClient, ref renderClient, ref sampleEvent);
                ringBuffer?.Clear();
                continue;
            }

            if (hr < 0)
            {
                continue;
            }

            var availableFrames = bufferFrameCount > paddingFrames ? bufferFrameCount - paddingFrames : 0;
            if (availableFrames == 0)
            {
                continue;
            }

            byte* renderPtr = null;
            hr = RenderClientGetBuffer(renderClient, availableFrames, &renderPtr);
            if (IsDeviceInvalidated(hr))
            {
                CloseWasapiClient(ref audioClient, ref renderClient, ref sampleEvent);
                ringBuffer?.Clear();
                continue;
            }

            if (hr < 0 || renderPtr is null)
            {
                continue;
            }

            var targetBytes = (int)(availableFrames * (uint)_bytesPerFrame);
            var targetSpan = new Span<byte>(renderPtr, targetBytes);
            int writtenBytes;

            if (_isMuted || ringBuffer is null)
            {
                ringBuffer?.Clear();
                targetSpan.Clear();
                writtenBytes = 0;
            }
            else
            {
                writtenBytes = ringBuffer.Read(targetSpan);
                if (writtenBytes > 0 && writtenBytes < targetBytes)
                {
                    targetSpan[writtenBytes..].Clear();
                }
            }

            var releaseFlags = writtenBytes == 0 ? 2u : 0u; // AUDCLNT_BUFFERFLAGS_SILENT = 2
            hr = RenderClientReleaseBuffer(renderClient, availableFrames, releaseFlags);
            if (IsDeviceInvalidated(hr))
            {
                CloseWasapiClient(ref audioClient, ref renderClient, ref sampleEvent);
                ringBuffer?.Clear();
            }
        }
    }

    private bool TryReopenAfterDeviceSwitch(
        ref void* audioClient,
        ref void* renderClient,
        ref uint bufferFrameCount,
        ref AutoResetEvent? sampleEvent,
        CancellationToken token)
    {
        for (var attempt = 0; attempt < 10 && !token.IsCancellationRequested; attempt++)
        {
            if (token.WaitHandle.WaitOne(250))
            {
                return false;
            }

            if (TryOpenWasapiClient(out audioClient, out renderClient, out bufferFrameCount, out sampleEvent, out _))
            {
                _logger.LogInformation("WASAPI 已重新連線至新的預設音訊裝置。");
                return true;
            }
        }

        return false;
    }

    private bool TryOpenWasapiClient(
        out void* audioClient,
        out void* renderClient,
        out uint bufferFrameCount,
        out AutoResetEvent? sampleEvent,
        out Exception? error)
    {
        audioClient = null;
        renderClient = null;
        bufferFrameCount = 0;
        sampleEvent = null;
        error = null;

        void* enumerator = null;
        void* device = null;
        try
        {
            var clsid = ClsidMmDeviceEnumerator;
            var iidEnum = IidImmDeviceEnumerator;
            var hr = CoCreateInstance(&clsid, null, ClsctxAll, &iidEnum, &enumerator);
            if (hr < 0 || enumerator is null)
            {
                error = new COMException($"無法建立 MMDeviceEnumerator (0x{hr:X8})", hr);
                return false;
            }

            hr = EnumeratorGetDefaultAudioEndpoint(enumerator, ERender, EConsole, &device);
            if (hr < 0 || device is null)
            {
                error = new COMException($"找不到預設 Windows 音訊輸出裝置 (0x{hr:X8})", hr);
                return false;
            }

            var iidClient = IidIAudioClient;
            void* client = null;
            hr = DeviceActivate(device, &iidClient, ClsctxAll, null, &client);
            if (hr < 0 || client is null)
            {
                error = new COMException($"啟用 IAudioClient 失敗 (0x{hr:X8})", hr);
                return false;
            }

            var blockAlign = (ushort)(_channels * (_bitsPerSample / 8));
            var format = new WaveFormatEx
            {
                FormatTag = WaveFormatPcm,
                Channels = (ushort)_channels,
                SamplesPerSec = (uint)_sampleRate,
                AvgBytesPerSec = (uint)(_sampleRate * blockAlign),
                BlockAlign = blockAlign,
                BitsPerSample = (ushort)_bitsPerSample,
                Size = 0
            };

            var flags = AudclntStreamFlagsEventCallback
                | AudclntStreamFlagsAutoConvertPcm
                | AudclntStreamFlagsSrcDefaultQuality;
            hr = AudioClientInitialize(client, AudclntShareModeShared, flags, BufferDurationHns, 0, &format, null);
            if (hr < 0)
            {
                ReleaseCom(client);
                error = new COMException($"IAudioClient::Initialize 失敗 (0x{hr:X8})", hr);
                return false;
            }

            uint frames = 0;
            hr = AudioClientGetBufferSize(client, &frames);
            if (hr < 0 || frames == 0)
            {
                ReleaseCom(client);
                error = new COMException($"IAudioClient::GetBufferSize 失敗 (0x{hr:X8})", hr);
                return false;
            }

            var ev = new AutoResetEvent(false);
            hr = AudioClientSetEventHandle(client, ev.SafeWaitHandle.DangerousGetHandle());
            if (hr < 0)
            {
                ev.Dispose();
                ReleaseCom(client);
                error = new COMException($"IAudioClient::SetEventHandle 失敗 (0x{hr:X8})", hr);
                return false;
            }

            var iidRender = IidIAudioRenderClient;
            void* render = null;
            hr = AudioClientGetService(client, &iidRender, &render);
            if (hr < 0 || render is null)
            {
                ev.Dispose();
                ReleaseCom(client);
                error = new COMException($"IAudioClient::GetService(IAudioRenderClient) 失敗 (0x{hr:X8})", hr);
                return false;
            }

            hr = AudioClientStart(client);
            if (hr < 0)
            {
                ReleaseCom(render);
                ev.Dispose();
                ReleaseCom(client);
                error = new COMException($"IAudioClient::Start 失敗 (0x{hr:X8})", hr);
                return false;
            }

            audioClient = client;
            renderClient = render;
            bufferFrameCount = frames;
            sampleEvent = ev;
            return true;
        }
        finally
        {
            ReleaseCom(device);
            ReleaseCom(enumerator);
        }
    }

    private static void CloseWasapiClient(ref void* audioClient, ref void* renderClient, ref AutoResetEvent? sampleEvent)
    {
        if (audioClient is not null)
        {
            _ = AudioClientStop(audioClient);
        }

        ReleaseCom(renderClient);
        renderClient = null;

        ReleaseCom(audioClient);
        audioClient = null;

        sampleEvent?.Dispose();
        sampleEvent = null;
    }

    private static bool IsDeviceInvalidated(int hr) =>
        hr is AudclntEDeviceInvalidated or AudclntEServiceNotRunning;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _isInitialized = false;
        _cts?.Cancel();
        _playbackThread?.Join(TimeSpan.FromSeconds(1));
        _playbackThread = null;
        _ringBuffer?.Clear();
        _cts?.Dispose();
        _cts = null;
    }

    private static void ReleaseCom(void* obj)
    {
        if (obj is null)
        {
            return;
        }

        var vtbl = *(void***)obj;
        ((delegate* unmanaged[Stdcall]<void*, uint>)vtbl[2])(obj);
    }

    private static int EnumeratorGetDefaultAudioEndpoint(void* enumerator, uint dataFlow, uint role, void** endpoint)
    {
        var vtbl = *(void***)enumerator;
        return ((delegate* unmanaged[Stdcall]<void*, uint, uint, void**, int>)vtbl[4])(enumerator, dataFlow, role, endpoint);
    }

    private static int DeviceActivate(void* device, Guid* iid, uint clsCtx, void* activationParams, void** ppInterface)
    {
        var vtbl = *(void***)device;
        return ((delegate* unmanaged[Stdcall]<void*, Guid*, uint, void*, void**, int>)vtbl[3])(
            device, iid, clsCtx, activationParams, ppInterface);
    }

    private static int AudioClientInitialize(
        void* client,
        uint shareMode,
        uint streamFlags,
        long hnsBufferDuration,
        long hnsPeriodicity,
        WaveFormatEx* format,
        Guid* sessionGuid)
    {
        var vtbl = *(void***)client;
        return ((delegate* unmanaged[Stdcall]<void*, uint, uint, long, long, WaveFormatEx*, Guid*, int>)vtbl[3])(
            client, shareMode, streamFlags, hnsBufferDuration, hnsPeriodicity, format, sessionGuid);
    }

    private static int AudioClientGetBufferSize(void* client, uint* numBufferFrames)
    {
        var vtbl = *(void***)client;
        return ((delegate* unmanaged[Stdcall]<void*, uint*, int>)vtbl[4])(client, numBufferFrames);
    }

    private static int AudioClientGetCurrentPadding(void* client, uint* numPaddingFrames)
    {
        var vtbl = *(void***)client;
        return ((delegate* unmanaged[Stdcall]<void*, uint*, int>)vtbl[6])(client, numPaddingFrames);
    }

    private static int AudioClientStart(void* client)
    {
        var vtbl = *(void***)client;
        return ((delegate* unmanaged[Stdcall]<void*, int>)vtbl[10])(client);
    }

    private static int AudioClientStop(void* client)
    {
        var vtbl = *(void***)client;
        return ((delegate* unmanaged[Stdcall]<void*, int>)vtbl[11])(client);
    }

    private static int AudioClientSetEventHandle(void* client, nint eventHandle)
    {
        var vtbl = *(void***)client;
        return ((delegate* unmanaged[Stdcall]<void*, nint, int>)vtbl[13])(client, eventHandle);
    }

    private static int AudioClientGetService(void* client, Guid* riid, void** ppv)
    {
        var vtbl = *(void***)client;
        return ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)vtbl[14])(client, riid, ppv);
    }

    private static int RenderClientGetBuffer(void* renderClient, uint numFramesRequested, byte** data)
    {
        var vtbl = *(void***)renderClient;
        return ((delegate* unmanaged[Stdcall]<void*, uint, byte**, int>)vtbl[3])(renderClient, numFramesRequested, data);
    }

    private static int RenderClientReleaseBuffer(void* renderClient, uint numFramesWritten, uint flags)
    {
        var vtbl = *(void***)renderClient;
        return ((delegate* unmanaged[Stdcall]<void*, uint, uint, int>)vtbl[4])(renderClient, numFramesWritten, flags);
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

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(void* reserved, uint dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(Guid* rclsid, void* pUnkOuter, uint dwClsContext, Guid* riid, void** ppv);
}
