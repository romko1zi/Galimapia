using Avalonia.Controls;
using Galimapia.Canvas;

namespace Galimapia.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        NameText.Text = AppInfo.Name;
        VersionText.Text = "Version " + AppInfo.Version;
        AuthorText.Text = AppInfo.Author;
    }
}
