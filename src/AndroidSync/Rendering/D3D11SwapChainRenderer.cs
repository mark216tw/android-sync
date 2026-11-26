using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace Com.AndroidSync.App.Rendering;

public sealed unsafe class D3D11SwapChainRenderer : IDisposable
{
    private const uint D3DDriverTypeHardware = 1;
    private const uint D3DDriverTypeWarp = 5;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const uint D3D11CreateDeviceVideoSupport = 0x800;
    private const uint D3D11SdkVersion = 7;

    private const uint DxgiFormatB8G8R8A8Unorm = 87;
    private const uint DxgiUsageRenderTargetOutput = 0x20;
    private const uint DxgiSwapEffectFlipSequential = 3;
    private const uint DxgiScalingStretch = 0;
    private const uint DxgiAlphaModeIgnore = 3;
    private const uint DxgiSwapChainFlagFrameLatencyWaitableObject = 0x40;

    private const uint D3D11UsageDefault = 0;
    private const uint D3D11UsageStaging = 3;
    private const uint D3D11CpuAccessRead = 0x20000;
    private const uint D3D11MapRead = 1;
    private const uint D3D11VideoFrameFormatProgressive = 0;
    private const uint D3D11VideoUsagePlaybackNormal = 0;
    private const uint D3D11VpivDimensionTexture2D = 1;
    private const uint D3D11VpovDimensionTexture2D = 1;

    private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    private const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);

    private static readonly Guid IidIdxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid IidIdxgiFactory2 = new("50C83A1C-E072-4C48-87B0-3630FA36A6D0");
    private static readonly Guid IidIdxgiSwapChain2 = new("A8BE2AC4-199F-4946-B331-79599FB98DE7");
    private static readonly Guid IidId3d11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static readonly Guid IidId3d10Multithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");
    private static readonly Guid IidId3d11VideoDevice = new("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");
    private static readonly Guid IidId3d11VideoContext = new("61F21C45-3C0E-4A74-9CEA-67100D9AD5E4");
    private static readonly Guid IidISwapChainPanelNative = new("63AAD0B8-7C24-40FF-85A8-640D944CC325");

    private readonly ILogger<D3D11SwapChainRenderer> _logger;
    private readonly Lock _renderLock = new();

    private void* _device;
    private void* _context;
    private void* _videoDevice;
    private void* _videoContext;
    private void* _dxgiDeviceManager;
    private void* _videoEnumerator;
    private void* _videoProcessor;
    private int _vpInputWidth;
    private int _vpInputHeight;
    private int _vpOutputWidth;
    private int _vpOutputHeight;
    private int _vpLastCropX = -1;
    private int _vpLastCropY = -1;
    private int _vpLastDisplayWidth = -1;
    private int _vpLastDisplayHeight = -1;

    private void* _swapChain;
    private void* _swapChain2;
    private void* _panelNative;
    private void* _cachedBackBuffer;
    private void* _cachedOutputView;
    private void* _cachedInputTexture;
    private readonly Dictionary<uint, nint> _cachedInputViews = [];
    private uint _swapChainFlags;
    private void* _lastGpuFrameTexture;
    private int _lastGpuFrameWidth;
    private int _lastGpuFrameHeight;
    private bool _hasRenderedGpuFrame;
    private bool _gpuBackupDirty;

    private int _width;
    private int _height;
    private double _displayWidth;
    private double _displayHeight;
    private float _compositionScaleX = 1.0f;
    private float _compositionScaleY = 1.0f;
    private byte[]? _lastFrameData;
    private int _lastFrameStride;
    private bool _deviceLostNotified;
    private bool _disposed;

    public D3D11SwapChainRenderer(ILogger<D3D11SwapChainRenderer> logger)
    {
        _logger = logger;
    }

    public event EventHandler? DeviceLost;

    public float CompositionScaleX => _compositionScaleX;

    public float CompositionScaleY => _compositionScaleY;

    public static bool IsDeviceLostHResult(int hr) =>
        hr is DxgiErrorDeviceRemoved or DxgiErrorDeviceReset;

    public bool IsGpuZeroCopySupported
    {
        get
        {
            lock (_renderLock)
            {
                return _videoDevice is not null && _videoContext is not null && _dxgiDeviceManager is not null;
            }
        }
    }

    public nint GetDxgiDeviceManager()
    {
        lock (_renderLock)
        {
            if (_disposed)
            {
                return 0;
            }

            EnsureDeviceCreated();
            return (nint)_dxgiDeviceManager;
        }
    }

    public void Attach(SwapChainPanel panel, int initialWidth, int initialHeight)
    {
        lock (_renderLock)
        {
            CleanupSwapChain();

            _width = Math.Max(initialWidth, 1);
            _height = Math.Max(initialHeight, 1);

            EnsureDeviceCreated();
            CreateSwapChain(_width, _height);

            var panelUnknown = ((IWinRTObject)panel).NativeObject.ThisPtr;
            var iid = IidISwapChainPanelNative;
            void* nativePanel = null;
            var hr = QueryInterface((void*)panelUnknown, &iid, &nativePanel);
            ThrowIfFailed(hr, "QueryInterface(ISwapChainPanelNative)");
            _panelNative = nativePanel;

            SetPanelSwapChain(_panelNative, _swapChain);
            ApplyMatrixTransform();
            _logger.LogInformation(
                "已建立 D3D11 SwapChainPanel 渲染器 ({Width}x{Height}, ZeroCopy={ZeroCopy})",
                _width,
                _height,
                _dxgiDeviceManager is not null);
        }
    }

    public void UpdateDisplaySize(
        double displayWidth,
        double displayHeight,
        float compositionScaleX = 1.0f,
        float compositionScaleY = 1.0f)
    {
        if (displayWidth <= 0 || displayHeight <= 0)
        {
            return;
        }

        lock (_renderLock)
        {
            if (_disposed)
            {
                return;
            }

            _displayWidth = displayWidth;
            _displayHeight = displayHeight;
            _compositionScaleX = compositionScaleX > 0 ? compositionScaleX : 1.0f;
            _compositionScaleY = compositionScaleY > 0 ? compositionScaleY : 1.0f;

            if (_swapChain is null || _context is null)
            {
                return;
            }

            ApplyMatrixTransform();

            if (_hasRenderedGpuFrame)
            {
                if (_gpuBackupDirty && _cachedBackBuffer is not null && EnsureLastGpuFrameTexture(_width, _height))
                {
                    ContextCopyResource(_context, _lastGpuFrameTexture, _cachedBackBuffer);
                    _gpuBackupDirty = false;
                }

                if (_lastGpuFrameTexture is not null && _lastGpuFrameWidth == _width && _lastGpuFrameHeight == _height)
                {
                    PresentGpuBackupTexture();
                }
            }
            else if (_lastFrameData is not null && _lastFrameStride > 0)
            {
                PresentBgraBuffer(_lastFrameData, _lastFrameStride);
            }
        }
    }

    public bool TryCaptureCurrentFrameBgra(out byte[] bgraData, out int width, out int height)
    {
        lock (_renderLock)
        {
            bgraData = [];
            width = 0;
            height = 0;

            if (_disposed || _width <= 0 || _height <= 0)
            {
                return false;
            }

            if (_lastFrameData is not null && _lastFrameData.Length >= _width * _height * 4)
            {
                bgraData = new byte[_width * _height * 4];
                Buffer.BlockCopy(_lastFrameData, 0, bgraData, 0, bgraData.Length);
                width = _width;
                height = _height;
                return true;
            }

            var sourceTexture = _hasRenderedGpuFrame && _cachedBackBuffer is not null
                ? _cachedBackBuffer
                : _lastGpuFrameTexture;
            if (_device is null || _context is null || sourceTexture is null)
            {
                return false;
            }

            var w = _width;
            var h = _height;
            var stagingDesc = new D3D11Texture2DDesc
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = DxgiFormatB8G8R8A8Unorm,
                SampleCount = 1,
                SampleQuality = 0,
                Usage = D3D11UsageStaging,
                BindFlags = 0,
                CpuAccessFlags = D3D11CpuAccessRead,
                MiscFlags = 0
            };

            void* staging = null;
            try
            {
                if (DeviceCreateTexture2D(_device, &stagingDesc, null, &staging) < 0 || staging is null)
                {
                    return false;
                }

                ContextCopyResource(_context, staging, sourceTexture);

                D3D11MappedSubresource mapped = default;
                if (ContextMap(_context, staging, 0, D3D11MapRead, 0, &mapped) < 0 || mapped.Data is null)
                {
                    return false;
                }

                try
                {
                    var rowBytes = w * 4;
                    var result = new byte[rowBytes * h];
                    var srcBase = (byte*)mapped.Data;
                    fixed (byte* dstBase = result)
                    {
                        for (var y = 0; y < h; y++)
                        {
                            Buffer.MemoryCopy(
                                srcBase + (y * mapped.RowPitch),
                                dstBase + (y * rowBytes),
                                rowBytes,
                                rowBytes);
                        }
                    }

                    bgraData = result;
                    width = w;
                    height = h;
                    return true;
                }
                finally
                {
                    ContextUnmap(_context, staging, 0);
                }
            }
            finally
            {
                ReleaseCom(staging);
            }
        }
    }

    public void RecoverDevice(SwapChainPanel? panel)
    {
        lock (_renderLock)
        {
            if (_disposed)
            {
                return;
            }

            _logger.LogWarning("偵測到 Direct3D 11 裝置遺失，正在重建 GPU 裝置與 SwapChain...");
            if (_panelNative is not null)
            {
                SetPanelSwapChain(_panelNative, null);
            }

            CleanupSwapChain();
            CleanupDevice();
            _deviceLostNotified = false;

            EnsureDeviceCreated();
            if (panel is not null && _width > 0 && _height > 0)
            {
                CreateSwapChain(_width, _height);
                var panelUnknown = ((IWinRTObject)panel).NativeObject.ThisPtr;
                var iid = IidISwapChainPanelNative;
                void* nativePanel = null;
                var hr = QueryInterface((void*)panelUnknown, &iid, &nativePanel);
                ThrowIfFailed(hr, "QueryInterface(ISwapChainPanelNative)");
                _panelNative = nativePanel;
                SetPanelSwapChain(_panelNative, _swapChain);
                ApplyMatrixTransform();
            }
        }
    }

    public static (float ScaleX, float ScaleY) ComputeScaleFactor(
        int bufferWidth,
        int bufferHeight,
        double displayWidth,
        double displayHeight)
    {
        if (bufferWidth <= 0 || bufferHeight <= 0 || displayWidth <= 0 || displayHeight <= 0)
        {
            return (1.0f, 1.0f);
        }

        return ((float)(displayWidth / bufferWidth), (float)(displayHeight / bufferHeight));
    }

    public void RenderFrame(DecodedVideoFrame frame)
    {
        lock (_renderLock)
        {
            if (_disposed || _swapChain is null || _context is null || frame.Width <= 0 || frame.Height <= 0)
            {
                return;
            }

            if (frame.Width != _width || frame.Height != _height)
            {
                ResizeSwapChain(frame.Width, frame.Height);
            }

            if (frame.IsGpuTexture && _videoDevice is not null && _videoContext is not null)
            {
                var codedWidth = frame.CodedWidth > 0 ? frame.CodedWidth : frame.Width;
                var codedHeight = frame.CodedHeight > 0 ? frame.CodedHeight : frame.Height;
                PresentGpuNv12Texture(
                    (void*)frame.NativeTexture,
                    frame.SubresourceIndex,
                    codedWidth,
                    codedHeight,
                    frame.CropX,
                    frame.CropY,
                    frame.Width,
                    frame.Height);
                return;
            }

            if (frame.BgraData is null || frame.BgraData.Length == 0)
            {
                return;
            }

            ReleaseLastGpuFrameTexture();
            if (_lastFrameData is null || _lastFrameData.Length != frame.BgraData.Length)
            {
                _lastFrameData = new byte[frame.BgraData.Length];
            }

            Buffer.BlockCopy(frame.BgraData, 0, _lastFrameData, 0, frame.BgraData.Length);
            _lastFrameStride = frame.Stride;

            PresentBgraBuffer(frame.BgraData, frame.Stride);
        }
    }

    private void PresentGpuNv12Texture(
        void* nv12Texture,
        uint subresourceIndex,
        int codedWidth,
        int codedHeight,
        int cropX,
        int cropY,
        int displayWidth,
        int displayHeight)
    {
        if (!EnsureVideoProcessor(codedWidth, codedHeight, displayWidth, displayHeight))
        {
            return;
        }

        if (!EnsureCachedOutputView())
        {
            return;
        }

        var inputView = GetOrCreateInputView(nv12Texture, subresourceIndex);
        if (inputView is null)
        {
            return;
        }

        if (_vpLastCropX != cropX || _vpLastCropY != cropY
            || _vpLastDisplayWidth != displayWidth || _vpLastDisplayHeight != displayHeight)
        {
            var srcRect = new NativeRect
            {
                Left = cropX,
                Top = cropY,
                Right = cropX + displayWidth,
                Bottom = cropY + displayHeight
            };
            var dstRect = new NativeRect
            {
                Left = 0,
                Top = 0,
                Right = displayWidth,
                Bottom = displayHeight
            };

            VideoContextSetStreamSourceRect(_videoContext, _videoProcessor, 0, 1, &srcRect);
            VideoContextSetStreamDestRect(_videoContext, _videoProcessor, 0, 1, &dstRect);
            VideoContextSetOutputTargetRect(_videoContext, _videoProcessor, 1, &dstRect);

            _vpLastCropX = cropX;
            _vpLastCropY = cropY;
            _vpLastDisplayWidth = displayWidth;
            _vpLastDisplayHeight = displayHeight;
        }

        var stream = new D3D11VideoProcessorStream
        {
            Enable = 1,
            OutputIndex = 0,
            InputFrameOrField = 0,
            PastFrames = 0,
            FutureFrames = 0,
            PastSurfaces = null,
            InputSurface = inputView,
            FutureSurfaces = null,
            PastSurfacesRight = null,
            InputSurfaceRight = null,
            FutureSurfacesRight = null
        };

        var hr = VideoContextBlt(_videoContext, _videoProcessor, _cachedOutputView, 0, 1, &stream);
        if (hr < 0)
        {
            HandlePotentialDeviceLost(hr, "VideoProcessorBlt");
            return;
        }

        _lastFrameData = null;
        _lastFrameStride = 0;
        _hasRenderedGpuFrame = true;
        _gpuBackupDirty = true;

        var presentHr = SwapChainPresent(_swapChain, 0, 0);
        if (presentHr < 0)
        {
            HandlePotentialDeviceLost(presentHr, "SwapChainPresent");
        }
    }

    private void* GetOrCreateInputView(void* nv12Texture, uint subresourceIndex)
    {
        if (_cachedInputTexture != nv12Texture)
        {
            ReleaseCachedInputViews();
            _cachedInputTexture = nv12Texture;
        }

        if (_cachedInputViews.TryGetValue(subresourceIndex, out var cached))
        {
            return (void*)cached;
        }

        var inViewDesc = new D3D11VideoProcessorInputViewDesc
        {
            FourCC = 0,
            ViewDimension = D3D11VpivDimensionTexture2D,
            MipSlice = 0,
            ArraySlice = subresourceIndex
        };
        void* inputView = null;
        var hr = VideoDeviceCreateVideoProcessorInputView(_videoDevice, nv12Texture, _videoEnumerator, &inViewDesc, &inputView);
        if (hr < 0 || inputView is null)
        {
            HandlePotentialDeviceLost(hr, "CreateVideoProcessorInputView");
            return null;
        }

        _cachedInputViews[subresourceIndex] = (nint)inputView;
        return inputView;
    }

    private void ReleaseCachedInputViews()
    {
        foreach (var view in _cachedInputViews.Values)
        {
            ReleaseCom((void*)view);
        }

        _cachedInputViews.Clear();
        _cachedInputTexture = null;
    }

    private bool EnsureCachedOutputView()
    {
        if (_cachedBackBuffer is not null && _cachedOutputView is not null)
        {
            return true;
        }

        ReleaseCachedOutputView();

        var texGuid = IidId3d11Texture2D;
        void* backBuffer = null;
        var hr = SwapChainGetBuffer(_swapChain, 0, &texGuid, &backBuffer);
        if (hr < 0 || backBuffer is null)
        {
            HandlePotentialDeviceLost(hr, "SwapChainGetBuffer");
            return false;
        }

        var outViewDesc = new D3D11VideoProcessorOutputViewDesc
        {
            ViewDimension = D3D11VpovDimensionTexture2D,
            MipSlice = 0
        };
        void* outputView = null;
        hr = VideoDeviceCreateVideoProcessorOutputView(_videoDevice, backBuffer, _videoEnumerator, &outViewDesc, &outputView);
        if (hr < 0 || outputView is null)
        {
            ReleaseCom(backBuffer);
            HandlePotentialDeviceLost(hr, "CreateVideoProcessorOutputView");
            return false;
        }

        _cachedBackBuffer = backBuffer;
        _cachedOutputView = outputView;
        return true;
    }

    private void ReleaseCachedOutputView()
    {
        ReleaseCom(_cachedOutputView);
        _cachedOutputView = null;

        ReleaseCom(_cachedBackBuffer);
        _cachedBackBuffer = null;
    }

    private void HandlePotentialDeviceLost(int hr, string operation)
    {
        if (IsDeviceLostHResult(hr))
        {
            if (!_deviceLostNotified)
            {
                _deviceLostNotified = true;
                _logger.LogError("Direct3D 11 裝置遺失 ({Operation}: 0x{Hr:X8})", operation, hr);
                var handler = DeviceLost;
                if (handler is not null)
                {
                    ThreadPool.QueueUserWorkItem(_ => handler.Invoke(this, EventArgs.Empty));
                }
            }
        }
        else
        {
            _logger.LogWarning("{Operation} 失敗 (0x{Hr:X8})", operation, hr);
        }
    }

    private void PresentGpuBackupTexture()
    {
        if (_lastGpuFrameTexture is null)
        {
            return;
        }

        void* backBuffer = null;
        try
        {
            var texGuid = IidId3d11Texture2D;
            var hr = SwapChainGetBuffer(_swapChain, 0, &texGuid, &backBuffer);
            if (hr < 0 || backBuffer is null)
            {
                return;
            }

            ContextCopyResource(_context, backBuffer, _lastGpuFrameTexture);
            SwapChainPresent(_swapChain, 0, 0);
        }
        finally
        {
            ReleaseCom(backBuffer);
        }
    }

    private void PresentBgraBuffer(byte[] bgraData, int stride)
    {
        void* backBuffer = null;
        try
        {
            var texGuid = IidId3d11Texture2D;
            var hr = SwapChainGetBuffer(_swapChain, 0, &texGuid, &backBuffer);
            if (hr < 0)
            {
                return;
            }

            fixed (byte* srcPtr = bgraData)
            {
                ContextUpdateSubresource(_context, backBuffer, 0, null, srcPtr, (uint)stride, 0);
            }

            var presentHr = SwapChainPresent(_swapChain, 0, 0);
            if (presentHr < 0)
            {
                HandlePotentialDeviceLost(presentHr, "SwapChainPresent(CPU)");
            }
        }
        finally
        {
            ReleaseCom(backBuffer);
        }
    }

    public void Detach()
    {
        lock (_renderLock)
        {
            if (_panelNative is not null)
            {
                SetPanelSwapChain(_panelNative, null);
            }

            CleanupSwapChain();
        }
    }

    private void EnsureDeviceCreated()
    {
        if (_device is not null)
        {
            return;
        }

        void* device = null;
        void* context = null;
        uint featureLevel = 0;

        // Prefer hardware D3D11 device with VIDEO_SUPPORT for zero-copy DXVA decoding.
        var hr = D3D11CreateDevice(
            null,
            D3DDriverTypeHardware,
            0,
            D3D11CreateDeviceBgraSupport | D3D11CreateDeviceVideoSupport,
            null,
            0,
            D3D11SdkVersion,
            &device,
            &featureLevel,
            &context);

        if (hr < 0)
        {
            hr = D3D11CreateDevice(
                null,
                D3DDriverTypeHardware,
                0,
                D3D11CreateDeviceBgraSupport,
                null,
                0,
                D3D11SdkVersion,
                &device,
                &featureLevel,
                &context);
        }

        if (hr < 0)
        {
            hr = D3D11CreateDevice(
                null,
                D3DDriverTypeWarp,
                0,
                D3D11CreateDeviceBgraSupport,
                null,
                0,
                D3D11SdkVersion,
                &device,
                &featureLevel,
                &context);
            ThrowIfFailed(hr, "D3D11CreateDevice(WARP)");
        }

        _device = device;
        _context = context;

        EnableMultithreadProtection(_device);
        InitializeVideoSupport();
    }

    private void InitializeVideoSupport()
    {
        void* videoDevice = null;
        void* videoContext = null;
        var videoDeviceGuid = IidId3d11VideoDevice;
        var videoContextGuid = IidId3d11VideoContext;

        if (QueryInterface(_device, &videoDeviceGuid, &videoDevice) < 0 || videoDevice is null)
        {
            return;
        }

        if (QueryInterface(_context, &videoContextGuid, &videoContext) < 0 || videoContext is null)
        {
            ReleaseCom(videoDevice);
            return;
        }

        uint resetToken = 0;
        void* manager = null;
        var hr = MFCreateDXGIDeviceManager(&resetToken, &manager);
        if (hr < 0 || manager is null)
        {
            ReleaseCom(videoContext);
            ReleaseCom(videoDevice);
            return;
        }

        hr = DxgiDeviceManagerResetDevice(manager, _device, resetToken);
        if (hr < 0)
        {
            ReleaseCom(manager);
            ReleaseCom(videoContext);
            ReleaseCom(videoDevice);
            return;
        }

        _videoDevice = videoDevice;
        _videoContext = videoContext;
        _dxgiDeviceManager = manager;
    }

    private bool EnsureVideoProcessor(int inputWidth, int inputHeight, int outputWidth, int outputHeight)
    {
        if (_videoDevice is null || _videoContext is null)
        {
            return false;
        }

        if (_videoProcessor is not null
            && _videoEnumerator is not null
            && _vpInputWidth == inputWidth
            && _vpInputHeight == inputHeight
            && _vpOutputWidth == outputWidth
            && _vpOutputHeight == outputHeight)
        {
            return true;
        }

        ReleaseVideoProcessor();

        var desc = new D3D11VideoProcessorContentDesc
        {
            InputFrameFormat = D3D11VideoFrameFormatProgressive,
            InputFrameRateNum = 60,
            InputFrameRateDen = 1,
            InputWidth = (uint)Math.Max(inputWidth, 1),
            InputHeight = (uint)Math.Max(inputHeight, 1),
            OutputFrameRateNum = 60,
            OutputFrameRateDen = 1,
            OutputWidth = (uint)Math.Max(outputWidth, 1),
            OutputHeight = (uint)Math.Max(outputHeight, 1),
            Usage = D3D11VideoUsagePlaybackNormal
        };

        void* enumerator = null;
        var hr = VideoDeviceCreateVideoProcessorEnumerator(_videoDevice, &desc, &enumerator);
        if (hr < 0 || enumerator is null)
        {
            _logger.LogWarning("CreateVideoProcessorEnumerator 失敗 (0x{Hr:X8})", hr);
            return false;
        }

        void* processor = null;
        hr = VideoDeviceCreateVideoProcessor(_videoDevice, enumerator, 0, &processor);
        if (hr < 0 || processor is null)
        {
            ReleaseCom(enumerator);
            _logger.LogWarning("CreateVideoProcessor 失敗 (0x{Hr:X8})", hr);
            return false;
        }

        _videoEnumerator = enumerator;
        _videoProcessor = processor;
        _vpInputWidth = inputWidth;
        _vpInputHeight = inputHeight;
        _vpOutputWidth = outputWidth;
        _vpOutputHeight = outputHeight;
        return true;
    }

    private bool EnsureLastGpuFrameTexture(int width, int height)
    {
        if (_device is null || width <= 0 || height <= 0)
        {
            return false;
        }

        if (_lastGpuFrameTexture is not null && _lastGpuFrameWidth == width && _lastGpuFrameHeight == height)
        {
            return true;
        }

        ReleaseLastGpuFrameTexture();

        var desc = new D3D11Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormatB8G8R8A8Unorm,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = D3D11UsageDefault,
            BindFlags = 0,
            CpuAccessFlags = 0,
            MiscFlags = 0
        };

        void* texture = null;
        if (DeviceCreateTexture2D(_device, &desc, null, &texture) < 0 || texture is null)
        {
            return false;
        }

        _lastGpuFrameTexture = texture;
        _lastGpuFrameWidth = width;
        _lastGpuFrameHeight = height;
        return true;
    }

    private void CreateSwapChain(int width, int height)
    {
        void* dxgiDevice = null;
        void* dxgiAdapter = null;
        void* dxgiFactory = null;
        try
        {
            var dxgiDevGuid = IidIdxgiDevice;
            ThrowIfFailed(QueryInterface(_device, &dxgiDevGuid, &dxgiDevice), "QI(IDXGIDevice)");
            ThrowIfFailed(DxgiDeviceGetAdapter(dxgiDevice, &dxgiAdapter), "GetAdapter");

            var factoryGuid = IidIdxgiFactory2;
            ThrowIfFailed(DxgiObjectGetParent(dxgiAdapter, &factoryGuid, &dxgiFactory), "GetParent(IDXGIFactory2)");

            var desc = new DxgiSwapChainDesc1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = DxgiFormatB8G8R8A8Unorm,
                Stereo = 0,
                SampleCount = 1,
                SampleQuality = 0,
                BufferUsage = DxgiUsageRenderTargetOutput,
                BufferCount = 2,
                Scaling = DxgiScalingStretch,
                SwapEffect = DxgiSwapEffectFlipSequential,
                AlphaMode = DxgiAlphaModeIgnore,
                Flags = DxgiSwapChainFlagFrameLatencyWaitableObject
            };

            void* swapChain = null;
            var hr = DxgiFactoryCreateSwapChainForComposition(dxgiFactory, _device, &desc, null, &swapChain);
            if (hr < 0)
            {
                desc.Flags = 0;
                hr = DxgiFactoryCreateSwapChainForComposition(dxgiFactory, _device, &desc, null, &swapChain);
                ThrowIfFailed(hr, "CreateSwapChainForComposition");
            }

            _swapChainFlags = desc.Flags;
            _swapChain = swapChain;

            var swapChain2Guid = IidIdxgiSwapChain2;
            void* swapChain2 = null;
            if (QueryInterface(_swapChain, &swapChain2Guid, &swapChain2) >= 0)
            {
                _swapChain2 = swapChain2;
                if ((_swapChainFlags & DxgiSwapChainFlagFrameLatencyWaitableObject) != 0)
                {
                    _ = SwapChain2SetMaximumFrameLatency(_swapChain2, 1);
                }
            }
        }
        finally
        {
            ReleaseCom(dxgiFactory);
            ReleaseCom(dxgiAdapter);
            ReleaseCom(dxgiDevice);
        }
    }

    private void ResizeSwapChain(int newWidth, int newHeight)
    {
        ReleaseCachedOutputView();
        ReleaseCachedInputViews();
        ReleaseLastGpuFrameTexture();
        var hr = SwapChainResizeBuffers(_swapChain, 2, (uint)newWidth, (uint)newHeight, DxgiFormatB8G8R8A8Unorm, _swapChainFlags);
        if (hr >= 0)
        {
            _width = newWidth;
            _height = newHeight;
            ApplyMatrixTransform();
        }
        else
        {
            HandlePotentialDeviceLost(hr, "SwapChainResizeBuffers");
        }
    }

    private void ApplyMatrixTransform()
    {
        if (_swapChain2 is null || _width <= 0 || _height <= 0 || _displayWidth <= 0 || _displayHeight <= 0)
        {
            return;
        }

        var (scaleX, scaleY) = ComputeScaleFactor(_width, _height, _displayWidth, _displayHeight);
        var matrix = new DxgiMatrix3x2F
        {
            M11 = scaleX,
            M22 = scaleY
        };

        var hr = SwapChain2SetMatrixTransform(_swapChain2, &matrix);
        if (hr < 0)
        {
            _logger.LogWarning("Direct3D 11 SetMatrixTransform 失敗 (0x{Hr:X8})", hr);
        }
    }

    private void ReleaseVideoProcessor()
    {
        ReleaseCachedOutputView();
        ReleaseCachedInputViews();

        ReleaseCom(_videoProcessor);
        _videoProcessor = null;

        ReleaseCom(_videoEnumerator);
        _videoEnumerator = null;

        _vpInputWidth = 0;
        _vpInputHeight = 0;
        _vpOutputWidth = 0;
        _vpOutputHeight = 0;
        _vpLastCropX = -1;
        _vpLastCropY = -1;
        _vpLastDisplayWidth = -1;
        _vpLastDisplayHeight = -1;
    }

    private void ReleaseLastGpuFrameTexture()
    {
        ReleaseCom(_lastGpuFrameTexture);
        _lastGpuFrameTexture = null;
        _lastGpuFrameWidth = 0;
        _lastGpuFrameHeight = 0;
        _hasRenderedGpuFrame = false;
        _gpuBackupDirty = false;
    }

    private void CleanupSwapChain()
    {
        _lastFrameData = null;
        _lastFrameStride = 0;

        ReleaseLastGpuFrameTexture();
        ReleaseVideoProcessor();

        ReleaseCom(_panelNative);
        _panelNative = null;

        ReleaseCom(_swapChain2);
        _swapChain2 = null;

        ReleaseCom(_swapChain);
        _swapChain = null;
    }

    private void CleanupDevice()
    {
        ReleaseCom(_dxgiDeviceManager);
        _dxgiDeviceManager = null;

        ReleaseCom(_videoContext);
        _videoContext = null;

        ReleaseCom(_videoDevice);
        _videoDevice = null;

        ReleaseCom(_context);
        _context = null;

        ReleaseCom(_device);
        _device = null;
    }

    public void Dispose()
    {
        lock (_renderLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_panelNative is not null)
            {
                SetPanelSwapChain(_panelNative, null);
            }

            CleanupSwapChain();
            CleanupDevice();
        }
    }

    private static void EnableMultithreadProtection(void* device)
    {
        var guid = IidId3d10Multithread;
        void* multithread = null;
        if (QueryInterface(device, &guid, &multithread) >= 0 && multithread is not null)
        {
            try
            {
                var vtbl = *(void***)multithread;
                ((delegate* unmanaged[Stdcall]<void*, int, int>)vtbl[5])(multithread, 1);
            }
            finally
            {
                ReleaseCom(multithread);
            }
        }
    }

    private static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
        {
            throw new COMException($"Direct3D 11 {operation} 失敗 (0x{hr:X8})", hr);
        }
    }

    private static int QueryInterface(void* obj, Guid* iid, void** ppv)
    {
        var vtbl = *(void***)obj;
        return ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)vtbl[0])(obj, iid, ppv);
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

    private static int SetPanelSwapChain(void* panelNative, void* swapChain)
    {
        var vtbl = *(void***)panelNative;
        return ((delegate* unmanaged[Stdcall]<void*, void*, int>)vtbl[3])(panelNative, swapChain);
    }

    private static int DxgiDeviceGetAdapter(void* dxgiDevice, void** adapter)
    {
        var vtbl = *(void***)dxgiDevice;
        return ((delegate* unmanaged[Stdcall]<void*, void**, int>)vtbl[7])(dxgiDevice, adapter);
    }

    private static int DxgiObjectGetParent(void* dxgiObj, Guid* iid, void** parent)
    {
        var vtbl = *(void***)dxgiObj;
        return ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)vtbl[6])(dxgiObj, iid, parent);
    }

    private static int DxgiFactoryCreateSwapChainForComposition(
        void* factory,
        void* device,
        DxgiSwapChainDesc1* desc,
        void* restrictOutput,
        void** swapChain)
    {
        var vtbl = *(void***)factory;
        return ((delegate* unmanaged[Stdcall]<void*, void*, DxgiSwapChainDesc1*, void*, void**, int>)vtbl[24])(
            factory, device, desc, restrictOutput, swapChain);
    }

    private static int SwapChainPresent(void* swapChain, uint syncInterval, uint flags)
    {
        var vtbl = *(void***)swapChain;
        return ((delegate* unmanaged[Stdcall]<void*, uint, uint, int>)vtbl[8])(swapChain, syncInterval, flags);
    }

    private static int SwapChainGetBuffer(void* swapChain, uint index, Guid* iid, void** surface)
    {
        var vtbl = *(void***)swapChain;
        return ((delegate* unmanaged[Stdcall]<void*, uint, Guid*, void**, int>)vtbl[9])(swapChain, index, iid, surface);
    }

    private static int SwapChainResizeBuffers(void* swapChain, uint bufferCount, uint width, uint height, uint format, uint flags)
    {
        var vtbl = *(void***)swapChain;
        return ((delegate* unmanaged[Stdcall]<void*, uint, uint, uint, uint, uint, int>)vtbl[13])(
            swapChain, bufferCount, width, height, format, flags);
    }

    private static int SwapChain2SetMaximumFrameLatency(void* swapChain2, uint maxLatency)
    {
        var vtbl = *(void***)swapChain2;
        return ((delegate* unmanaged[Stdcall]<void*, uint, int>)vtbl[31])(swapChain2, maxLatency);
    }

    private static int SwapChain2SetMatrixTransform(void* swapChain2, DxgiMatrix3x2F* matrix)
    {
        var vtbl = *(void***)swapChain2;
        return ((delegate* unmanaged[Stdcall]<void*, DxgiMatrix3x2F*, int>)vtbl[34])(swapChain2, matrix);
    }

    private static int DeviceCreateTexture2D(void* device, D3D11Texture2DDesc* desc, void* initialData, void** texture)
    {
        var vtbl = *(void***)device;
        return ((delegate* unmanaged[Stdcall]<void*, D3D11Texture2DDesc*, void*, void**, int>)vtbl[5])(
            device, desc, initialData, texture);
    }

    private static int ContextMap(
        void* context,
        void* resource,
        uint subresource,
        uint mapType,
        uint mapFlags,
        D3D11MappedSubresource* mappedResource)
    {
        var vtbl = *(void***)context;
        return ((delegate* unmanaged[Stdcall]<void*, void*, uint, uint, uint, D3D11MappedSubresource*, int>)vtbl[14])(
            context, resource, subresource, mapType, mapFlags, mappedResource);
    }

    private static void ContextUnmap(void* context, void* resource, uint subresource)
    {
        var vtbl = *(void***)context;
        ((delegate* unmanaged[Stdcall]<void*, void*, uint, void>)vtbl[15])(context, resource, subresource);
    }

    private static void ContextCopyResource(void* context, void* dstResource, void* srcResource)
    {
        var vtbl = *(void***)context;
        ((delegate* unmanaged[Stdcall]<void*, void*, void*, void>)vtbl[47])(context, dstResource, srcResource);
    }

    private static void ContextUpdateSubresource(
        void* context,
        void* resource,
        uint subresource,
        void* dstBox,
        void* srcData,
        uint srcRowPitch,
        uint srcDepthPitch)
    {
        var vtbl = *(void***)context;
        ((delegate* unmanaged[Stdcall]<void*, void*, uint, void*, void*, uint, uint, void>)vtbl[48])(
            context, resource, subresource, dstBox, srcData, srcRowPitch, srcDepthPitch);
    }

    private static int DxgiDeviceManagerResetDevice(void* manager, void* device, uint resetToken)
    {
        var vtbl = *(void***)manager;
        return ((delegate* unmanaged[Stdcall]<void*, void*, uint, int>)vtbl[7])(manager, device, resetToken);
    }

    private static int VideoDeviceCreateVideoProcessor(void* videoDevice, void* enumerator, uint rateConversionIndex, void** processor)
    {
        var vtbl = *(void***)videoDevice;
        return ((delegate* unmanaged[Stdcall]<void*, void*, uint, void**, int>)vtbl[4])(
            videoDevice, enumerator, rateConversionIndex, processor);
    }

    private static int VideoDeviceCreateVideoProcessorInputView(
        void* videoDevice,
        void* resource,
        void* enumerator,
        D3D11VideoProcessorInputViewDesc* desc,
        void** view)
    {
        var vtbl = *(void***)videoDevice;
        return ((delegate* unmanaged[Stdcall]<void*, void*, void*, D3D11VideoProcessorInputViewDesc*, void**, int>)vtbl[8])(
            videoDevice, resource, enumerator, desc, view);
    }

    private static int VideoDeviceCreateVideoProcessorOutputView(
        void* videoDevice,
        void* resource,
        void* enumerator,
        D3D11VideoProcessorOutputViewDesc* desc,
        void** view)
    {
        var vtbl = *(void***)videoDevice;
        return ((delegate* unmanaged[Stdcall]<void*, void*, void*, D3D11VideoProcessorOutputViewDesc*, void**, int>)vtbl[9])(
            videoDevice, resource, enumerator, desc, view);
    }

    private static int VideoDeviceCreateVideoProcessorEnumerator(
        void* videoDevice,
        D3D11VideoProcessorContentDesc* desc,
        void** enumerator)
    {
        var vtbl = *(void***)videoDevice;
        return ((delegate* unmanaged[Stdcall]<void*, D3D11VideoProcessorContentDesc*, void**, int>)vtbl[10])(
            videoDevice, desc, enumerator);
    }

    private static void VideoContextSetOutputTargetRect(void* videoContext, void* processor, int enable, NativeRect* rect)
    {
        var vtbl = *(void***)videoContext;
        ((delegate* unmanaged[Stdcall]<void*, void*, int, NativeRect*, void>)vtbl[13])(videoContext, processor, enable, rect);
    }

    private static void VideoContextSetStreamSourceRect(void* videoContext, void* processor, uint streamIndex, int enable, NativeRect* rect)
    {
        var vtbl = *(void***)videoContext;
        ((delegate* unmanaged[Stdcall]<void*, void*, uint, int, NativeRect*, void>)vtbl[30])(
            videoContext, processor, streamIndex, enable, rect);
    }

    private static void VideoContextSetStreamDestRect(void* videoContext, void* processor, uint streamIndex, int enable, NativeRect* rect)
    {
        var vtbl = *(void***)videoContext;
        ((delegate* unmanaged[Stdcall]<void*, void*, uint, int, NativeRect*, void>)vtbl[31])(
            videoContext, processor, streamIndex, enable, rect);
    }

    private static int VideoContextBlt(
        void* videoContext,
        void* processor,
        void* view,
        uint outputFrame,
        uint streamCount,
        D3D11VideoProcessorStream* streams)
    {
        var vtbl = *(void***)videoContext;
        return ((delegate* unmanaged[Stdcall]<void*, void*, void*, uint, uint, D3D11VideoProcessorStream*, int>)vtbl[53])(
            videoContext, processor, view, outputFrame, streamCount, streams);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11MappedSubresource
    {
        public void* Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11VideoProcessorContentDesc
    {
        public uint InputFrameFormat;
        public uint InputFrameRateNum;
        public uint InputFrameRateDen;
        public uint InputWidth;
        public uint InputHeight;
        public uint OutputFrameRateNum;
        public uint OutputFrameRateDen;
        public uint OutputWidth;
        public uint OutputHeight;
        public uint Usage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11VideoProcessorInputViewDesc
    {
        public uint FourCC;
        public uint ViewDimension;
        public uint MipSlice;
        public uint ArraySlice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11VideoProcessorOutputViewDesc
    {
        public uint ViewDimension;
        public uint MipSlice;
        public uint FirstArraySlice;
        public uint ArraySize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11VideoProcessorStream
    {
        public int Enable;
        public uint OutputIndex;
        public uint InputFrameOrField;
        public uint PastFrames;
        public uint FutureFrames;
        public void* PastSurfaces;
        public void* InputSurface;
        public void* FutureSurfaces;
        public void* PastSurfacesRight;
        public void* InputSurfaceRight;
        public void* FutureSurfacesRight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiMatrix3x2F
    {
        public float M11;
        public float M12;
        public float M21;
        public float M22;
        public float M31;
        public float M32;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiSwapChainDesc1
    {
        public uint Width;
        public uint Height;
        public uint Format;
        public int Stereo;
        public uint SampleCount;
        public uint SampleQuality;
        public uint BufferUsage;
        public uint BufferCount;
        public uint Scaling;
        public uint SwapEffect;
        public uint AlphaMode;
        public uint Flags;
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        void* adapter,
        uint driverType,
        nint software,
        uint flags,
        uint* featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        void** device,
        uint* featureLevel,
        void** immediateContext);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateDXGIDeviceManager(uint* resetToken, void** deviceManager);
}
