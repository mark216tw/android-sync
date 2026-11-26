using System.Runtime.InteropServices;
using Com.AndroidSync.App.Models;
using Microsoft.Extensions.Logging;

namespace Com.AndroidSync.App.Rendering;

public sealed unsafe class MediaFoundationH264Decoder : IDisposable
{
    private const uint MfVersion = 0x00020070;
    private const uint MfStartupLite = 1;
    private const uint ClsctxInprocServer = 1;

    private const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    private const int MfETransformStreamChange = unchecked((int)0xC00D6D61);
    private const int MfENotAccepting = unchecked((int)0xC00D36B5);
    private const int MfENoMoreTypes = unchecked((int)0xC00D36B9);

    private const uint MftEnumFlagSyncMft = 0x00000001;
    private const uint MftEnumFlagSortAndFilter = 0x00000040;

    private const uint MftMessageCommandFlush = 0x00000000;
    private const uint MftMessageSetD3DManager = 0x00000002;
    private const uint MftMessageNotifyBeginStreaming = 0x10000000;
    private const uint MftMessageNotifyStartOfStream = 0x10000003;

    private const uint MftOutputStreamProvidesSamples = 0x00000100;
    private const uint MftOutputStreamCanProvideSamples = 0x00000200;

    private static readonly Guid ClsidCmsH264DecoderMft = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
    private static readonly Guid MftCategoryVideoDecoder = new("D6C02D4B-6833-45B4-971A-05A4B04BAB91");
    private static readonly Guid IidImfTransform = new("BF94C121-5B05-4E6F-8000-BA598961414D");
    private static readonly Guid IidImfDxgiBuffer = new("E7174CFA-1C9E-48B1-8866-626226BFC258");
    private static readonly Guid IidId3d11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private static readonly Guid MfSaD3D11Aware = new("206B4FC8-FCF9-4C51-AFE3-9764369E33A0");
    private static readonly Guid MfLowLatency = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");

    private static readonly Guid MfMtMajorType = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    private static readonly Guid MfMtSubtype = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    private static readonly Guid MfMtFrameSize = new("1652C33D-D6B2-4012-B834-72030849A37D");
    private static readonly Guid MfMtDefaultStride = new("644B4E48-1E02-4516-B0EB-C01CA9D49AC6");
    private static readonly Guid MfMtMinimumDisplayAperture = new("D7388766-18FE-48C6-A177-EE894867C8C4");
    private static readonly Guid MfMtGeometricAperture = new("66758743-7E5F-400D-980A-AA8596C85696");
    private static readonly Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfVideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfVideoFormatHevc = new("43564548-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfVideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");

    private readonly ILogger<MediaFoundationH264Decoder> _logger;
    private static readonly Lazy<bool> CachedHevcDecoderAvailable = new(ProbeHevcDecoderAvailable);
    private void* _decoder;
    private void* _activeOutputSample;
    private void* _activeOutputTexture;
    private void* _cpuOutputSample;
    private void* _cpuOutputBuffer;
    private uint _cpuOutputBufferCapacity;
    private bool _mfStarted;
    private bool _mftProvidesSamples;
    private VideoCodecPreference _activeCodec = VideoCodecPreference.H264;
    private int _width;
    private int _height;
    private int _codedWidth;
    private int _codedHeight;
    private int _cropX;
    private int _cropY;
    private int _stride;
    private uint _outputBufferSize;
    private byte[] _bgraBuffer = [];
    private bool _disposed;

    public MediaFoundationH264Decoder(ILogger<MediaFoundationH264Decoder> logger)
    {
        _logger = logger;
    }

    public int Width => _width;

    public int Height => _height;

    public bool IsHardwareAccelerated { get; private set; }

    public VideoCodecPreference ActiveCodec => _activeCodec;

    public static bool IsHevcDecoderAvailable() => CachedHevcDecoderAvailable.Value;

    private static bool ProbeHevcDecoderAvailable()
    {
        if (MFStartup(MfVersion, MfStartupLite) < 0)
        {
            return false;
        }

        try
        {
            var inputType = new MftRegisterTypeInfo { GuidMajorType = MfMediaTypeVideo, GuidSubtype = MfVideoFormatHevc };
            var outputType = new MftRegisterTypeInfo { GuidMajorType = MfMediaTypeVideo, GuidSubtype = MfVideoFormatNv12 };
            void** activates = null;
            uint count = 0;
            var hr = MFTEnumEx(
                MftCategoryVideoDecoder,
                MftEnumFlagSyncMft | MftEnumFlagSortAndFilter,
                &inputType,
                &outputType,
                &activates,
                &count);
            if (activates is not null)
            {
                for (uint i = 0; i < count; i++)
                {
                    ReleaseCom(activates[i]);
                }

                Marshal.FreeCoTaskMem((nint)activates);
            }

            return hr >= 0 && count > 0;
        }
        finally
        {
            _ = MFShutdown();
        }
    }

    public void Initialize(
        int initialWidth,
        int initialHeight,
        nint dxgiDeviceManager = 0,
        VideoCodecPreference codec = VideoCodecPreference.H264)
    {
        _width = initialWidth;
        _height = initialHeight;
        _codedWidth = initialWidth;
        _codedHeight = initialHeight;
        _cropX = 0;
        _cropY = 0;
        _stride = initialWidth;
        _activeCodec = codec;
        IsHardwareAccelerated = false;
        _mftProvidesSamples = false;

        ThrowIfFailed(MFStartup(MfVersion, MfStartupLite), "MFStartup");
        _mfStarted = true;

        _decoder = codec == VideoCodecPreference.H265
            ? CreateHevcDecoderMft()
            : CreateH264DecoderMft();

        ConfigureHardwareAcceleration(dxgiDeviceManager);
        ConfigureInputType(initialWidth, initialHeight);
        ConfigureOutputType();

        ThrowIfFailed(MftProcessMessage(_decoder, MftMessageNotifyBeginStreaming, 0), "NotifyBeginStreaming");
        ThrowIfFailed(MftProcessMessage(_decoder, MftMessageNotifyStartOfStream, 0), "NotifyStartOfStream");
    }

    public bool TryDecode(ReadOnlySpan<byte> annexBPacket, long ptsMicroseconds, out DecodedVideoFrame frame)
    {
        frame = default;
        if (_disposed || _decoder is null || annexBPacket.IsEmpty)
        {
            return false;
        }

        ReleaseOutputFrame();
        FeedInput(annexBPacket, ptsMicroseconds, onDrainedFrame: null);
        return TryPullOutputFrame(out frame);
    }

    public int DecodeFrames(ReadOnlySpan<byte> annexBPacket, long ptsMicroseconds, Action<DecodedVideoFrame> onFrame)
    {
        if (_disposed || _decoder is null || annexBPacket.IsEmpty)
        {
            return 0;
        }

        var delivered = 0;
        FeedInput(annexBPacket, ptsMicroseconds, frame =>
        {
            delivered++;
            onFrame(frame);
        });

        while (TryPullOutputFrame(out var frame))
        {
            try
            {
                delivered++;
                onFrame(frame);
            }
            finally
            {
                ReleaseOutputFrame();
            }
        }

        return delivered;
    }

    private void FeedInput(ReadOnlySpan<byte> annexBPacket, long ptsMicroseconds, Action<DecodedVideoFrame>? onDrainedFrame)
    {
        void* sample = null;
        void* buffer = null;
        try
        {
            ThrowIfFailed(MFCreateSample(&sample), "MFCreateSample");
            ThrowIfFailed(MFCreateMemoryBuffer((uint)annexBPacket.Length, &buffer), "MFCreateMemoryBuffer");

            byte* dstPtr = null;
            uint maxLen = 0;
            uint curLen = 0;
            ThrowIfFailed(BufferLock(buffer, &dstPtr, &maxLen, &curLen), "BufferLock");
            try
            {
                annexBPacket.CopyTo(new Span<byte>(dstPtr, annexBPacket.Length));
            }
            finally
            {
                BufferUnlock(buffer);
            }

            ThrowIfFailed(BufferSetCurrentLength(buffer, (uint)annexBPacket.Length), "SetCurrentLength");
            ThrowIfFailed(SampleAddBuffer(sample, buffer), "AddBuffer");
            SampleSetTime(sample, ptsMicroseconds * 10);

            var hr = MftProcessInput(_decoder, 0, sample, 0);
            if (hr == MfENotAccepting)
            {
                // Decoder output queue is full: drain pending frames before retrying ProcessInput.
                while (TryPullOutputFrame(out var drained))
                {
                    try
                    {
                        onDrainedFrame?.Invoke(drained);
                    }
                    finally
                    {
                        ReleaseOutputFrame();
                    }
                }

                hr = MftProcessInput(_decoder, 0, sample, 0);
            }

            if (hr < 0)
            {
                _logger.LogDebug("ProcessInput 回傳 0x{Hr:X8}", hr);
            }
        }
        finally
        {
            ReleaseCom(buffer);
            ReleaseCom(sample);
        }
    }

    private static void* CreateH264DecoderMft()
    {
        var clsid = ClsidCmsH264DecoderMft;
        var iid = IidImfTransform;
        void* mft = null;
        ThrowIfFailed(CoCreateInstance(&clsid, null, ClsctxInprocServer, &iid, &mft), "CoCreateInstance(H264MFT)");
        return mft;
    }

    private static void* CreateHevcDecoderMft()
    {
        var inputType = new MftRegisterTypeInfo { GuidMajorType = MfMediaTypeVideo, GuidSubtype = MfVideoFormatHevc };
        var outputType = new MftRegisterTypeInfo { GuidMajorType = MfMediaTypeVideo, GuidSubtype = MfVideoFormatNv12 };
        void** activates = null;
        uint count = 0;
        var hr = MFTEnumEx(
            MftCategoryVideoDecoder,
            MftEnumFlagSyncMft | MftEnumFlagSortAndFilter,
            &inputType,
            &outputType,
            &activates,
            &count);
        ThrowIfFailed(hr, "MFTEnumEx(HEVC)");

        if (activates is null || count == 0)
        {
            throw new InvalidOperationException("找不到 Windows HEVC (H.265) 解碼器，請安裝 Microsoft HEVC 視訊延伸模組或切換回 H.264。");
        }

        try
        {
            var iid = IidImfTransform;
            void* mft = null;
            for (uint i = 0; i < count; i++)
            {
                if (ActivateObject(activates[i], &iid, &mft) >= 0 && mft is not null)
                {
                    return mft;
                }
            }

            throw new InvalidOperationException("無法啟用 Windows HEVC (H.265) 解碼器 MFT。");
        }
        finally
        {
            for (uint i = 0; i < count; i++)
            {
                ReleaseCom(activates[i]);
            }

            Marshal.FreeCoTaskMem((nint)activates);
        }
    }

    public void ReleaseOutputFrame()
    {
        ReleaseCom(_activeOutputTexture);
        _activeOutputTexture = null;

        ReleaseCom(_activeOutputSample);
        _activeOutputSample = null;
    }

    private void ConfigureHardwareAcceleration(nint dxgiDeviceManager)
    {
        void* attributes = null;
        if (MftGetAttributes(_decoder, &attributes) >= 0 && attributes is not null)
        {
            try
            {
                // Request low-latency decoding when supported.
                TrySetAttrUInt32(attributes, MfLowLatency, 1);

                if (dxgiDeviceManager != 0 && GetAttrUInt32(attributes, MfSaD3D11Aware) != 0)
                {
                    var hr = MftProcessMessage(_decoder, MftMessageSetD3DManager, (nuint)dxgiDeviceManager);
                    if (hr >= 0)
                    {
                        IsHardwareAccelerated = true;
                        _logger.LogInformation("H.264 解碼器已啟用 D3D11 DXVA GPU 零拷貝硬體解碼。");
                    }
                    else
                    {
                        _logger.LogWarning("MFT_MESSAGE_SET_D3D_MANAGER 失敗 (0x{Hr:X8})，將退回 CPU 解碼路徑。", hr);
                    }
                }
            }
            finally
            {
                ReleaseCom(attributes);
            }
        }
    }

    private void EnsureCpuOutputSample(uint requiredBytes)
    {
        if (_cpuOutputSample is not null && _cpuOutputBuffer is not null && _cpuOutputBufferCapacity >= requiredBytes)
        {
            _ = BufferSetCurrentLength(_cpuOutputBuffer, 0);
            return;
        }

        ReleaseCpuOutputSample();

        void* sample = null;
        void* buffer = null;
        try
        {
            ThrowIfFailed(MFCreateSample(&sample), "MFCreateSample(Out)");
            ThrowIfFailed(MFCreateMemoryBuffer(requiredBytes, &buffer), "MFCreateMemoryBuffer(Out)");
            ThrowIfFailed(SampleAddBuffer(sample, buffer), "AddBuffer(Out)");
            _cpuOutputSample = sample;
            _cpuOutputBuffer = buffer;
            _cpuOutputBufferCapacity = requiredBytes;
        }
        catch
        {
            ReleaseCom(buffer);
            ReleaseCom(sample);
            throw;
        }
    }

    private void ReleaseCpuOutputSample()
    {
        ReleaseCom(_cpuOutputBuffer);
        _cpuOutputBuffer = null;
        ReleaseCom(_cpuOutputSample);
        _cpuOutputSample = null;
        _cpuOutputBufferCapacity = 0;
    }

    private bool TryPullOutputFrame(out DecodedVideoFrame frame)
    {
        frame = default;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            void* reusableSample = null;
            void* producedSample = null;
            void* events = null;
            var keepProducedSample = false;

            try
            {
                if (!_mftProvidesSamples)
                {
                    var allocSize = Math.Max(_outputBufferSize, (uint)(Math.Max(_codedWidth, _width) * Math.Max(_codedHeight, _height) * 2));
                    EnsureCpuOutputSample(allocSize);
                    reusableSample = _cpuOutputSample;
                }

                var outputData = new MftOutputDataBuffer
                {
                    StreamId = 0,
                    Sample = reusableSample,
                    Status = 0,
                    Events = null
                };

                uint status = 0;
                var hr = MftProcessOutput(_decoder, 0, 1, &outputData, &status);
                events = outputData.Events;
                producedSample = outputData.Sample;

                if (hr == MfETransformNeedMoreInput)
                {
                    return false;
                }

                if (hr == MfETransformStreamChange)
                {
                    ConfigureOutputType();
                    continue;
                }

                if (hr < 0 || producedSample is null)
                {
                    _logger.LogDebug("ProcessOutput 回傳 0x{Hr:X8}", hr);
                    return false;
                }

                if (IsHardwareAccelerated && TryExtractGpuTextureFrame(producedSample, out frame))
                {
                    keepProducedSample = true;
                    return true;
                }

                if (TryExtractCpuBgraFrame(producedSample, out frame))
                {
                    return true;
                }

                return false;
            }
            finally
            {
                ReleaseCom(events);
                if (!keepProducedSample && producedSample is not null && producedSample != _cpuOutputSample)
                {
                    ReleaseCom(producedSample);
                }
            }
        }

        return false;
    }

    private bool TryExtractGpuTextureFrame(void* sample, out DecodedVideoFrame frame)
    {
        frame = default;

        void* mediaBuffer = null;
        void* dxgiBuffer = null;
        try
        {
            if (SampleGetBufferByIndex(sample, 0, &mediaBuffer) < 0 || mediaBuffer is null)
            {
                return false;
            }

            var dxgiBufferGuid = IidImfDxgiBuffer;
            if (QueryInterface(mediaBuffer, &dxgiBufferGuid, &dxgiBuffer) < 0 || dxgiBuffer is null)
            {
                return false;
            }

            void* texture = null;
            var texGuid = IidId3d11Texture2D;
            if (DxgiBufferGetResource(dxgiBuffer, &texGuid, &texture) < 0 || texture is null)
            {
                return false;
            }

            uint subresourceIndex = 0;
            if (DxgiBufferGetSubresourceIndex(dxgiBuffer, &subresourceIndex) < 0)
            {
                ReleaseCom(texture);
                return false;
            }

            _activeOutputSample = sample;
            _activeOutputTexture = texture;
            frame = new DecodedVideoFrame(
                _width,
                _height,
                _width * 4,
                BgraData: null,
                NativeTexture: (nint)texture,
                SubresourceIndex: subresourceIndex,
                CodedWidth: _codedWidth,
                CodedHeight: _codedHeight,
                CropX: _cropX,
                CropY: _cropY);
            return true;
        }
        finally
        {
            ReleaseCom(dxgiBuffer);
            ReleaseCom(mediaBuffer);
        }
    }

    private bool TryExtractCpuBgraFrame(void* sample, out DecodedVideoFrame frame)
    {
        frame = default;
        void* contiguous = null;
        try
        {
            ThrowIfFailed(SampleConvertToContiguousBuffer(sample, &contiguous), "ConvertToContiguousBuffer");
            byte* dataPtr = null;
            uint maxLen = 0;
            uint curLen = 0;
            ThrowIfFailed(BufferLock(contiguous, &dataPtr, &maxLen, &curLen), "OutBufferLock");
            try
            {
                var neededBgra = _width * _height * 4;
                if (_bgraBuffer.Length != neededBgra)
                {
                    _bgraBuffer = new byte[neededBgra];
                }

                var nv12Span = new ReadOnlySpan<byte>(dataPtr, (int)curLen);
                Nv12ColorConverter.ConvertNv12ToBgra(
                    nv12Span,
                    _width,
                    _height,
                    _stride,
                    _codedHeight,
                    _cropX,
                    _cropY,
                    _bgraBuffer);
                frame = new DecodedVideoFrame(_width, _height, _width * 4, _bgraBuffer);
                return true;
            }
            finally
            {
                BufferUnlock(contiguous);
            }
        }
        finally
        {
            ReleaseCom(contiguous);
        }
    }

    private void ConfigureInputType(int width, int height)
    {
        void* inputType = null;
        try
        {
            ThrowIfFailed(MFCreateMediaType(&inputType), "MFCreateMediaType(In)");
            SetAttrGuid(inputType, MfMtMajorType, MfMediaTypeVideo);
            SetAttrGuid(
                inputType,
                MfMtSubtype,
                _activeCodec == VideoCodecPreference.H265 ? MfVideoFormatHevc : MfVideoFormatH264);
            if (width > 0 && height > 0)
            {
                var packedSize = ((ulong)(uint)width << 32) | (uint)height;
                SetAttrUInt64(inputType, MfMtFrameSize, packedSize);
            }

            ThrowIfFailed(MftSetInputType(_decoder, 0, inputType, 0), "SetInputType");
        }
        finally
        {
            ReleaseCom(inputType);
        }
    }

    private void ConfigureOutputType()
    {
        for (uint index = 0; ; index++)
        {
            void* candidate = null;
            var hr = MftGetOutputAvailableType(_decoder, 0, index, &candidate);
            if (hr == MfENoMoreTypes)
            {
                throw new InvalidOperationException("找不到支援 NV12 輸出的 H.264 解碼格式。");
            }

            ThrowIfFailed(hr, "GetOutputAvailableType");
            try
            {
                var subType = GetAttrGuid(candidate, MfMtSubtype);
                if (subType == MfVideoFormatNv12)
                {
                    ThrowIfFailed(MftSetOutputType(_decoder, 0, candidate, 0), "SetOutputType(NV12)");

                    void* currentType = null;
                    var hasCurrentType = MftGetOutputCurrentType(_decoder, 0, &currentType) >= 0 && currentType is not null;
                    try
                    {
                        var typeToInspect = hasCurrentType ? currentType : candidate;

                        var packedSize = GetAttrUInt64(typeToInspect, MfMtFrameSize);
                        if (packedSize != 0)
                        {
                            _codedWidth = (int)(packedSize >> 32);
                            _codedHeight = (int)(packedSize & 0xFFFFFFFF);
                        }

                        var stride = GetAttrUInt32(typeToInspect, MfMtDefaultStride);
                        _stride = stride != 0 ? Math.Abs((int)stride) : _codedWidth;

                        var (cropX, cropY, displayWidth, displayHeight) = ResolveDisplayAperture(
                            typeToInspect,
                            _codedWidth,
                            _codedHeight);
                        _cropX = cropX;
                        _cropY = cropY;
                        _width = displayWidth;
                        _height = displayHeight;
                    }
                    finally
                    {
                        ReleaseCom(currentType);
                    }

                    MftOutputStreamInfo streamInfo = default;
                    if (MftGetOutputStreamInfo(_decoder, 0, &streamInfo) >= 0)
                    {
                        _outputBufferSize = streamInfo.CbSize;
                        _mftProvidesSamples = (streamInfo.DwFlags & (MftOutputStreamProvidesSamples | MftOutputStreamCanProvideSamples)) != 0;
                    }

                    _logger.LogInformation(
                        "H.264 輸出格式: coded={CodedWidth}x{CodedHeight}, stride={Stride}, crop=({CropX},{CropY},{Width}x{Height}), hw={Hw}, providesSamples={ProvidesSamples}",
                        _codedWidth,
                        _codedHeight,
                        _stride,
                        _cropX,
                        _cropY,
                        _width,
                        _height,
                        IsHardwareAccelerated,
                        _mftProvidesSamples);

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
        ReleaseOutputFrame();
        ReleaseCpuOutputSample();

        if (_decoder is not null)
        {
            MftProcessMessage(_decoder, MftMessageCommandFlush, 0);
            ReleaseCom(_decoder);
            _decoder = null;
        }

        if (_mfStarted)
        {
            MFShutdown();
            _mfStarted = false;
        }
    }

    private static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
        {
            throw new COMException($"Media Foundation {operation} 失敗 (0x{hr:X8})", hr);
        }
    }

    private static int QueryInterface(void* obj, Guid* iid, void** ppv)
    {
        var vtbl = *(void***)obj;
        return ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)vtbl[0])(obj, iid, ppv);
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

    private static void TrySetAttrUInt32(void* attrs, Guid key, uint value)
    {
        var vtbl = *(void***)attrs;
        ((delegate* unmanaged[Stdcall]<void*, Guid*, uint, int>)vtbl[21])(attrs, &key, value);
    }

    private static void SetAttrGuid(void* attrs, Guid key, Guid value)
    {
        var vtbl = *(void***)attrs;
        ThrowIfFailed(((delegate* unmanaged[Stdcall]<void*, Guid*, Guid*, int>)vtbl[24])(attrs, &key, &value), "SetGUID");
    }

    private static void SetAttrUInt64(void* attrs, Guid key, ulong value)
    {
        var vtbl = *(void***)attrs;
        ThrowIfFailed(((delegate* unmanaged[Stdcall]<void*, Guid*, ulong, int>)vtbl[22])(attrs, &key, value), "SetUINT64");
    }

    private static Guid GetAttrGuid(void* attrs, Guid key)
    {
        var vtbl = *(void***)attrs;
        Guid val = default;
        var hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, Guid*, int>)vtbl[10])(attrs, &key, &val);
        return hr >= 0 ? val : Guid.Empty;
    }

    private static ulong GetAttrUInt64(void* attrs, Guid key)
    {
        var vtbl = *(void***)attrs;
        ulong val = 0;
        var hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, ulong*, int>)vtbl[8])(attrs, &key, &val);
        return hr >= 0 ? val : 0;
    }

    private static (int CropX, int CropY, int DisplayWidth, int DisplayHeight) ResolveDisplayAperture(
        void* mediaType,
        int codedWidth,
        int codedHeight)
    {
        if (TryGetAttrVideoArea(mediaType, MfMtMinimumDisplayAperture, out var minAperture)
            && TryValidateAperture(
                minAperture.OffsetX.Value,
                minAperture.OffsetY.Value,
                minAperture.Width,
                minAperture.Height,
                codedWidth,
                codedHeight,
                out var result))
        {
            return result;
        }

        if (TryGetAttrVideoArea(mediaType, MfMtGeometricAperture, out var geoAperture)
            && TryValidateAperture(
                geoAperture.OffsetX.Value,
                geoAperture.OffsetY.Value,
                geoAperture.Width,
                geoAperture.Height,
                codedWidth,
                codedHeight,
                out result))
        {
            return result;
        }

        return (0, 0, codedWidth, codedHeight);
    }

    public static bool TryValidateAperture(
        int offsetX,
        int offsetY,
        int areaWidth,
        int areaHeight,
        int codedWidth,
        int codedHeight,
        out (int CropX, int CropY, int DisplayWidth, int DisplayHeight) result)
    {
        if (offsetX >= 0
            && offsetY >= 0
            && areaWidth > 0
            && areaHeight > 0
            && offsetX + areaWidth <= codedWidth
            && offsetY + areaHeight <= codedHeight)
        {
            result = (offsetX, offsetY, areaWidth, areaHeight);
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryGetAttrVideoArea(void* attrs, Guid key, out MfVideoArea area)
    {
        area = default;
        var vtbl = *(void***)attrs;
        uint blobSize = 0;
        fixed (MfVideoArea* areaPtr = &area)
        {
            var hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, byte*, uint, uint*, int>)vtbl[15])(
                attrs,
                &key,
                (byte*)areaPtr,
                (uint)sizeof(MfVideoArea),
                &blobSize);
            return hr >= 0 && blobSize >= (uint)sizeof(MfVideoArea);
        }
    }

    private static uint GetAttrUInt32(void* attrs, Guid key)
    {
        var vtbl = *(void***)attrs;
        uint val = 0;
        var hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, uint*, int>)vtbl[7])(attrs, &key, &val);
        return hr >= 0 ? val : 0;
    }

    private static int MftGetOutputStreamInfo(void* mft, uint streamId, MftOutputStreamInfo* info)
    {
        var vtbl = *(void***)mft;
        return ((delegate* unmanaged[Stdcall]<void*, uint, MftOutputStreamInfo*, int>)vtbl[7])(mft, streamId, info);
    }

    private static int MftGetAttributes(void* mft, void** attributes)
    {
        var vtbl = *(void***)mft;
        return ((delegate* unmanaged[Stdcall]<void*, void**, int>)vtbl[8])(mft, attributes);
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

    private static int MftGetOutputCurrentType(void* mft, uint streamId, void** type)
    {
        var vtbl = *(void***)mft;
        return ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)vtbl[18])(mft, streamId, type);
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

    private static int DxgiBufferGetResource(void* dxgiBuffer, Guid* iid, void** resource)
    {
        var vtbl = *(void***)dxgiBuffer;
        return ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)vtbl[3])(dxgiBuffer, iid, resource);
    }

    private static int DxgiBufferGetSubresourceIndex(void* dxgiBuffer, uint* subresourceIndex)
    {
        var vtbl = *(void***)dxgiBuffer;
        return ((delegate* unmanaged[Stdcall]<void*, uint*, int>)vtbl[4])(dxgiBuffer, subresourceIndex);
    }

    private static int SampleSetTime(void* sample, long hnsTime)
    {
        var vtbl = *(void***)sample;
        return ((delegate* unmanaged[Stdcall]<void*, long, int>)vtbl[36])(sample, hnsTime);
    }

    private static int SampleGetBufferByIndex(void* sample, uint index, void** buffer)
    {
        var vtbl = *(void***)sample;
        return ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)vtbl[40])(sample, index, buffer);
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

    private static int ActivateObject(void* activate, Guid* iid, void** ppv)
    {
        var vtbl = *(void***)activate;
        return ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)vtbl[33])(activate, iid, ppv);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftRegisterTypeInfo
    {
        public Guid GuidMajorType;
        public Guid GuidSubtype;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MfOffset
    {
        public ushort Fract;
        public short Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MfVideoArea
    {
        public MfOffset OffsetX;
        public MfOffset OffsetY;
        public int Width;
        public int Height;
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

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFTEnumEx(
        Guid guidCategory,
        uint flags,
        MftRegisterTypeInfo* pInputType,
        MftRegisterTypeInfo* pOutputType,
        void*** pppMftActivate,
        uint* pcMftActivate);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(Guid* rclsid, void* pUnkOuter, uint dwClsContext, Guid* riid, void** ppv);
}
