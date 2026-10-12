using System.Windows;
using System.Windows.Controls;
using OpenRealityMixer.Core;
using OpenRealityMixer.Engine;

namespace OpenRealityMixer.UI;

public partial class MainWindow : Window
{
    public MainWindow(AppSettings settings, MixerEngine engine)
    {
        InitializeComponent();
        DataContext = new MainViewModel(settings, engine);
        Closing += (_, _) => ((MainViewModel)DataContext).FlushSave();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var kind in Enum.GetValues<PresetKind>())
        {
            var item = new MenuItem { Header = Presets.Title(kind), Tag = kind };
            item.Click += (_, _) => ((MainViewModel)DataContext).AddPreset(kind);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var blank = new MenuItem { Header = "Caja en blanco" };
        blank.Click += (_, _) =>
        {
            var vm = (MainViewModel)DataContext;
            var box = new CameraBox();
            vm.Settings.Boxes.Add(box);
            vm.SelectedBox = box;
        };
        menu.Items.Add(blank);

        menu.PlacementTarget = AddButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
