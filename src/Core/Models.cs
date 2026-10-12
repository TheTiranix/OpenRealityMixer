using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenRealityMixer.Core;

/// <summary>A que esta pegada la caja de realidad.</summary>
public enum AnchorType { World, Head, LeftController, RightController, Tracker, LeftHand, RightHand }

/// <summary>Cuando se muestra la caja.</summary>
public enum ActivationMode { Always, Proximity, Gaze, Button }

/// <summary>Custom: contorno dibujado a mano con los mandos o las manos (se guarda en <see cref="CameraBox.CustomShape"/>).</summary>
public enum BoxShape { Rectangle, Ellipse, Custom }

/// <summary>
/// Aligned: la caja es una "ventana" a la realidad, muestra lo que hay realmente detras de ella.
/// Flat: simplemente pega un recorte de la imagen de la camara (util para webcams).
/// PassthroughKey: no usa la camara; pinta la forma de la caja con un color solido y es la app del visor
/// (Virtual Desktop, ALVR) quien reemplaza ese color por el passthrough real. Es el unico modo posible en Quest,
/// porque Meta no entrega los pixeles de la camara al PC.
/// </summary>
public enum RenderMode { Aligned, Flat, PassthroughKey }

public enum CameraSourceKind { Headset, Webcam, TestPattern }

/// <summary>De donde sale la punta del lapiz al dibujar formas.</summary>
public enum PenTipMode
{
    /// <summary>Con el gatillo, la punta del mando; con el pellizco, la punta de los dedos.</summary>
    Auto,
    /// <summary>Siempre la punta del mando (unos 5 cm por delante).</summary>
    Controller,
    /// <summary>Siempre la punta de los dedos del hand tracking (pulgar e indice).</summary>
    Fingers,
}

/// <summary>Una "caja de realidad": una ventana flotante que muestra la camara del visor.</summary>
public partial class CameraBox : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [ObservableProperty] private string name = "Caja nueva";
    [ObservableProperty] private bool enabled = true;

    // --- Posicion ---
    [ObservableProperty] private AnchorType anchor = AnchorType.World;
    [ObservableProperty] private string trackerSerial = "";
    /// <summary>Posicion en metros respecto del ancla (espacio de pie si el ancla es World).</summary>
    [ObservableProperty] private float posX;
    [ObservableProperty] private float posY = 1.0f;
    [ObservableProperty] private float posZ = -0.6f;
    /// <summary>Rotacion en grados.</summary>
    [ObservableProperty] private float yaw;
    [ObservableProperty] private float pitch;
    [ObservableProperty] private float roll;
    [ObservableProperty] private float width = 0.6f;
    [ObservableProperty] private float height = 0.4f;
    /// <summary>Se puede mover/escalar pellizcandola con las manos (hand tracking).</summary>
    [ObservableProperty] private bool grabbable = true;

    // --- Imagen ---
    [ObservableProperty] private RenderMode renderMode = RenderMode.Aligned;
    [ObservableProperty] private BoxShape shape = BoxShape.Rectangle;
    /// <summary>Contorno de la forma dibujada: puntos normalizados "x,y;x,y;..." (0..1, y hacia abajo). Vacio si no hay.</summary>
    [ObservableProperty] private string customShape = "";
    /// <summary>
    /// Modo passthrough por color: usar un color propio en vez del color clave global. ALVR solo quita un color a la vez,
    /// asi que lo normal es dejarlo desmarcado; sirve para cajas decorativas o si cambias el color en ALVR.
    /// </summary>
    [ObservableProperty] private bool useOwnColor;
    [ObservableProperty] private string ownColor = "#00FFFF";
    /// <summary>0 = borde duro, 1 = muy difuminado.</summary>
    [ObservableProperty] private float edgeSoftness = 0.08f;
    [ObservableProperty] private float opacity = 1.0f;
    [ObservableProperty] private float brightness;
    [ObservableProperty] private float contrast = 1.0f;
    [ObservableProperty] private float saturation = 1.0f;
    /// <summary>Pixeles de textura por metro: mas = mas nitido y mas pesado.</summary>
    [ObservableProperty] private int pixelsPerMeter = 900;
    [ObservableProperty] private bool mirrorHorizontal;

    // --- Recorte (solo modo Flat) ---
    [ObservableProperty] private float cropX;
    [ObservableProperty] private float cropY;
    [ObservableProperty] private float cropW = 1.0f;
    [ObservableProperty] private float cropH = 1.0f;

    // --- Activacion ---
    [ObservableProperty] private ActivationMode activation = ActivationMode.Always;
    /// <summary>Distancia (m) desde la cabeza a la que la caja desaparece.</summary>
    [ObservableProperty] private float proximityDistance = 1.2f;
    /// <summary>Angulo maximo (grados) entre la mirada y la caja para que aparezca.</summary>
    [ObservableProperty] private float gazeAngle = 25f;
    /// <summary>Segundos mirando la caja antes de que aparezca.</summary>
    [ObservableProperty] private float gazeDelay = 0.15f;
    /// <summary>Segundos que tarda en aparecer/desaparecer.</summary>
    [ObservableProperty] private float fadeSeconds = 0.25f;
    /// <summary>Boton asignado, ver <see cref="Engine.InputPoller"/>. Vacio = ninguno.</summary>
    [ObservableProperty] private string buttonBinding = "";
    /// <summary>true = visible solo mientras se mantiene el boton; false = alterna.</summary>
    [ObservableProperty] private bool buttonHold;
    /// <summary>Estado inicial del modo alternar.</summary>
    [ObservableProperty] private bool buttonStartsVisible = true;

    // --- Chroma key ---
    [ObservableProperty] private bool chromaEnabled;
    [ObservableProperty] private string keyColor = "#00FF00";
    [ObservableProperty] private float keySimilarity = 0.18f;
    [ObservableProperty] private float keySmoothness = 0.12f;
    [ObservableProperty] private float keySpill = 0.5f;

    public CameraBox Clone()
    {
        var c = (CameraBox)MemberwiseClone();
        c.Id = Guid.NewGuid();
        c.Name = Name + " (copia)";
        return c;
    }
}

/// <summary>Configuracion global de la aplicacion.</summary>
public partial class AppSettings : ObservableObject
{
    public ObservableCollection<CameraBox> Boxes { get; set; } = new();

    [ObservableProperty] private CameraSourceKind cameraSource = CameraSourceKind.Headset;
    [ObservableProperty] private string webcamName = "";
    [ObservableProperty] private bool startWithSteamVR;
    [ObservableProperty] private bool startMinimized;

    /// <summary>
    /// Tarjeta grafica del motor, por nombre. Vacio = automatica (la misma GPU que usa SteamVR para el visor,
    /// que es la unica en la que SteamVR puede mostrar los overlays).
    /// </summary>
    [ObservableProperty] private string gpuAdapterName = "";

    /// <summary>
    /// Color solido que se pinta en las cajas de modo PassthroughKey. Debe ser exactamente el mismo que se configura
    /// como color de passthrough en el visor (Virtual Desktop > Streaming > Passthrough, o el chroma key de ALVR).
    /// </summary>
    [ObservableProperty] private string passthroughKeyColor = "#FF00FF";

    /// <summary>Boton extra (teclado, gamepad, gesto...) para mantener pulsado mientras se dibuja una forma; vacio = solo gatillo / pellizco.</summary>
    [ObservableProperty] private string drawBinding = "";
    [ObservableProperty] private PenTipMode penTip = PenTipMode.Auto;

    // --- Hand tracking ---
    /// <summary>Leer las manos por la entrada esqueletica de SteamVR (Steam Link, ALVR).</summary>
    [ObservableProperty] private bool handTrackingEnabled = true;
    /// <summary>Permitir mover y escalar cajas pellizcandolas.</summary>
    [ObservableProperty] private bool handGrabEnabled = true;
    /// <summary>Distancia entre las puntas de pulgar e indice por debajo de la cual se considera pellizco.</summary>
    [ObservableProperty] private float pinchDistanceCm = 2.2f;

    // Calibracion del mapeo camara -> mundo. Se corrigen aqui si la imagen sale invertida en un visor concreto.
    [ObservableProperty] private bool calibFlipU;
    [ObservableProperty] private bool calibFlipV;
    [ObservableProperty] private bool calibSwapRedBlue;
    /// <summary>Ajuste manual del campo de vision de la camara (1 = el que reporta el driver).</summary>
    [ObservableProperty] private float calibFovScale = 1.0f;

    [JsonIgnore] public string FilePath { get; set; } = "";
}
