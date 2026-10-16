using System.Windows;
using System.Windows.Controls;
namespace Anno1800IslandTool.UI;

internal sealed class QuitConfirmationWindow : Window
{
    public QuitConfirmationWindow()
    {
        Title = UiLocalizationManager.T("Quitter le programme");
        Width = 430; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = UiLocalizationManager.T("Voulez-vous vraiment quitter ? Le travail non exporté sera perdu."), TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,20,0,0) };
        var yes = new Button { Content = UiLocalizationManager.T("Oui"), MinWidth = 85, Padding = new Thickness(10,5,10,5), Margin = new Thickness(0,0,10,0) };
        var no = new Button { Content = UiLocalizationManager.T("Non"), MinWidth = 85, Padding = new Thickness(10,5,10,5), IsCancel = true, IsDefault = true };
        yes.Click += (_, _) => DialogResult = true;
        no.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(yes); buttons.Children.Add(no); panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => no.Focus();
    }
}
