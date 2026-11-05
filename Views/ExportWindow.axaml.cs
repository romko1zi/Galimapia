using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Galimapia.Canvas;

namespace Galimapia.Views;

public partial class ExportWindow : Window
{
    private readonly MapCanvas _map;
    private CancellationTokenSource? _cancel;
    private bool _running;

    public ExportWindow()
    {
        _map = null!;
        InitializeComponent();
    }

    public ExportWindow(MapCanvas map)
    {
        _map = map;
        InitializeComponent();
        FormatBox.SelectionChanged += (_, _) => UpdateHint();
        QualityBox.SelectionChanged += (_, _) => UpdateHint();
        CancelSetupButton.Click += (_, _) => Close();
        StartButton.Click += async (_, _) => await Start();
        StopButton.Click += (_, _) =>
        {
            if (_running)
                _cancel?.Cancel();
            else
                Close();
        };
        Closing += (_, _) =>
        {
            if (_running)
                _cancel?.Cancel();
        };
        UpdateHint();
    }

    private void UpdateHint()
    {
        Hint.Text = FormatBox.SelectedIndex == 3
            ? "A folder of map tiles and a page you open in a browser. This stays light when the project has many sheets."
            : "PDF keeps one picture per sheet. JPEG and PNG are a single flattened picture at the size you pick.";
    }

    private async Task Start()
    {
        var kind = FormatBox.SelectedIndex switch
        {
            1 => ExportKind.Jpeg,
            2 => ExportKind.Png,
            3 => ExportKind.Tiles,
            _ => ExportKind.Pdf,
        };
        var settings = ExportSettings.For(kind, QualityBox.SelectedIndex);
        var name = ProjectName();
        string? path;
        if (kind == ExportKind.Tiles)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a folder for the map tiles",
                AllowMultiple = false,
            });
            var parent = folders.Count == 0 ? null : folders[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(parent))
                return;
            path = Path.Combine(parent, name + " tiles");
        }
        else
        {
            var extension = kind switch
            {
                ExportKind.Jpeg => "jpg",
                ExportKind.Png => "png",
                _ => "pdf",
            };
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export the map",
                SuggestedFileName = name,
                DefaultExtension = extension,
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = ["*." + extension] }],
            });
            path = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
                return;
            if (!path.EndsWith("." + extension, StringComparison.OrdinalIgnoreCase))
                path += "." + extension;
        }

        Setup.IsVisible = false;
        Running.IsVisible = true;
        _running = true;
        _cancel = new CancellationTokenSource();
        var progress = new Progress<ExportReport>(report =>
        {
            var percent = (int)Math.Clamp(Math.Round(report.Fraction * 100d), 0, 100);
            Bar.Value = percent;
            Percent.Text = percent + "%";
            Detail.Text = report.Detail;
        });
        var exported = await _map.ExportAsync(path, settings, progress, _cancel.Token);
        _running = false;
        if (!exported && _cancel.IsCancellationRequested)
        {
            Detail.Text = "Export cancelled";
            Percent.Text = "";
            StopButton.Content = "Close";
            return;
        }

        Close();
    }

    private string ProjectName()
    {
        var folder = _map.ProjectFolder;
        if (string.IsNullOrWhiteSpace(folder))
            return AppInfo.Name;
        return Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }
}
