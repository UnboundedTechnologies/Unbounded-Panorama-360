using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;
using Unbounded.Panorama.App.ViewModels;

namespace Unbounded.Panorama.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is MainViewModel vm)
            {
                ((INotifyCollectionChanged)vm.LogLines).CollectionChanged += (_, _) =>
                {
                    if (Log.Items.Count > 0)
                        Log.ScrollIntoView(Log.Items[^1]);
                };
            }
        };
    }

    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
