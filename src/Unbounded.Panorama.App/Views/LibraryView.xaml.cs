using System.Windows;
using System.Windows.Controls;
using Unbounded.Panorama.App.ViewModels;

namespace Unbounded.Panorama.App.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        MineTab.IsChecked = true;
    }

    private void OnTab(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tab } || DataContext is not MainViewModel vm)
            return;
        vm.LibraryTab = tab;
        if (tab == "Cooler")
            vm.RefreshDeviceCommand.Execute(null);
    }

    private void OnImportYouTube(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.OpenYouTube(null);
}
