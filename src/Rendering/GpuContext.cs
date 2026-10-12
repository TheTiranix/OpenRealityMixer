using System.Numerics;
using System.Runtime.InteropServices;
using OpenRealityMixer.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace OpenRealityMixer.Rendering;

/// <summary>Constantes del shader; el orden y el tamanio deben coincidir con BoxCB en Shaders.Source.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BoxConstants
{
    public Matrix4x4 WorldToClip;
    public Vector4 BoxCenter;
    public Vector4 BoxRight;
    public Vector4 BoxUp;
    public Vector4 CamRect;
    public Vector4 KeyColor;
    public Vector4 Params1;
    public Vector4 Params2;
    public Vector4 Flags;
    public Vector4 Flags2;
    public Vector4 PassColor;
}

public sealed class RenderTarget : IDisposable
{
    public ID3D11Texture2D Texture { get; }
    public ID3D11RenderTargetView View { get; }
    public int Width { get; }
    public int Height { get; }

    public RenderTarget(ID3D11Device device, int width, int height)
    {
        Width = width;
        Height = height;
        Texture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        View = device.CreateRenderTargetView(Texture);
    }

    public void Dispose()
    {
        View.Dispose();
        Texture.Dispose();
    }
}

/// <summary>Ultima imagen de la camara subida a la GPU.</summary>
public sealed class CameraTexture : IDisposable
{
    private ID3D11Texture2D? _tex;
    private ID3D11ShaderResourceView? _srv;
    private int _w, _h;

    public ID3D11ShaderResourceView? View => _srv;
    public bool HasImage => _srv != null;

    public void Update(GpuContext gpu, byte[] pixels, int w, int h)
    {
        if (_tex == null || w != _w || h != _h)
        {
            _srv?.Dispose();
            _tex?.Dispose();
            _tex = gpu.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.Write,
            });
            _srv = gpu.Device.CreateShaderResourceView(_tex);
            _w = w;
            _h = h;
        }

        var mapped = gpu.Context.Map(_tex, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int rowBytes = w * 4;
            unsafe
            {
                fixed (byte* src = pixels)
                {
                    byte* dst = (byte*)mapped.DataPointer;
                    for (int y = 0; y < h; y++)
                        Buffer.MemoryCopy(src + (long)y * rowBytes, dst + (long)y * mapped.RowPitch, mapped.RowPitch, rowBytes);
                }
            }
        }
        finally
        {
            gpu.Context.Unmap(_tex, 0);
        }
    }

    public void Dispose()
    {
        _srv?.Dispose();
        _tex?.Dispose();
        _srv = null;
        _tex = null;
    }
}

/// <summary>Mascara de 8 bits (una caja con forma dibujada a mano): 255 = visible, 0 = transparente.</summary>
public sealed class MaskTexture : IDisposable
{
    private ID3D11Texture2D? _tex;
    private ID3D11ShaderResourceView? _srv;
    private int _w, _h;

    public ID3D11ShaderResourceView? View => _srv;
    public int Width => _w;
    public int Height => _h;

    public void Update(GpuContext gpu, byte[] r8, int w, int h)
    {
        if (_tex == null || w != _w || h != _h)
        {
            _srv?.Dispose();
            _tex?.Dispose();
            _tex = gpu.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.Write,
            });
            _srv = gpu.Device.CreateShaderResourceView(_tex);
            _w = w;
            _h = h;
        }

        var mapped = gpu.Context.Map(_tex, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            unsafe
            {
                fixed (byte* src = r8)
                {
                    byte* dst = (byte*)mapped.DataPointer;
                    for (int y = 0; y < h; y++)
                        Buffer.MemoryCopy(src + (long)y * w, dst + (long)y * mapped.RowPitch, mapped.RowPitch, w);
                }
            }
        }
        finally
        {
            gpu.Context.Unmap(_tex, 0);
        }
    }

    public void Dispose()
    {
        _srv?.Dispose();
        _tex?.Dispose();
        _srv = null;
        _tex = null;
    }
}

/// <summary>Dispositivo D3D11 + pipeline que compone una caja de realidad en una textura.</summary>
public sealed class GpuContext : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public string AdapterName { get; }

    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11Buffer _cb;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11RasterizerState _raster;
    private readonly ID3D11BlendState _blend;
    private readonly CameraTexture _dummy = new();
    private readonly MaskTexture _dummyMask = new();

    public GpuContext(int adapterIndex = -1)
    {
        IDXGIAdapter1? adapter = null;
        string name = "predeterminado";
        if (adapterIndex >= 0)
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            if (factory.EnumAdapters1((uint)adapterIndex, out adapter).Success && adapter != null)
                name = adapter.Description1.Description;
            else
                adapter = null;
        }

        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        var flags = DeviceCreationFlags.BgraSupport;
        var result = D3D11.D3D11CreateDevice(
            adapter, adapter != null ? DriverType.Unknown : DriverType.Hardware, flags, levels,
            out var device, out _, out var context);
        adapter?.Dispose();
        if (result.Failure || device == null || context == null)
            throw new InvalidOperationException($"No se pudo crear el dispositivo D3D11 ({result}).");

        Device = device;
        Context = context;
        AdapterName = name;

        var vsBlob = Compile("VS", "vs_5_0");
        var psBlob = Compile("PS", "ps_5_0");
        _vs = Device.CreateVertexShader(vsBlob);
        _ps = Device.CreatePixelShader(psBlob);

        _cb = Device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)Marshal.SizeOf<BoxConstants>(),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
        _sampler = Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxLOD = float.MaxValue,
        });
        _raster = Device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            DepthClipEnable = false,
        });
        _blend = Device.CreateBlendState(BlendDescription.Opaque);

        _dummy.Update(this, new byte[4], 1, 1);
        _dummyMask.Update(this, new byte[] { 255 }, 1, 1);
    }

    private static byte[] Compile(string entry, string profile)
    {
        var hr = Compiler.Compile(Shaders.Source, entry, "box.hlsl", profile, out var blob, out var err);
        if (hr.Failure || blob == null)
        {
            var msg = err != null ? Marshal.PtrToStringAnsi(err.BufferPointer) : hr.ToString();
            throw new InvalidOperationException($"Error compilando el shader {entry}: {msg}");
        }
        var bytes = blob.AsBytes();
        blob.Dispose();
        err?.Dispose();
        return bytes;
    }

    /// <summary>Compone la caja en <paramref name="target"/>. El resultado es alfa recta (sin premultiplicar).</summary>
    public unsafe void Draw(CameraTexture camera, RenderTarget target, in BoxConstants constants, MaskTexture? mask = null)
    {
        // Las cajas en modo passthrough por color no usan la camara; se enlaza una textura de 1 pixel.
        var view = camera.View ?? _dummy.View;
        if (view == null) return;

        var mapped = Context.Map(_cb, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        *(BoxConstants*)mapped.DataPointer = constants;
        Context.Unmap(_cb, 0);

        Context.OMSetRenderTargets(target.View);
        Context.RSSetViewport(0, 0, target.Width, target.Height);
        Context.RSSetState(_raster);
        Context.OMSetBlendState(_blend);
        Context.ClearRenderTargetView(target.View, new Color4(0, 0, 0, 0));
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Context.IASetInputLayout(null);
        Context.VSSetShader(_vs);
        Context.PSSetShader(_ps);
        Context.PSSetConstantBuffer(0, _cb);
        Context.PSSetShaderResource(0, view);
        Context.PSSetShaderResource(1, mask?.View ?? _dummyMask.View);
        Context.PSSetSampler(0, _sampler);
        Context.Draw(3, 0);
        Context.PSSetShaderResource(0, null);
        Context.PSSetShaderResource(1, null);
        Context.Flush();
    }

    /// <summary>Lee de vuelta una textura RGBA8 (para la autoprueba y las capturas).</summary>
    public byte[] ReadPixels(RenderTarget target)
    {
        using var staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)target.Width,
            Height = (uint)target.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        Context.CopyResource(staging, target.Texture);
        var mapped = Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var result = new byte[target.Width * target.Height * 4];
        unsafe
        {
            fixed (byte* dstBase = result)
            {
                byte* src = (byte*)mapped.DataPointer;
                for (int y = 0; y < target.Height; y++)
                    Buffer.MemoryCopy(src + (long)y * mapped.RowPitch, dstBase + (long)y * target.Width * 4, target.Width * 4, target.Width * 4);
            }
        }
        Context.Unmap(staging, 0);
        return result;
    }

    public void Dispose()
    {
        _dummy.Dispose();
        _dummyMask.Dispose();
        _blend.Dispose();
        _raster.Dispose();
        _sampler.Dispose();
        _cb.Dispose();
        _ps.Dispose();
        _vs.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
