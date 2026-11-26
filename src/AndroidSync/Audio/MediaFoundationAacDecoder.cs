using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Com.AndroidSync.App.Audio;

public sealed unsafe class MediaFoundationAacDecoder : IDisposable
{
    private const uint MfVersion = 0x00020070;
    private const uint MfStartupLite = 1;
    private const uint ClsctxInprocServer = 1;

    private const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    private const int MfETransformStreamChange = unchecked((int)0xC00D6D61);
    private const int MfENotAccepting = unchecked((int)0xC00D36B5);
    private const int MfENoMoreTypes = unchecked((int)0xC00D36B9);

    private const uint MftMessageCommandFlush = 0x00000000;
    private const uint MftMessageNotifyBeginStreaming = 0x10000000;
    private const uint MftMessageNotifyStartOfStream = 0x10000003;

    private static readonly Guid ClsidCmsAacDecMft = new("32D186A7-218F-4C75-8876-DD77273A8999");
    private static readonly Guid IidImfTransform = new("BF94C121-5B05-4E6F-8000-BA598961414D");
    private static readonly Guid MfMtMajorType = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    private static readonly Guid MfMtSubtype = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    private static readonly Guid MfMediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfAudioFormatAac = new("00001610-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfAudioFormatPcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfMtAudioNumChannels = new("37E48BF5-645E-4C5B-89DE-ADA9E29B696A");
    private static readonly Guid MfMtAudioSamplesPerSecond = new("5FAEEAE7-0290-4C31-9E8A-C534F68D9DBA");
    private static readonly Guid MfMtAudioBitsPerSample = new("F2DEB57F-40FA-4764-AA33-ED4F2D1FF669");
    private static readonly Guid MfMtAacPayloadType = new("BFBABE79-7434-4D1C-94F0-72A3B9E17188");
    private static readonly Guid MfMtUserData = new("B6BC765F-4C3B-40A4-BD51-2535B66FE09D");

    public delegate void PcmDecodedHandler(ReadOnlySpan<byte> pcmData);

    private readonly ILogger<MediaFoundationAacDecoder> _logger;
    private void* _decoder;
    private void* _outputSample;
    private void* _outputBuffer;
    private uint _outputBufferCapacity;
    private bool _mfStarted;
    private bool _initialized;
    private bool _disposed;
    private uint _outputBufferSize = 16_384;

    public MediaFoundationAacDecoder(ILogger<MediaFoundationAacDecoder> logger)
    {
        _logger = logger;
    }

    public bool IsInitialized => _initialized && !_disposed;

    public void Initialize(ReadOnlySpan<byte> audioSpecificConfig, int sampleRate = 48_000, int channels = 2)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        ThrowIfFailed(MFStartup(MfVersion, MfStartupLite), "MFStartup");
        _mfStarted = true;

        var clsid = ClsidCmsAacDecMft;
        var iid = IidImfTransform;
        void* mft = null;
        ThrowIfFailed(CoCreateInstance(&clsid, null, ClsctxInprocServer, &iid, &mft), "CoCreateInstance(AACDecMFT)");
        _decoder = mft;

        ConfigureInputType(audioSpecificConfig, sampleRate, channels);
        ConfigureOutputType(sampleRate, channels);

        ThrowIfFailed(MftProcessMessage(_decoder, MftMessageNotifyBeginStreaming, 0), "NotifyBeginStreaming");
        ThrowIfFailed(MftProcessMessage(_decoder, MftMessageNotifyStartOfStream, 0), "NotifyStartOfStream");
        _initialized = true;
        _logger.LogInformation("已初始化 Media Foundation AAC 音訊解碼器 ({SampleRate}Hz, {Channels}ch)", sampleRate, channels);
    }

    public static byte[] BuildHeAacUserData(ReadOnlySpan<byte> audioSpecificConfig)
    {
        // HEAACWAVEINFO trailing 12 bytes (wPayloadType=0, wAudioProfileLevelIndication=0xFE, wStructType=0, wReserved1=0, dwReserved2=0)
        // followed by AudioSpecificConfig (defaults to 0x11, 0x90 for 48kHz stereo AAC-LC if empty).
        var asc = audioSpecificConfig.IsEmpty ? [0x11, 0x90] : audioSpecificConfig.ToArray();
        var userData = new byte[12 + asc.Length];
        userData[2] = 0xFE; // AudioProfileLevelIndication unspecified
        Buffer.BlockCopy(asc, 0, userData, 12, asc.Length);
        return userData;
    }

    public void Decode(ReadOnlySpan<byte> aacPacket, long ptsMicroseconds, PcmDecodedHandler onPcmDecoded)
    {
        if (!IsInitialized || _decoder is null || aacPacket.IsEmpty)
        {
            return;
        }

        void* sample = null;
        void* buffer = null;
        try
        {
            ThrowIfFailed(MFCreateSample(&sample), "MFCreateSample(AAC)");
            ThrowIfFailed(MFCreateMemoryBuffer((uint)aacPacket.Length, &buffer), "MFCreateMemoryBuffer(AAC)");

            byte* dstPtr = null;
            uint maxLen = 0;
            uint curLen = 0;
            ThrowIfFailed(BufferLock(buffer, &dstPtr, &maxLen, &curLen), "BufferLock(AAC)");
            try
            {
                aacPacket.CopyTo(new Span<byte>(dstPtr, aacPacket.Length));
            }
            finally
            {
                BufferUnlock(buffer);
            }

            ThrowIfFailed(BufferSetCurrentLength(buffer, (uint)aacPacket.Length), "SetCurrentLength(AAC)");
            ThrowIfFailed(SampleAddBuffer(sample, buffer), "AddBuffer(AAC)");
            SampleSetTime(sample, ptsMicroseconds * 10);

            var hr = MftProcessInput(_decoder, 0, sample, 0);
            if (hr == MfENotAccepting)
            {
                DrainOutputs(onPcmDecoded);
                hr = MftProcessInput(_decoder, 0, sample, 0);
            }

            if (hr < 0)
            {
                _logger.LogDebug("AAC ProcessInput 回傳 0x{Hr:X8}", hr);
                return;
            }
        }
        finally
        {
            ReleaseCom(buffer);
            ReleaseCom(sample);
        }

        DrainOutputs(onPcmDecoded);
    }

    private void EnsureOutputSample(uint requiredBytes)
    {
        if (_outputSample is not null && _outputBuffer is not null && _outputBufferCapacity >= requiredBytes)
        {
            _ = BufferSetCurrentLength(_outputBuffer, 0);
            return;
        }

        ReleaseOutputSample();

        void* sample = null;
        void* buffer = null;
        try
        {
            ThrowIfFailed(MFCreateSample(&sample), "MFCreateSample(AACOut)");
            ThrowIfFailed(MFCreateMemoryBuffer(requiredBytes, &buffer), "MFCreateMemoryBuffer(AACOut)");
            ThrowIfFailed(SampleAddBuffer(sample, buffer), "AddBuffer(AACOut)");
            _outputSample = sample;
            _outputBuffer = buffer;
            _outputBufferCapacity = requiredBytes;
        }
        catch
        {
            ReleaseCom(buffer);
            ReleaseCom(sample);
            throw;
        }
    }

    private void ReleaseOutputSample()
    {
        ReleaseCom(_outputBuffer);
        _outputBuffer = null;
        ReleaseCom(_outputSample);
        _outputSample = null;
        _outputBufferCapacity = 0;
    }

    private void DrainOutputs(PcmDecodedHandler onPcmDecoded)
    {
        var requiredSize = Math.Max(_outputBufferSize, 16_384u);
        for (var i = 0; i < 8; i++)
        {
            void* events = null;
            try
            {
                EnsureOutputSample(requiredSize);

                var outputData = new MftOutputDataBuffer
                {
                    StreamId = 0,
                    Sample = _outputSample,
                    Status = 0,
                    Events = null
                };

                uint status = 0;
                var hr = MftProcessOutput(_decoder, 0, 1, &outputData, &status);
                events = outputData.Events;

                if (hr == MfETransformNeedMoreInput)
                {
                    return;
                }

                if (hr == MfETransformStreamChange)
                {
                    ConfigureOutputType(48_000, 2);
                    requiredSize = Math.Max(_outputBufferSize, 16_384u);
                    continue;
                }

                if (hr < 0 || outputData.Sample is null)
                {
                    return;
                }

                void* contiguous = null;
                try
                {
                    if (SampleConvertToContiguousBuffer(outputData.Sample, &contiguous) >= 0 && contiguous is not null)
                    {
                        byte* ptr = null;
                        uint maxLen = 0;
                        uint curLen = 0;
                        if (BufferLock(contiguous, &ptr, &maxLen, &curLen) >= 0 && curLen > 0)
                        {
                            try
                            {
                                onPcmDecoded(new ReadOnlySpan<byte>(ptr, (int)curLen));
                            }
                            finally
                            {
                                BufferUnlock(contiguous);
                            }
                        }
                    }
                }
                finally
                {
                    ReleaseCom(contiguous);
                }
            }
            finally
            {
                ReleaseCom(events);
            }
        }
    }

    private void ConfigureInputType(ReadOnlySpan<byte> audioSpecificConfig, int sampleRate, int channels)
    {
        void* inputType = null;
        try
        {
            ThrowIfFailed(MFCreateMediaType(&inputType), "MFCreateMediaType(AACIn)");
            SetAttrGuid(inputType, MfMtMajorType, MfMediaTypeAudio);
            SetAttrGuid(inputType, MfMtSubtype, MfAudioFormatAac);
            SetAttrUInt32(inputType, MfMtAudioNumChannels, (uint)channels);
            SetAttrUInt32(inputType, MfMtAudioSamplesPerSecond, (uint)sampleRate);
            SetAttrUInt32(inputType, MfMtAacPayloadType, 0);

            var userData = BuildHeAacUserData(audioSpecificConfig);
            SetAttrBlob(inputType, MfMtUserData, userData);

            ThrowIfFailed(MftSetInputType(_decoder, 0, inputType, 0), "SetInputType(AAC)");
        }
        finally
        {
            ReleaseCom(inputType);
        }
    }

    private void ConfigureOutputType(int sampleRate, int channels)
    {
        for (uint index = 0; ; index++)
        {
            void* candidate = null;
            var hr = MftGetOutputAvailableType(_decoder, 0, index, &candidate);
            if (hr == MfENoMoreTypes)
            {
                throw new InvalidOperationException("找不到支援 16-bit PCM 輸出的 AAC 解碼格式。");
            }

            ThrowIfFailed(hr, "GetOutputAvailableType(AAC)");
            try
            {
                var subType = GetAttrGuid(candidate, MfMtSubtype);
                var bits = GetAttrUInt32(candidate, MfMtAudioBitsPerSample);
                if (subType == MfAudioFormatPcm && bits == 16)
                {
                    SetAttrUInt32(candidate, MfMtAudioSamplesPerSecond, (uint)sampleRate);
                    SetAttrUInt32(candidate, MfMtAudioNumChannels, (uint)channels);
                    ThrowIfFailed(MftSetOutputType(_decoder, 0, candidate, 0), "SetOutputType(AAC->PCM16)");

                    MftOutputStreamInfo info = default;
                    if (MftGetOutputStreamInfo(_decoder, 0, &info) >= 0 && info.CbSize > 0)
                    {
                        _outputBufferSize = info.CbSize;
                    }

                    return;
                }
            }
            finally
            {
                ReleaseCom(candidate);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _initialized = false;
        ReleaseOutputSample();

        if (_decoder is not null)
        {
            _ = MftProcessMessage(_decoder, MftMessageCommandFlush, 0);
            ReleaseCom(_decoder);
            _decoder = null;
        }

        if (_mfStarted)
        {
            _ = MFShutdown();
            _mfStarted = false;
        }
    }

    private static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
        {
            throw new COMException($"Media Foundation AAC {operation} 失敗 (0x{hr:X8})", hr);
        }
    }

    private static void ReleaseCom(void* ptr)
    {
        if (ptr is null)
        {
            return;
        }

        var vtbl = *(void***)ptr;
        ((delegate* unmanaged[Stdcall]<void*, uint>)vtbl[2])(ptr);
    }

    private static uint GetAttrUInt32(void* attrs, Guid key)
    {
        var vtbl = *(void***)attrs;
        uint val = 0;
        var hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, uint*, int>)vtbl[7])(attrs, &key, &val);
        return hr >= 0 ? val : 0;
    }

    private static Guid GetAttrGuid(void* attrs, Guid key)
    {
        var vtbl = *(void***)attrs;
        Guid val = default;
        var hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, Guid*, int>)vtbl[10])(attrs, &key, &val);
        return hr >= 0 ? val : Guid.Empty;
    }

    private static void SetAttrUInt32(void* attrs, Guid key, uint value)
    {
        var vtbl = *(void***)attrs;
        ThrowIfFailed(((delegate* unmanaged[Stdcall]<void*, Guid*, uint, int>)vtbl[21])(attrs, &key, value), "SetUINT32");
    }

    private static void SetAttrGuid(void* attrs, Guid key, Guid value)
    {
        var vtbl = *(void***)attrs;
        ThrowIfFailed(((delegate* unmanaged[Stdcall]<void*, Guid*, Guid*, int>)vtbl[24])(attrs, &key, &value), "SetGUID");
    }

    private static void SetAttrBlob(void* attrs, Guid key, byte[] blob)
    {
        var vtbl = *(void***)attrs;
        fixed (byte* ptr = blob)
        {
            ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, Guid*, byte*, uint, int>)vtbl[26])(attrs, &key, ptr, (uint)blob.Length),
                "SetBlob");
        }
    }

    private static int MftGetOutputStreamInfo(void* mft, uint streamId, MftOutputStreamInfo* info)
    {
        var vtbl = *(void***)mft;
        return ((delegate* unmanaged[Stdcall]<void*, uint, MftOutputStreamInfo*, int>)vtbl[7])(mft, streamId, info);
    }

    private static int MftGetOutputAvailableType(void* mft, uint streamId, uint typeIndex, void** type)
    {
        var vtbl = *(void***)mft;
        return ((delegate* unmanaged[Stdcall]<void*, uint, uint, void**, int>)vtbl[14])(mft, streamId, typeIndex, type);
    }

    private static int MftSetInputType(void* mft, uint streamId, void* type, uint flags)
    {
        var vtbl = *(void***)mft;
        return ((delegate* unmanaged[Stdcall]<void*, uint, void*, uint, int>)vtbl[15])(mft, streamId, type, flags);
    }

    private static int MftSetOutputType(void* mft, uint streamId, void* type, uint flags)
    {
        var vtbl = *(void***)mft;
        return ((delegate* unmanaged[Stdcall]<void*, uint, void*, uint, int>)vtbl[16])(mft, streamId, type, flags);
    }

    private static int MftProcessMessage(void* mft, uint message, nuint param)
    {
        var vtbl = *(void***)mft;
        return ((delegate* unmanaged[Stdcall]<void*, uint, nuint, int>)vtbl[23])(mft, message, param);
    }

    private static int MftProcessInput(void* mft, uint streamId, void* sample, uint flags)
    {
        var vtbl = *(void***)mft;
        return ((delegate* unmanaged[Stdcall]<void*, uint, void*, uint, int>)vtbl[24])(mft, streamId, sample, flags);
    }

    private static int MftProcessOutput(void* mft, uint flags, uint bufferCount, MftOutputDataBuffer* buffers, uint* status)
    {
        var vtbl = *(void***)mft;
        return ((delegate* unmanaged[Stdcall]<void*, uint, uint, MftOutputDataBuffer*, uint*, int>)vtbl[25])(mft, flags, bufferCount, buffers, status);
    }

    private static int BufferLock(void* buffer, byte** data, uint* maxLen, uint* curLen)
    {
        var vtbl = *(void***)buffer;
        return ((delegate* unmanaged[Stdcall]<void*, byte**, uint*, uint*, int>)vtbl[3])(buffer, data, maxLen, curLen);
    }

    private static int BufferUnlock(void* buffer)
    {
        var vtbl = *(void***)buffer;
        return ((delegate* unmanaged[Stdcall]<void*, int>)vtbl[4])(buffer);
    }

    private static int BufferSetCurrentLength(void* buffer, uint length)
    {
        var vtbl = *(void***)buffer;
        return ((delegate* unmanaged[Stdcall]<void*, uint, int>)vtbl[6])(buffer, length);
    }

    private static int SampleSetTime(void* sample, long hnsTime)
    {
        var vtbl = *(void***)sample;
        return ((delegate* unmanaged[Stdcall]<void*, long, int>)vtbl[36])(sample, hnsTime);
    }

    private static int SampleConvertToContiguousBuffer(void* sample, void** buffer)
    {
        var vtbl = *(void***)sample;
        return ((delegate* unmanaged[Stdcall]<void*, void**, int>)vtbl[41])(sample, buffer);
    }

    private static int SampleAddBuffer(void* sample, void* buffer)
    {
        var vtbl = *(void***)sample;
        return ((delegate* unmanaged[Stdcall]<void*, void*, int>)vtbl[42])(sample, buffer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftOutputStreamInfo
    {
        public uint DwFlags;
        public uint CbSize;
        public uint CbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftOutputDataBuffer
    {
        public uint StreamId;
        public void* Sample;
        public uint Status;
        public void* Events;
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(void** mediaType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateSample(void** sample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMemoryBuffer(uint maxLength, void** buffer);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(Guid* rclsid, void* pUnkOuter, uint dwClsContext, Guid* riid, void** ppv);
}
