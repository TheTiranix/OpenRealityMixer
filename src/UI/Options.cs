using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using OpenRealityMixer.Core;
using OpenRealityMixer.Engine;

namespace OpenRealityMixer.UI;

public sealed record Option<T>(T Value, string Label);

/// <summary>Listas para los desplegables, con textos en espanol.</summary>
public static class Options
{
    public static readonly Option<AnchorType>[] Anchors =
    {
        new(AnchorType.World, "Mundo (fija en la habitacion)"),
        new(AnchorType.Head, "Cabeza (me sigue)"),
        new(AnchorType.LeftController, "Mando izquierdo"),
        new(AnchorType.RightController, "Mando derecho"),
        new(AnchorType.Tracker, "Tracker (Vive / Tundra)"),
        new(AnchorType.LeftHand, "Mano izquierda (hand tracking)"),
        new(AnchorType.RightHand, "Mano derecha (hand tracking)"),
    };

    public static readonly Option<RenderMode>[] RenderModes =
    {
        new(RenderMode.Aligned, "Ventana a la realidad (camara del visor: Index, WMR)"),
        new(RenderMode.Flat, "Recorte plano (webcam USB / imagen simple)"),
        new(RenderMode.PassthroughKey, "Passthrough por color (Quest, Pico: chroma key de ALVR)"),
    };

    public static readonly Option<BoxShape>[] Shapes =
    {
        new(BoxShape.Rectangle, "Rectangulo"),
        new(BoxShape.Ellipse, "Elipse / circulo"),
        new(BoxShape.Custom, "Forma dibujada a mano (con los mandos o las manos)"),
    };

    public static readonly Option<ActivationMode>[] Activations =
    {
        new(ActivationMode.Always, "Siempre visible"),
        new(ActivationMode.Proximity, "Por proximidad (se desvanece al alejarte)"),
        new(ActivationMode.Gaze, "Al mirarla"),
        new(ActivationMode.Button, "Con un boton"),
    };

    public static readonly Option<CameraSourceKind>[] Sources =
    {
        new(CameraSourceKind.Headset, "Camara del visor (via SteamVR)"),
        new(CameraSourceKind.Webcam, "Webcam USB"),
        new(CameraSourceKind.TestPattern, "Patron de prueba"),
    };

    public static readonly Option<PlaceOrientation>[] Orientations =
    {
        new(PlaceOrientation.VerticalFacingMe, "Vertical, mirando hacia mi"),
        new(PlaceOrientation.FlatHorizontal, "Plana (sobre una mesa)"),
    };

    public static readonly Option<PenTipMode>[] PenTips =
    {
        new(PenTipMode.Auto, "Automatica (gatillo = punta del mando; pellizco = punta de los dedos)"),
        new(PenTipMode.Controller, "Siempre la punta del mando"),
        new(PenTipMode.Fingers, "Siempre la punta de los dedos (hand tracking)"),
    };

    public static readonly string[] KeyColors =
    {
        "#FF00FF", "#00FF00", "#0000FF", "#000000", "#FFFFFF", "#FF8000",
    };
}

/// <summary>Visible si el valor (enum o bool) coincide con alguno de los nombres del parametro; "!" invierte.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var p = (parameter as string ?? "").Trim();
        bool negate = p.StartsWith('!');
        if (negate) p = p[1..];
        var current = value?.ToString() ?? "";
        bool match = p.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase));
        return match ^ negate ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Muestra la muestra de color de un "#RRGGBB".</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            if (value is string s && s.Length > 0)
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.StartsWith('#') ? s : "#" + s));
        }
        catch { /* texto a medio escribir */ }
        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value == null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
