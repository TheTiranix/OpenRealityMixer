using System.Numerics;

namespace OpenRealityMixer.Camera;

/// <summary>Un fotograma de camara. El buffer pertenece a la fuente y se reutiliza.</summary>
public sealed class CameraFrame
{
    public byte[] Pixels = Array.Empty<byte>();
    public int Width;
    public int Height;
    public uint Sequence;
    /// <summary>true si los bytes estan en orden B,G,R,A; false si R,G,B,A.</summary>
    public bool IsBgra;

    /// <summary>Si la fuente conoce donde esta la camara en el espacio de seguimiento.</summary>
    public bool HasPose;
    /// <summary>Camara -> mundo (fila-vector). Solo valido con HasPose.</summary>
    public Matrix4x4 CameraToWorld = Matrix4x4.Identity;
    /// <summary>Proyeccion estilo OpenGL de la camara (fila-vector). Solo valido con HasPose.</summary>
    public Matrix4x4 Projection = Matrix4x4.Identity;
    /// <summary>Subregion (u0, v0, du, dv) de la textura que corresponde a la camara usada.</summary>
    public Vector4 SubRect = new(0, 0, 1, 1);
}

public interface ICameraSource : IDisposable
{
    string Description { get; }

    /// <summary>Texto con el motivo del fallo, o null si todo va bien.</summary>
    string? Error { get; }

    /// <summary>true si entrega pose y proyeccion (modo "ventana" alineado con la realidad).</summary>
    bool SupportsAlignment { get; }

    /// <summary>Devuelve true solo si llego un fotograma nuevo desde la ultima llamada.</summary>
    bool TryGetFrame(out CameraFrame frame);
}
