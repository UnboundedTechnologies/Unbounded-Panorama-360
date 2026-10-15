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
                // Scroll after the list has processed the change: this handler can run before the
                // ListBox's own one, and scrolling then sees an inconsistent item count.
                ((INotifyCollectionChanged)vm.LogLines).CollectionChanged += (_, _) =>
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (Log.Items.Count > 0)
                            Log.ScrollIntoView(Log.Items[^1]);
                    }, System.Windows.Threading.DispatcherPriority.Background);
            }
        };
    }

    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
