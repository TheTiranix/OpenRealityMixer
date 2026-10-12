using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenRealityMixer.Core;

namespace OpenRealityMixer.UI;

/// <summary>
/// Selector de color: muestra el color, acepta un codigo hexadecimal, colores rapidos y ajuste fino RGB.
/// A diferencia de un cuadro de texto editable, aplica el cambio en el instante en que es valido (no al perder el foco).
/// </summary>
public partial class ColorPickerBox : UserControl
{
    private static readonly (string Hex, string Name)[] Presets =
    {
        ("#FF00FF", "Magenta"), ("#00FF00", "Verde"), ("#0000FF", "Azul"), ("#00FFFF", "Cian"),
        ("#FF0000", "Rojo"), ("#FFFF00", "Amarillo"), ("#FF8000", "Naranja"), ("#FFFFFF", "Blanco"), ("#000000", "Negro"),
    };

    private bool _updating;

    public ColorPickerBox()
    {
        InitializeComponent();
        foreach (var (hex, name) in Presets)
        {
            var b = new Button
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 4, 2),
                Padding = new Thickness(0),
                Background = Brush(hex),
                BorderBrush = Brushes.Gray,
                ToolTip = $"{name} ({hex})",
                Tag = hex,
            };
            b.Click += (_, _) => Color = hex;
            PresetPanel.Children.Add(b);
        }
        Refresh(Color);
    }

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(nameof(Color), typeof(string), typeof(ColorPickerBox),
        new FrameworkPropertyMetadata("#FF00FF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((ColorPickerBox)d).Refresh((string?)e.NewValue)));

    public static readonly DependencyProperty RProperty = DependencyProperty.Register(nameof(R), typeof(double), typeof(ColorPickerBox),
        new PropertyMetadata(0.0, (d, _) => ((ColorPickerBox)d).FromChannels()));
    public static readonly DependencyProperty GProperty = DependencyProperty.Register(nameof(G), typeof(double), typeof(ColorPickerBox),
        new PropertyMetadata(0.0, (d, _) => ((ColorPickerBox)d).FromChannels()));
    public static readonly DependencyProperty BProperty = DependencyProperty.Register(nameof(B), typeof(double), typeof(ColorPickerBox),
        new PropertyMetadata(0.0, (d, _) => ((ColorPickerBox)d).FromChannels()));

    /// <summary>Color en formato #RRGGBB.</summary>
    public string Color { get => (string)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public double R { get => (double)GetValue(RProperty); set => SetValue(RProperty, value); }
    public double G { get => (double)GetValue(GProperty); set => SetValue(GProperty, value); }
    public double B { get => (double)GetValue(BProperty); set => SetValue(BProperty, value); }

    private static SolidColorBrush Brush(string hex)
    {
        VrMath.TryParseColor(hex, out var c);
        return new SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(c.X * 255 + 0.5f), (byte)(c.Y * 255 + 0.5f), (byte)(c.Z * 255 + 0.5f)));
    }

    /// <summary>Pone al dia el cuadro de color, el texto y los deslizadores a partir del color actual.</summary>
    private void Refresh(string? hex)
    {
        if (Swatch == null) return; // aun no se construyo el control
        if (!VrMath.TryParseColor(hex, out var c)) return;
        _updating = true;
        try
        {
            Swatch.Background = Brush(VrMath.ToHex(c));
            // no se pisa lo que el usuario esta escribiendo
            if (!HexBox.IsKeyboardFocused) HexBox.Text = VrMath.ToHex(c);
            R = Math.Round(c.X * 255);
            G = Math.Round(c.Y * 255);
            B = Math.Round(c.Z * 255);
        }
        finally { _updating = false; }
    }

    private void FromChannels()
    {
        if (_updating || Swatch == null) return;
        var hex = VrMath.ToHex(new Vector3((float)R / 255f, (float)G / 255f, (float)B / 255f));
        if (!string.Equals(hex, Color, StringComparison.OrdinalIgnoreCase)) Color = hex;
    }

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        // valido -> se aplica ya; a medio escribir -> se espera sin tocar el color actual
        if (VrMath.TryParseColor(HexBox.Text, out var c))
        {
            var hex = VrMath.ToHex(c);
            if (!string.Equals(hex, Color, StringComparison.OrdinalIgnoreCase)) Color = hex;
        }
    }

    private void HexBox_LostFocus(object sender, RoutedEventArgs e) => Restore();

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Restore();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    /// <summary>Si el texto no es un color valido, vuelve al color vigente; si lo es, lo muestra normalizado.</summary>
    private void Restore()
    {
        _updating = true;
        try
        {
            if (VrMath.TryParseColor(Color, out var c)) HexBox.Text = VrMath.ToHex(c);
        }
        finally { _updating = false; }
    }
}
