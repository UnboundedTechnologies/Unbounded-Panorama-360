using System.Windows;
using System.Windows.Input;
using Unbounded.Panorama.Layout;

namespace Unbounded.Panorama.App;

/// <summary>Asks where an imported media goes: the whole screen, the top face or the front face.</summary>
public partial class AreaPickerWindow : Window
{
    public ScreenArea? Result { get; private set; }

    public AreaPickerWindow(string title)
    {
        InitializeComponent();
        FileText.Text = title;
        PreviewKeyDown += OnKey;
        Loaded += (_, _) => WholeButton.Focus();
    }

    /// <summary>Shows the dialog over <paramref name="owner"/>; null when cancelled.</summary>
    public static ScreenArea? Ask(Window owner, string title)
    {
        var dialog = new AreaPickerWindow(title) { Owner = owner, Icon = owner.Icon };
        Services.WindowChrome.UseDarkTitleBar(dialog);
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private void Choose(ScreenArea area)
    {
        Result = area;
        DialogResult = true;
    }

    private void OnWhole(object sender, RoutedEventArgs e) => Choose(ScreenArea.Whole);

    private void OnTop(object sender, RoutedEventArgs e) => Choose(ScreenArea.TopFace);

    private void OnFront(object sender, RoutedEventArgs e) => Choose(ScreenArea.FrontFace);

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.W: Choose(ScreenArea.Whole); break;
            case Key.T: Choose(ScreenArea.TopFace); break;
            case Key.F: Choose(ScreenArea.FrontFace); break;
        }
    }
}
