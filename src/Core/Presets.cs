namespace OpenRealityMixer.Core;

public enum PresetKind { Desk, WheelOrHotas, FloorViewer, LapViewer, Door, WristPanel }

/// <summary>Cajas de ejemplo para los usos mas comunes. La posicion se ajusta despues en la app.</summary>
public static class Presets
{
    public static string Title(PresetKind k) => k switch
    {
        PresetKind.Desk => "Escritorio (teclado y raton)",
        PresetKind.WheelOrHotas => "Volante / HOTAS con chroma key",
        PresetKind.FloorViewer => "Visor de suelo",
        PresetKind.LapViewer => "Visor de regazo",
        PresetKind.Door => "Puerta / habitacion",
        PresetKind.WristPanel => "Ventana en la muneca (hand tracking)",
        _ => k.ToString(),
    };

    public static IEnumerable<CameraBox> Create(PresetKind kind)
    {
        switch (kind)
        {
            case PresetKind.Desk:
                yield return new CameraBox
                {
                    Name = "Teclado y raton",
                    Anchor = AnchorType.World,
                    PosX = 0, PosY = 0.75f, PosZ = -0.45f,
                    Pitch = -90,
                    Width = 0.85f, Height = 0.4f,
                    Activation = ActivationMode.Gaze, GazeAngle = 38, GazeDelay = 0.1f,
                    EdgeSoftness = 0.12f,
                };
                break;

            case PresetKind.WheelOrHotas:
                yield return new CameraBox
                {
                    Name = "Volante / HOTAS",
                    Anchor = AnchorType.World,
                    PosX = 0, PosY = 0.95f, PosZ = -0.5f,
                    Pitch = -35,
                    Width = 0.9f, Height = 0.6f,
                    Activation = ActivationMode.Proximity, ProximityDistance = 1.0f,
                    ChromaEnabled = true, KeyColor = "#00FF00", KeySimilarity = 0.2f, KeySmoothness = 0.12f,
                    EdgeSoftness = 0.04f,
                };
                break;

            case PresetKind.FloorViewer:
                yield return new CameraBox
                {
                    Name = "Suelo",
                    Anchor = AnchorType.World,
                    PosX = 0, PosY = 0.02f, PosZ = 0,
                    Pitch = -90,
                    Width = 1.8f, Height = 1.8f,
                    Shape = BoxShape.Ellipse, EdgeSoftness = 0.35f,
                    Activation = ActivationMode.Button, ButtonHold = true,
                };
                break;

            case PresetKind.LapViewer:
                yield return new CameraBox
                {
                    Name = "Regazo",
                    Anchor = AnchorType.Head,
                    PosX = 0, PosY = -0.5f, PosZ = -0.35f,
                    Pitch = -65,
                    Width = 0.75f, Height = 0.4f,
                    Activation = ActivationMode.Gaze, GazeAngle = 42, GazeDelay = 0.1f,
                    EdgeSoftness = 0.12f,
                };
                break;

            case PresetKind.WristPanel:
                // Punto de partida: el eje exacto de la mano depende del driver. Se afina con "Colocar rapido > En mi mano"
                // o simplemente agarrandola con la otra mano.
                yield return new CameraBox
                {
                    Name = "Ventana en la muneca",
                    Anchor = AnchorType.LeftHand,
                    PosX = 0, PosY = 0.04f, PosZ = 0.05f,
                    Pitch = -90,
                    Width = 0.16f, Height = 0.1f,
                    Activation = ActivationMode.Gaze, GazeAngle = 30, GazeDelay = 0.15f,
                    Shape = BoxShape.Ellipse, EdgeSoftness = 0.12f,
                };
                break;

            case PresetKind.Door:
                yield return new CameraBox
                {
                    Name = "Puerta",
                    Anchor = AnchorType.World,
                    PosX = 1.8f, PosY = 1.0f, PosZ = -1.2f,
                    Yaw = 60,
                    Width = 1.0f, Height = 2.0f,
                    Activation = ActivationMode.Button, ButtonHold = true,
                    EdgeSoftness = 0.1f,
                };
                break;
        }
    }
}
