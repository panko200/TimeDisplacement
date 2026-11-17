using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.D3DCompiler;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;

namespace TimeDisplacement
{
    internal sealed class TimeDisplacementD3DRenderer : IDisposable
    {
        private const long MaxHistoryVramBytes = 1024L * 1024L * 1024L;
        private const int MaxHistorySlices = 512;
        private const int MaxLutWidth = 16384;

        private const string VertexShaderSource = @"
struct VSOut
{
    float4 Pos : SV_Position;
    float2 UV  : TEXCOORD0;
};

VSOut VS(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.UV = uv;
    o.Pos = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}
";

        private const string PixelShaderSource = @"
Texture2DArray<float4> HistoryTex : register(t0);
Texture2D<float4> MapTex : register(t1);
Texture2D<float4> LutTex : register(t2);

cbuffer Params : register(b0)
{
    float MaxTime;
    float TimeResolution;
    float FPS;
    float CurrentFrame;

    float MaxLookback;
    float LutScale;
    float MaxLutIndex;
    int Mode;

    int Interpolate;
    int ShowMap;
    float2 _Padding;
};

struct VSOut
{
    float4 Pos : SV_Position;
    float2 UV  : TEXCOORD0;
};

float4 SampleLutEntry(int2 px, float4 e)
{
    int sA = (int)round(e.y);
    float4 cA = HistoryTex.Load(int4(px, sA, 0));
    int sB = (int)round(e.z);
    if (sA == sB || e.w <= 0.0001)
    {
        return cA;
    }
    float4 cB = HistoryTex.Load(int4(px, sB, 0));
    return lerp(cA, cB, e.w);
}

float4 PS(VSOut input) : SV_Target
{
    int2 px = int2(input.Pos.xy);
    float4 mapCol = MapTex.Load(int3(px, 0));

    if (ShowMap != 0)
    {
        return mapCol;
    }

    float lum = saturate(dot(mapCol.rgb, float3(0.299, 0.587, 0.114)));

    float baseFrame = CurrentFrame;
    float rawOffsetSec = 0.0;

    if (Mode == 0)
    {
        float factor = (MaxTime >= 0.0) ? (1.0 - lum) : lum;
        rawOffsetSec = -abs(MaxTime) * factor;
    }
    else if (Mode == 1)
    {
        float factor = (MaxTime >= 0.0) ? lum : (1.0 - lum);
        rawOffsetSec = -abs(MaxTime) * factor;
    }
    else if (Mode == 2)
    {
        baseFrame = CurrentFrame - abs(MaxTime) * FPS;
        rawOffsetSec = (lum - 0.5) * 2.0 * MaxTime;
    }
    else
    {
        rawOffsetSec = (lum - 0.5) * 2.0 * MaxTime;
    }

    float quantizedSec = (TimeResolution > 0.0001)
        ? (round(rawOffsetSec * TimeResolution) / TimeResolution)
        : rawOffsetSec;

    float targetFrame = clamp(baseFrame + quantizedSec * FPS, 0.0, CurrentFrame);
    float lookback = clamp(CurrentFrame - targetFrame, 0.0, MaxLookback);
    float lutCoord = clamp(lookback / max(LutScale, 1.0), 0.0, MaxLutIndex);

    int maxLutIdx = (int)MaxLutIndex;
    if (Interpolate == 0)
    {
        int k = clamp((int)round(lutCoord), 0, maxLutIdx);
        int slice = (int)round(LutTex.Load(int3(k, 0, 0)).x);
        return HistoryTex.Load(int4(px, slice, 0));
    }

    int k0 = clamp((int)floor(lutCoord), 0, maxLutIdx);
    int k1 = min(k0 + 1, maxLutIdx);
    float subT = lutCoord - (float)k0;

    float4 c0 = SampleLutEntry(px, LutTex.Load(int3(k0, 0, 0)));
    if (subT <= 0.0001 || k0 == k1)
    {
        return c0;
    }

    float4 c1 = SampleLutEntry(px, LutTex.Load(int3(k1, 0, 0)));
    return lerp(c0, c1, subT);
}
";

        [StructLayout(LayoutKind.Sequential)]
        private struct ShaderParams
        {
            public float MaxTime;
            public float TimeResolution;
            public float FPS;
            public float CurrentFrame;

            public float MaxLookback;
            public float LutScale;
            public float MaxLutIndex;
            public int Mode;

            public int Interpolate;
            public int ShowMap;
            public float Padding0;
            public float Padding1;
        }

        private struct CachedFrameEntry
        {
            public int Frame;
            public int SliceIndex;
        }

        private readonly IGraphicsDevicesAndContext _devices;
        private readonly ID2D1Multithread? _d2dMultithread;

        private ID3D11VertexShader? _vertexShader;
        private ID3D11PixelShader? _pixelShader;
        private ID3D11Buffer? _constantBuffer;
        private ID3D11RasterizerState? _rasterizerState;

        private int _texWidth;
        private int _texHeight;
        private ID3D11Texture2D? _currentFrameTex;
        private ID2D1Bitmap1? _currentFrameBitmap;
        private ID3D11Texture2D? _mapTex;
        private ID2D1Bitmap1? _mapBitmap;
        private ID3D11ShaderResourceView? _mapSrv;
        private ID3D11Texture2D? _outputTex;
        private ID3D11RenderTargetView? _outputRtv;
        private ID2D1Bitmap1? _outputBitmap;

        private ID3D11Texture2D? _historyTex;
        private ID3D11ShaderResourceView? _historySrv;
        private int _historyCapacity;
        private int _currentStride = 1;

        private ID3D11Texture2D? _lutTex;
        private ID3D11ShaderResourceView? _lutSrv;
        private int _lutCapacity;
        private Vector4[] _lutBuffer = Array.Empty<Vector4>();

        private readonly List<CachedFrameEntry> _cachedFrames = new();
        private readonly Queue<int> _freeSlices = new();
        private int _lastFrame = -1;
        private bool _disposed;

        public bool IsInitialized { get; private set; }
        public ID2D1Bitmap1? CurrentFrameBitmap => _currentFrameBitmap;
        public ID2D1Bitmap1? MapBitmap => _mapBitmap;
        public ID2D1Bitmap1? OutputBitmap => _outputBitmap;

        public TimeDisplacementD3DRenderer(IGraphicsDevicesAndContext devices)
        {
            _devices = devices;
            _d2dMultithread = devices.D2D.Factory.QueryInterfaceOrNull<ID2D1Multithread>();
            InitializePipeline();
        }

        private void InitializePipeline()
        {
            try
            {
                var d3dDevice = _devices.D3D.Device;
                using var vsBlob = Compiler.Compile(VertexShaderSource, "VS", "TimeDisplacement.hlsl", "vs_4_0");
                using var psBlob = Compiler.Compile(PixelShaderSource, "PS", "TimeDisplacement.hlsl", "ps_4_0");

                _vertexShader = d3dDevice.CreateVertexShader(vsBlob);
                _pixelShader = d3dDevice.CreatePixelShader(psBlob);
                _constantBuffer = d3dDevice.CreateBuffer(new BufferDescription
                {
                    ByteWidth = Marshal.SizeOf<ShaderParams>(),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ConstantBuffer,
                    CPUAccessFlags = CpuAccessFlags.None,
                });
                _rasterizerState = d3dDevice.CreateRasterizerState(new RasterizerDescription
                {
                    FillMode = Vortice.Direct3D11.FillMode.Solid,
                    CullMode = CullMode.None,
                    DepthClipEnable = false,
                });
                IsInitialized = true;
            }
            catch
            {
                IsInitialized = false;
            }
        }

        public bool EnsureResources(int width, int height, double peakMaxTimeSec, int fps, TimeShiftMode mode)
        {
            if (!IsInitialized || _disposed)
                return false;

            width = Math.Max(1, width);
            height = Math.Max(1, height);
            fps = Math.Max(1, fps);

            if (width != _texWidth || height != _texHeight)
            {
                DisposeSizeDependentResources();
                if (!CreateSizeDependentResources(width, height))
                {
                    DisposeSizeDependentResources();
                    return false;
                }
                _texWidth = width;
                _texHeight = height;
            }

            double maxWindowSec = mode == TimeShiftMode.BidirectionalDelayed
                ? Math.Abs(peakMaxTimeSec) * 2.0
                : Math.Abs(peakMaxTimeSec);

            int maxLookbackFrames = Math.Max(0, (int)Math.Ceiling(maxWindowSec * fps));
            int neededFrames = maxLookbackFrames + 1;

            long bytesPerSlice = Math.Max(1L, (long)width * height * 4L);
            int budgetSlices = (int)Math.Clamp(MaxHistoryVramBytes / bytesPerSlice, 2L, MaxHistorySlices);

            int stride = neededFrames <= budgetSlices
                ? 1
                : (int)Math.Ceiling((double)neededFrames / Math.Max(1, budgetSlices - 1));

            int desiredCapacity = Math.Clamp((int)Math.Ceiling((double)neededFrames / stride) + 1, 2, budgetSlices);

            if (_historyTex == null || desiredCapacity > _historyCapacity)
            {
                if (!EnsureHistoryCapacity(width, height, desiredCapacity, neededFrames, ref stride))
                    return false;
            }

            _currentStride = Math.Max(1, stride);
            return true;
        }

        private bool CreateSizeDependentResources(int width, int height)
        {
            try
            {
                var d3dDevice = _devices.D3D.Device;
                var dc = _devices.DeviceContext;

                var texDesc = new Texture2DDescription
                {
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                };

                var targetBmpProps = new BitmapProperties1(
                    new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                    96f, 96f, BitmapOptions.Target);
                var readBmpProps = new BitmapProperties1(
                    new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                    96f, 96f, BitmapOptions.None);

                _currentFrameTex = d3dDevice.CreateTexture2D(texDesc);
                using (var surf = _currentFrameTex.QueryInterface<IDXGISurface>())
                    _currentFrameBitmap = dc.CreateBitmapFromDxgiSurface(surf, targetBmpProps);

                _mapTex = d3dDevice.CreateTexture2D(texDesc);
                _mapSrv = d3dDevice.CreateShaderResourceView(_mapTex);
                using (var surf = _mapTex.QueryInterface<IDXGISurface>())
                    _mapBitmap = dc.CreateBitmapFromDxgiSurface(surf, targetBmpProps);

                _outputTex = d3dDevice.CreateTexture2D(texDesc);
                _outputRtv = d3dDevice.CreateRenderTargetView(_outputTex);
                using (var surf = _outputTex.QueryInterface<IDXGISurface>())
                    _outputBitmap = dc.CreateBitmapFromDxgiSurface(surf, readBmpProps);

                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool EnsureHistoryCapacity(int width, int height, int desiredCapacity, int neededFrames, ref int stride)
        {
            var d3dDevice = _devices.D3D.Device;
            var d3dContext = _devices.D3D.DeviceContext;

            int attemptCapacity = Math.Max(2, desiredCapacity);
            while (attemptCapacity >= 2)
            {
                try
                {
                    var arrayDesc = new Texture2DDescription
                    {
                        Width = width,
                        Height = height,
                        MipLevels = 1,
                        ArraySize = attemptCapacity,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ShaderResource,
                        CPUAccessFlags = CpuAccessFlags.None,
                    };

                    var newHistoryTex = d3dDevice.CreateTexture2D(arrayDesc);
                    var srvDesc = new ShaderResourceViewDescription(
                        newHistoryTex,
                        ShaderResourceViewDimension.Texture2DArray,
                        Format.B8G8R8A8_UNorm,
                        0, 1, 0, attemptCapacity);
                    var newHistorySrv = d3dDevice.CreateShaderResourceView(newHistoryTex, srvDesc);

                    if (_historyTex != null && _historyCapacity > 0)
                    {
                        int slicesToCopy = Math.Min(_historyCapacity, attemptCapacity);
                        ExecuteInD3DLock(() =>
                        {
                            for (int i = 0; i < slicesToCopy; i++)
                            {
                                d3dContext.CopySubresourceRegion(newHistoryTex, i, 0, 0, 0, _historyTex, i);
                            }
                        });
                    }

                    _historySrv?.Dispose();
                    _historyTex?.Dispose();
                    _historyTex = newHistoryTex;
                    _historySrv = newHistorySrv;

                    for (int i = _historyCapacity; i < attemptCapacity; i++)
                    {
                        _freeSlices.Enqueue(i);
                    }
                    _historyCapacity = attemptCapacity;

                    if (attemptCapacity < desiredCapacity)
                    {
                        stride = Math.Max(stride, (int)Math.Ceiling((double)neededFrames / Math.Max(1, attemptCapacity - 1)));
                    }
                    return true;
                }
                catch
                {
                    if (attemptCapacity <= 2)
                        return false;
                    attemptCapacity = Math.Max(2, attemptCapacity / 2);
                }
            }
            return false;
        }

        private bool EnsureLutResources(int lutWidth)
        {
            if (_lutTex != null && lutWidth <= _lutCapacity)
                return true;

            int newCap = Math.Min(MaxLutWidth, Math.Max(64, NextPowerOfTwo(lutWidth)));
            try
            {
                var d3dDevice = _devices.D3D.Device;
                var newLutTex = d3dDevice.CreateTexture2D(new Texture2DDescription
                {
                    Width = newCap,
                    Height = 1,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R32G32B32A32_Float,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                });
                var newLutSrv = d3dDevice.CreateShaderResourceView(newLutTex);

                _lutSrv?.Dispose();
                _lutTex?.Dispose();
                _lutTex = newLutTex;
                _lutSrv = newLutSrv;
                _lutCapacity = newCap;

                if (_lutBuffer.Length < newCap)
                    _lutBuffer = new Vector4[newCap];

                return true;
            }
            catch
            {
                return false;
            }
        }

        public unsafe void RenderFrame(
            int frame,
            int fps,
            float maxTimeSec,
            float timeResolutionFps,
            TimeShiftMode mode,
            bool enableFrameBlending,
            bool showMap)
        {
            if (!IsInitialized || _disposed || _historyTex == null || _historySrv == null ||
                _currentFrameTex == null || _mapSrv == null || _outputRtv == null ||
                _vertexShader == null || _pixelShader == null || _constantBuffer == null || _rasterizerState == null)
            {
                return;
            }

            if (frame < _lastFrame)
            {
                for (int i = _cachedFrames.Count - 1; i >= 0; i--)
                {
                    if (_cachedFrames[i].Frame >= frame)
                    {
                        _freeSlices.Enqueue(_cachedFrames[i].SliceIndex);
                        _cachedFrames.RemoveAt(i);
                    }
                }
            }

            int targetSlice;
            if (_cachedFrames.Count > 0 && _cachedFrames[^1].Frame == frame)
            {
                targetSlice = _cachedFrames[^1].SliceIndex;
            }
            else if (_cachedFrames.Count >= 2 && (_cachedFrames[^1].Frame % _currentStride != 0))
            {
                var last = _cachedFrames[^1];
                targetSlice = last.SliceIndex;
                _cachedFrames[^1] = new CachedFrameEntry { Frame = frame, SliceIndex = targetSlice };
            }
            else
            {
                if (_freeSlices.Count > 0)
                {
                    targetSlice = _freeSlices.Dequeue();
                }
                else if (_cachedFrames.Count > 0)
                {
                    targetSlice = _cachedFrames[0].SliceIndex;
                    _cachedFrames.RemoveAt(0);
                }
                else
                {
                    targetSlice = 0;
                }

                _cachedFrames.Add(new CachedFrameEntry { Frame = frame, SliceIndex = targetSlice });
            }

            _lastFrame = frame;

            double currentMaxWindowSec = mode == TimeShiftMode.BidirectionalDelayed
                ? Math.Abs((double)maxTimeSec) * 2.0
                : Math.Abs((double)maxTimeSec);
            int maxLookbackFrames = Math.Max(0, (int)Math.Ceiling(currentMaxWindowSec * fps));

            BuildLut(frame, maxLookbackFrames, out int lutWidth, out float lutScale);
            if (!EnsureLutResources(lutWidth))
                return;

            var shaderParams = new ShaderParams
            {
                MaxTime = maxTimeSec,
                TimeResolution = Math.Max(0.1f, timeResolutionFps),
                FPS = Math.Max(1f, fps),
                CurrentFrame = frame,
                MaxLookback = maxLookbackFrames,
                LutScale = lutScale,
                MaxLutIndex = Math.Max(0, lutWidth - 1),
                Mode = (int)mode,
                Interpolate = enableFrameBlending ? 1 : 0,
                ShowMap = showMap ? 1 : 0,
            };

            var d3dContext = _devices.D3D.DeviceContext;
            _d2dMultithread?.Enter();
            _devices.D3D.Multithread.Enter();
            try
            {
                d3dContext.CopySubresourceRegion(_historyTex, targetSlice, 0, 0, 0, _currentFrameTex, 0);

                fixed (Vector4* pLut = _lutBuffer)
                {
                    var box = new Box(0, 0, 0, lutWidth, 1, 1);
                    d3dContext.UpdateSubresource(_lutTex!, 0, box, (nint)pLut, lutWidth * 16, 0);
                }

                d3dContext.UpdateSubresource(_constantBuffer, 0, null, (nint)(&shaderParams), 0, 0);

                d3dContext.ClearState();
                d3dContext.IASetInputLayout(null);
                d3dContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                d3dContext.VSSetShader(_vertexShader);
                d3dContext.PSSetShader(_pixelShader);
                d3dContext.PSSetConstantBuffer(0, _constantBuffer);
                d3dContext.PSSetShaderResources(0, new[] { _historySrv, _mapSrv, _lutSrv! });
                d3dContext.RSSetState(_rasterizerState);
                d3dContext.RSSetViewport(0f, 0f, _texWidth, _texHeight);
                d3dContext.OMSetBlendState(null);
                d3dContext.OMSetDepthStencilState(null);
                d3dContext.OMSetRenderTargets(_outputRtv);

                d3dContext.Draw(3, 0);
            }
            finally
            {
                d3dContext.ClearState();
                _devices.D3D.Multithread.Leave();
                _d2dMultithread?.Leave();
            }
        }

        private void BuildLut(int currentFrame, int maxLookbackFrames, out int lutWidth, out float lutScale)
        {
            lutScale = maxLookbackFrames <= MaxLutWidth - 1 ? 1f : (float)maxLookbackFrames / (MaxLutWidth - 1);
            lutWidth = Math.Clamp((int)Math.Ceiling(maxLookbackFrames / (double)lutScale) + 1, 1, MaxLutWidth);

            if (_lutBuffer.Length < lutWidth)
                _lutBuffer = new Vector4[Math.Min(MaxLutWidth, Math.Max(64, NextPowerOfTwo(lutWidth)))];

            int count = _cachedFrames.Count;
            if (count == 0)
            {
                for (int i = 0; i < lutWidth; i++)
                    _lutBuffer[i] = Vector4.Zero;
                return;
            }

            int hi = count - 1;
            for (int i = 0; i < lutWidth; i++)
            {
                double targetFrame = currentFrame - i * (double)lutScale;
                while (hi > 0 && _cachedFrames[hi - 1].Frame >= targetFrame)
                {
                    hi--;
                }

                if (hi == 0 && targetFrame <= _cachedFrames[0].Frame)
                {
                    float s = _cachedFrames[0].SliceIndex;
                    _lutBuffer[i] = new Vector4(s, s, s, 0f);
                    continue;
                }

                if (hi == count - 1 && targetFrame >= _cachedFrames[hi].Frame)
                {
                    float s = _cachedFrames[hi].SliceIndex;
                    _lutBuffer[i] = new Vector4(s, s, s, 0f);
                    continue;
                }

                var entryA = _cachedFrames[Math.Max(0, hi - 1)];
                var entryB = _cachedFrames[hi];
                float frac = entryB.Frame > entryA.Frame
                    ? Math.Clamp((float)((targetFrame - entryA.Frame) / (double)(entryB.Frame - entryA.Frame)), 0f, 1f)
                    : 0f;
                float nearest = frac < 0.5f ? entryA.SliceIndex : entryB.SliceIndex;
                _lutBuffer[i] = new Vector4(nearest, entryA.SliceIndex, entryB.SliceIndex, frac);
            }
        }

        private void ExecuteInD3DLock(Action action)
        {
            _d2dMultithread?.Enter();
            _devices.D3D.Multithread.Enter();
            try
            {
                action();
            }
            finally
            {
                _devices.D3D.DeviceContext.ClearState();
                _devices.D3D.Multithread.Leave();
                _d2dMultithread?.Leave();
            }
        }

        private static int NextPowerOfTwo(int value)
        {
            int p = 1;
            while (p < value && p < MaxLutWidth)
                p <<= 1;
            return p;
        }

        private void DisposeSizeDependentResources()
        {
            _cachedFrames.Clear();
            _freeSlices.Clear();
            _lastFrame = -1;

            _historySrv?.Dispose();
            _historySrv = null;
            _historyTex?.Dispose();
            _historyTex = null;
            _historyCapacity = 0;

            _currentFrameBitmap?.Dispose();
            _currentFrameBitmap = null;
            _currentFrameTex?.Dispose();
            _currentFrameTex = null;

            _mapBitmap?.Dispose();
            _mapBitmap = null;
            _mapSrv?.Dispose();
            _mapSrv = null;
            _mapTex?.Dispose();
            _mapTex = null;

            _outputBitmap?.Dispose();
            _outputBitmap = null;
            _outputRtv?.Dispose();
            _outputRtv = null;
            _outputTex?.Dispose();
            _outputTex = null;

            _texWidth = 0;
            _texHeight = 0;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            DisposeSizeDependentResources();

            _lutSrv?.Dispose();
            _lutSrv = null;
            _lutTex?.Dispose();
            _lutTex = null;
            _lutCapacity = 0;

            _rasterizerState?.Dispose();
            _rasterizerState = null;
            _constantBuffer?.Dispose();
            _constantBuffer = null;
            _pixelShader?.Dispose();
            _pixelShader = null;
            _vertexShader?.Dispose();
            _vertexShader = null;

            _d2dMultithread?.Dispose();
        }
    }
}
