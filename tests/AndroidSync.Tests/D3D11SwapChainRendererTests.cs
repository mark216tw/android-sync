using System.Runtime.InteropServices;
using Com.AndroidSync.App.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Com.AndroidSync.App.Tests;

public sealed unsafe class D3D11SwapChainRendererTests
{
    private const uint D3DDriverTypeWarp = 5;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const uint D3D11SdkVersion = 7;

    private const uint DxgiFormatB8G8R8A8Unorm = 87;
    private const uint DxgiUsageRenderTargetOutput = 0x20;
    private const uint DxgiSwapEffectFlipSequential = 3;
    private const uint DxgiScalingStretch = 0;
    private const uint DxgiAlphaModeIgnore = 3;

    private static readonly Guid IidIdxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid IidIdxgiFactory2 = new("50C83A1C-E072-4C48-87B0-3630FA36A6D0");
    private static readonly Guid IidIdxgiSwapChain2 = new("A8BE2AC4-199F-4946-B331-79599FB98DE7");

    [Fact]
    public void ComputeScaleFactor_ScalesDownHighResBufferToPanelDipSize()
    {
        var (scaleX, scaleY) = D3D11SwapChainRenderer.ComputeScaleFactor(1080, 2400, 360, 800);

        Assert.Equal(1.0f / 3.0f, scaleX, precision: 5);
        Assert.Equal(1.0f / 3.0f, scaleY, precision: 5);
    }

    [Fact]
    public void ComputeScaleFactor_ReturnsIdentityWhenDimensionsInvalid()
    {
        Assert.Equal((1.0f, 1.0f), D3D11SwapChainRenderer.ComputeScaleFactor(0, 1080, 360, 800));
        Assert.Equal((1.0f, 1.0f), D3D11SwapChainRenderer.ComputeScaleFactor(1080, 1920, 0, 800));
        Assert.Equal((1.0f, 1.0f), D3D11SwapChainRenderer.ComputeScaleFactor(1080, 1920, 360, -10));
    }

    [Fact]
    public void UpdateDisplaySize_BeforeAttach_DoesNotThrow()
    {
        using var renderer = new D3D11SwapChainRenderer(NullLogger<D3D11SwapChainRenderer>.Instance);
        renderer.UpdateDisplaySize(360, 800);
        renderer.UpdateDisplaySize(0, 0);
    }

    [Fact]
    public void GetDxgiDeviceManager_IsIdempotentAndInitializesVideoSupport()
    {
        using var renderer = new D3D11SwapChainRenderer(NullLogger<D3D11SwapChainRenderer>.Instance);
        var first = renderer.GetDxgiDeviceManager();
        var second = renderer.GetDxgiDeviceManager();

        Assert.Equal(first, second);
        Assert.Equal(first != 0, renderer.IsGpuZeroCopySupported);
    }

    [Fact]
    public void DxgiSwapChain2_SetAndGetMatrixTransform_VtableSlot34And35_RoundTrip()
    {
        void* device = null;
        void* context = null;
        void* dxgiDevice = null;
        void* dxgiAdapter = null;
        void* dxgiFactory = null;
        void* swapChain = null;
        void* swapChain2 = null;

        try
        {
            uint featureLevel = 0;
            var hr = D3D11CreateDevice(
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
            Assert.True(hr >= 0, $"D3D11CreateDevice 失敗: 0x{hr:X8}");

            var dxgiDevGuid = IidIdxgiDevice;
            hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)(*(void***)device)[0])(device, &dxgiDevGuid, &dxgiDevice);
            Assert.True(hr >= 0, $"QI(IDXGIDevice) 失敗: 0x{hr:X8}");

            hr = ((delegate* unmanaged[Stdcall]<void*, void**, int>)(*(void***)dxgiDevice)[7])(dxgiDevice, &dxgiAdapter);
            Assert.True(hr >= 0, $"GetAdapter 失敗: 0x{hr:X8}");

            var factoryGuid = IidIdxgiFactory2;
            hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)(*(void***)dxgiAdapter)[6])(dxgiAdapter, &factoryGuid, &dxgiFactory);
            Assert.True(hr >= 0, $"GetParent(IDXGIFactory2) 失敗: 0x{hr:X8}");

            var desc = new DxgiSwapChainDesc1
            {
                Width = 1080,
                Height = 2400,
                Format = DxgiFormatB8G8R8A8Unorm,
                Stereo = 0,
                SampleCount = 1,
                SampleQuality = 0,
                BufferUsage = DxgiUsageRenderTargetOutput,
                BufferCount = 2,
                Scaling = DxgiScalingStretch,
                SwapEffect = DxgiSwapEffectFlipSequential,
                AlphaMode = DxgiAlphaModeIgnore,
                Flags = 0
            };

            hr = ((delegate* unmanaged[Stdcall]<void*, void*, DxgiSwapChainDesc1*, void*, void**, int>)(*(void***)dxgiFactory)[24])(
                dxgiFactory, device, &desc, null, &swapChain);
            Assert.True(hr >= 0, $"CreateSwapChainForComposition 失敗: 0x{hr:X8}");

            var swapChain2Guid = IidIdxgiSwapChain2;
            hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)(*(void***)swapChain)[0])(swapChain, &swapChain2Guid, &swapChain2);
            Assert.True(hr >= 0, $"QI(IDXGISwapChain2) 失敗: 0x{hr:X8}");

            var (scaleX, scaleY) = D3D11SwapChainRenderer.ComputeScaleFactor(1080, 2400, 360, 800);
            var expected = new DxgiMatrix3x2F { M11 = scaleX, M22 = scaleY };

            hr = ((delegate* unmanaged[Stdcall]<void*, DxgiMatrix3x2F*, int>)(*(void***)swapChain2)[34])(swapChain2, &expected);
            Assert.True(hr >= 0, $"SetMatrixTransform 失敗: 0x{hr:X8}");

            DxgiMatrix3x2F actual = default;
            hr = ((delegate* unmanaged[Stdcall]<void*, DxgiMatrix3x2F*, int>)(*(void***)swapChain2)[35])(swapChain2, &actual);
            Assert.True(hr >= 0, $"GetMatrixTransform 失敗: 0x{hr:X8}");

            Assert.Equal(expected.M11, actual.M11, precision: 5);
            Assert.Equal(expected.M22, actual.M22, precision: 5);
            Assert.Equal(0f, actual.M12);
            Assert.Equal(0f, actual.M21);
            Assert.Equal(0f, actual.M31);
            Assert.Equal(0f, actual.M32);
        }
        finally
        {
            ReleaseCom(swapChain2);
            ReleaseCom(swapChain);
            ReleaseCom(dxgiFactory);
            ReleaseCom(dxgiAdapter);
            ReleaseCom(dxgiDevice);
            ReleaseCom(context);
            ReleaseCom(device);
        }
    }

    private static void ReleaseCom(void* obj)
    {
        if (obj is null)
        {
            return;
        }

        ((delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)obj)[2])(obj);
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
}
