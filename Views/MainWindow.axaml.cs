using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Galimapia.Canvas;

namespace Galimapia.Views;

public partial class MainWindow : Window
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;
    private static readonly Bitmap AvailableIcon = LoadIcon("welcome-ok.png");
    private static readonly Bitmap MissingIcon = LoadIcon("welcome-missing.png");
    private static readonly IBrush CardBrush = new SolidColorBrush(Color.Parse("#2E2B28"));
    private static readonly IBrush CardSelectedBrush = new SolidColorBrush(Color.Parse("#3C3428"));
    private static readonly IBrush GoldBrush = new SolidColorBrush(Color.Parse("#D7B98A"));

    private readonly Dictionary<string, Bitmap> _thumbs = new();
    private readonly HashSet<string> _thumbsLoading = new();
    private bool _syncing;
    private bool _opacityPointer;
    private bool _opacityKey;
    private bool _tonePointer;
    private bool _toneKey;
    private bool _layersOpen = true;
    private int _layerStructure = -1;
    private MapFragment? _dragFragment;
    private bool _dragMoved;
    private Point _dragOrigin;
    private int _dropVisual = -1;
    private int _thumbGeneration;
    private readonly RecentProjects _recent = RecentProjects.Default;
    private readonly DispatcherTimer _autosave;
    private int _savedEditVersion;
    private bool _allowClose;
    private string? _createParent;

    public MainWindow()
    {
        InitializeComponent();
        Map.ViewChanged += (_, _) => UpdateStatus();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        OpacitySlider.ValueChanged += (_, _) => OnOpacityChanged();
        OpacitySlider.AddHandler(PointerPressedEvent, OnOpacityPressed, RoutingStrategies.Tunnel);
        OpacitySlider.AddHandler(PointerReleasedEvent, OnOpacityReleased, RoutingStrategies.Tunnel);
        OpacitySlider.AddHandler(KeyDownEvent, OnOpacityKeyDown, RoutingStrategies.Tunnel);
        OpacitySlider.AddHandler(KeyUpEvent, OnOpacityKeyUp, RoutingStrategies.Tunnel);
        WireTone(BrightSlider);
        WireTone(ContrastSlider);
        WireTone(HueSlider);
        RotateLeftButton.Click += (_, _) => Map.StepRotation(-90);
        RotateRightButton.Click += (_, _) => Map.StepRotation(90);
        Rotate0Button.Click += (_, _) => Map.CommitRotation(0);
        Rotate90Button.Click += (_, _) => Map.CommitRotation(90);
        Rotate180Button.Click += (_, _) => Map.CommitRotation(180);
        Rotate270Button.Click += (_, _) => Map.CommitRotation(270);
        MirrorButton.Click += (_, _) => Map.ToggleMirror();
        ResetButton.Click += (_, _) => Map.ResetAppearance();
        CropRectButton.Click += (_, _) => Map.SetTool(MapCanvas.MapTool.Rect);
        CropPolyButton.Click += (_, _) => Map.SetTool(MapCanvas.MapTool.Poly);
        CropApplyButton.Click += (_, _) => Map.ConfirmCrop();
        JoinButton.Click += (_, _) => Map.SetTool(MapCanvas.MapTool.Align);
        WarpButton.Click += (_, _) => Map.SetTool(MapCanvas.MapTool.Warp);
        DeleteButton.Click += (_, _) => Map.DeleteSelected();
        LayerUpButton.Click += (_, _) => Map.BringForward();
        LayerDownButton.Click += (_, _) => Map.SendBackward();
        LayerToggle.Click += (_, _) => SetLayersOpen(!_layersOpen);
        ProjectsButton.Click += (_, _) => ShowWelcome();
        SaveButton.Click += async (_, _) => await SaveProject();
        ExportButton.Click += async (_, _) =>
        {
            if (Map.SheetCount == 0)
                return;
            await new ExportWindow(Map).ShowDialog(this);
        };
        AboutButton.Click += async (_, _) => await new AboutWindow().ShowDialog(this);
        NewProjectButton.Click += (_, _) => ShowCreate();
        OpenProjectButton.Click += async (_, _) => await OpenProject();
        WelcomeBack.Click += (_, _) => Welcome.IsVisible = false;
        ChooseFolderButton.Click += async (_, _) => await ChooseCreateFolder();
        CreateProjectButton.Click += async (_, _) => await CreateProject();
        ProjectNameBox.TextChanged += (_, _) => RefreshCreatePreview();
        UpdateStatus();
        ShowWelcome();
        _autosave = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _autosave.Tick += async (_, _) => await Autosave();
        _autosave.Start();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_allowClose)
        {
            _autosave.Stop();
            base.OnClosing(e);
            return;
        }

        var folder = Map.ProjectFolder;
        if (string.IsNullOrWhiteSpace(folder) || Map.EditVersion == _savedEditVersion)
        {
            _autosave.Stop();
            base.OnClosing(e);
            return;
        }

        SessionLog.Action("window closing");
        e.Cancel = true;
        await Autosave();
        _allowClose = true;
        Close();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        UpdateStatus();
    }

    private async void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            await SaveProject();
            return;
        }

        if (Welcome.IsVisible)
            return;

        if (!Map.HandleKey(e.Key, e.KeyModifiers))
            return;

        e.Handled = true;
        _opacityPointer = false;
        _opacityKey = false;
        _tonePointer = false;
        _toneKey = false;
        UpdateStatus();
    }

    private void WireTone(Slider slider)
    {
        slider.ValueChanged += (_, _) => OnToneChanged();
        slider.AddHandler(PointerPressedEvent, OnTonePressed, RoutingStrategies.Tunnel);
        slider.AddHandler(PointerReleasedEvent, OnToneReleased, RoutingStrategies.Tunnel);
        slider.AddHandler(KeyDownEvent, OnToneKeyDown, RoutingStrategies.Tunnel);
        slider.AddHandler(KeyUpEvent, OnToneKeyUp, RoutingStrategies.Tunnel);
    }

    private void OnTonePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsEnabled)
            return;

        var starting = !_tonePointer && !_toneKey;
        _tonePointer = true;
        if (starting && Map.Selected is not null)
            Map.BeginOpacityEdit();
    }

    private void OnToneReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_tonePointer)
            return;

        _tonePointer = false;
        if (_toneKey)
            return;

        Map.EndOpacityEdit();
        UpdateStatus();
    }

    private void OnToneKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsEnabled)
            return;
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down))
            return;
        if (_tonePointer || _toneKey)
            return;

        _toneKey = true;
        if (Map.Selected is not null)
            Map.BeginOpacityEdit();
    }

    private void OnToneKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down) || !_toneKey)
            return;

        _toneKey = false;
        if (_tonePointer)
            return;

        Map.EndOpacityEdit();
        UpdateStatus();
    }

    private void OnToneChanged()
    {
        if (_syncing || Map.Selected is null)
            return;

        Map.PreviewBrightness(BrightSlider.Value / 100d);
        Map.PreviewContrast(ContrastSlider.Value / 100d);
        Map.PreviewHue(HueSlider.Value);
        BrightText.Text = string.Format(Culture, "{0,4:0}", BrightSlider.Value);
        ContrastText.Text = string.Format(Culture, "{0,4:0}", ContrastSlider.Value);
        HueText.Text = string.Format(Culture, "{0,4:0}", HueSlider.Value);
    }

    private void ShowWelcome()
    {
        Welcome.IsVisible = true;
        WelcomeBack.IsVisible = !string.IsNullOrWhiteSpace(Map.ProjectFolder);
        CreatePanel.IsVisible = false;
        CreateError.Text = "";
        WelcomeError.Text = "";
        RebuildRecent();
    }

    private void ShowCreate()
    {
        CreatePanel.IsVisible = true;
        CreateError.Text = "";
        WelcomeError.Text = "";
        RefreshCreatePreview();
        ProjectNameBox.Focus();
    }

    private void RefreshCreatePreview()
    {
        if (!RecentProjects.TryProjectName(ProjectNameBox.Text, out var name) || string.IsNullOrWhiteSpace(_createParent))
        {
            CreatePreview.Text = "A folder with this name is created inside the folder you choose.";
            return;
        }

        CreatePreview.Text = Path.Combine(_createParent, name);
    }

    private async Task ChooseCreateFolder()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where to create the project folder",
            AllowMultiple = false,
        });
        if (folders.Count == 0)
            return;

        var folder = folders[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(folder))
            return;

        _createParent = folder;
        CreateFolderText.Text = folder;
        RefreshCreatePreview();
    }

    private async Task CreateProject()
    {
        CreateError.Text = "";
        WelcomeError.Text = "";
        if (!RecentProjects.TryProjectName(ProjectNameBox.Text, out var name))
        {
            CreateError.Text = "Enter a project name that can be a folder name.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_createParent))
        {
            CreateError.Text = "Choose the folder where the project folder will be created.";
            return;
        }

        var folder = Path.Combine(_createParent, name);
        try
        {
            if (Directory.Exists(folder) && !ProjectFile.IsProject(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            {
                CreateError.Text = "That folder already exists and is not a Galimapia project.";
                return;
            }

            if (!ProjectFile.IsProject(folder))
                ProjectFile.CreateEmpty(folder);
            ResetThumbs();
            if (!await Map.OpenProjectAsync(folder))
            {
                CreateError.Text = string.IsNullOrWhiteSpace(Map.Notice) ? "The project could not be opened." : Map.Notice;
                UpdateStatus();
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            CreateError.Text = ex.Message;
            UpdateStatus();
            return;
        }

        _recent.Remember(folder, name);
        EnterEditor(folder);
    }

    private async Task OpenProject()
    {
        WelcomeError.Text = "";
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open a Galimapia project",
            AllowMultiple = false,
        });
        if (folders.Count == 0)
            return;

        var folder = folders[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(folder))
            return;

        if (!ProjectFile.IsProject(folder))
        {
            WelcomeError.Text = "That folder has no project.json.";
            return;
        }

        ResetThumbs();
        if (!await Map.OpenProjectAsync(folder))
        {
            WelcomeError.Text = string.IsNullOrWhiteSpace(Map.Notice) ? "The project could not be opened." : Map.Notice;
            UpdateStatus();
            return;
        }

        _recent.Remember(folder, Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        EnterEditor(folder);
    }

    private async Task OpenRecent(RecentProject project)
    {
        if (!project.IsAvailable)
        {
            WelcomeError.Text = "That project folder was moved or deleted.";
            return;
        }

        ResetThumbs();
        if (!await Map.OpenProjectAsync(project.Folder))
        {
            WelcomeError.Text = string.IsNullOrWhiteSpace(Map.Notice) ? "The project could not be opened." : Map.Notice;
            RebuildRecent();
            UpdateStatus();
            return;
        }

        _recent.Remember(project.Folder, project.Name);
        EnterEditor(project.Folder);
    }

    private void EnterEditor(string folder)
    {
        Welcome.IsVisible = false;
        _savedEditVersion = Map.EditVersion;
        Title = "Galimapia — " + Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        Map.Focus();
    }

    private void RebuildRecent()
    {
        RecentList.Children.Clear();
        var projects = _recent.Load();
        RecentEmpty.IsVisible = projects.Count == 0;
        foreach (var project in projects)
            RecentList.Children.Add(BuildRecentCard(project));
    }

    private Border BuildRecentCard(RecentProject project)
    {
        var available = project.IsAvailable;
        var icon = new Image
        {
            Width = 22,
            Height = 22,
            Source = available ? AvailableIcon : MissingIcon,
        };
        var name = new TextBlock
        {
            Text = project.Name,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#E6D7C3")),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var path = new TextBlock
        {
            Text = project.Folder,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#8A8378")),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var status = new TextBlock
        {
            Text = available ? "Available" : "Unavailable",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse(available ? "#8FDB6A" : "#E07070")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(name);
        text.Children.Add(path);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(status, 2);
        text.Margin = new Thickness(10, 0, 0, 0);
        row.Children.Add(icon);
        row.Children.Add(text);
        row.Children.Add(status);
        var card = new Border
        {
            Child = row,
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(10),
            Background = CardBrush,
            BorderBrush = new SolidColorBrush(Color.Parse("#3C3428")),
            BorderThickness = new Thickness(1),
            Opacity = available ? 1 : 0.72,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        card.PointerPressed += async (_, e) =>
        {
            if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
                return;

            e.Handled = true;
            await OpenRecent(project);
        };
        return card;
    }

    private static Bitmap LoadIcon(string file)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://Galimapia/Assets/icons/{file}"));
        return new Bitmap(stream);
    }

    private async Task SaveProject()
    {
        if (!await SaveOpenProject("Saved"))
        {
            if (string.IsNullOrWhiteSpace(Map.ProjectFolder))
                ShowWelcome();
            return;
        }

        if (!Welcome.IsVisible)
            Map.Focus();
    }

    private async Task Autosave()
    {
        if (Map.EditVersion == _savedEditVersion)
            return;

        await SaveOpenProject("Autosaved");
    }

    private async Task<bool> SaveOpenProject(string notice)
    {
        var folder = Map.ProjectFolder;
        if (string.IsNullOrWhiteSpace(folder))
            return false;

        if (!await Map.SaveProjectAsync(folder, notice))
            return false;

        _savedEditVersion = Map.EditVersion;
        Title = "Galimapia — " + Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return true;
    }

    private void OnOpacityPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!OpacitySlider.IsEnabled)
            return;

        var starting = !_opacityPointer && !_opacityKey;
        _opacityPointer = true;
        if (starting)
            Map.BeginOpacityEdit();
    }

    private void OnOpacityReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_opacityPointer)
            return;

        _opacityPointer = false;
        if (_opacityKey)
            return;

        Map.EndOpacityEdit();
        UpdateStatus();
    }

    private void OnOpacityKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down) || !OpacitySlider.IsEnabled)
            return;

        if (_opacityPointer || _opacityKey)
            return;

        _opacityKey = true;
        Map.BeginOpacityEdit();
    }

    private void OnOpacityKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down) || !_opacityKey)
            return;

        _opacityKey = false;
        if (_opacityPointer)
            return;

        Map.EndOpacityEdit();
        UpdateStatus();
    }

    private void OnOpacityChanged()
    {
        if (_syncing)
            return;

        Map.PreviewOpacity(OpacitySlider.Value / 100d);
        OpacityText.Text = string.Format(Culture, "{0,3:0}%", OpacitySlider.Value);
    }

    private void UpdateStatus()
    {
        ZoomText.Text = Map.ZoomText;
        CenterText.Text = Map.CenterText;
        CursorText.Text = Map.CursorText;
        SheetsText.Text = Map.SheetsText;
        UpdateEditBar();
        UpdateLayers();
    }

    private void UpdateEditBar()
    {
        var sheet = Map.Selected;
        var hasSheet = sheet is not null;
        RotateLeftButton.IsEnabled = hasSheet;
        RotateRightButton.IsEnabled = hasSheet;
        Rotate0Button.IsEnabled = hasSheet;
        Rotate90Button.IsEnabled = hasSheet;
        Rotate180Button.IsEnabled = hasSheet;
        Rotate270Button.IsEnabled = hasSheet;
        MirrorButton.IsEnabled = hasSheet;
        ResetButton.IsEnabled = hasSheet;
        CropRectButton.IsEnabled = hasSheet;
        CropPolyButton.IsEnabled = hasSheet;
        JoinButton.IsEnabled = hasSheet;
        WarpButton.IsEnabled = hasSheet;
        DeleteButton.IsEnabled = hasSheet;
        ExportButton.IsEnabled = Map.SheetCount > 0;
        OpacitySlider.IsEnabled = hasSheet;
        BrightSlider.IsEnabled = hasSheet;
        ContrastSlider.IsEnabled = hasSheet;
        HueSlider.IsEnabled = hasSheet;
        var index = hasSheet ? Map.Sheets.ToList().IndexOf(sheet!) : -1;
        LayerUpButton.IsEnabled = hasSheet && index >= 0 && index < Map.SheetCount - 1;
        LayerDownButton.IsEnabled = hasSheet && index > 0;
        MarkTool(CropRectButton, Map.Tool == MapCanvas.MapTool.Rect);
        MarkTool(CropPolyButton, Map.Tool == MapCanvas.MapTool.Poly);
        MarkTool(JoinButton, Map.Tool == MapCanvas.MapTool.Align);
        MarkTool(WarpButton, Map.Tool == MapCanvas.MapTool.Warp);
        CropApplyButton.IsVisible = Map.CanConfirmCrop;
        ToolHint.Text = Map.ToolHint;
        ToolHint.IsVisible = Map.Tool != MapCanvas.MapTool.None;

        SheetText.Text = hasSheet ? sheet!.Name : "No sheet";
        PoseText.IsVisible = hasSheet;
        PoseText.Text = hasSheet ? FormatPose(sheet!) : "";

        if (_opacityPointer || _opacityKey)
        {
            OpacityText.Text = string.Format(Culture, "{0,3:0}%", OpacitySlider.Value);
            return;
        }

        var percent = hasSheet ? sheet!.Opacity * 100d : 100d;
        if (Math.Abs(OpacitySlider.Value - percent) > 0.01)
        {
            _syncing = true;
            OpacitySlider.Value = percent;
            _syncing = false;
        }

        OpacityText.Text = string.Format(Culture, "{0,3:0}%", percent);
        if (_tonePointer || _toneKey)
            return;

        var bright = hasSheet ? sheet!.Brightness * 100d : 0d;
        var contrast = hasSheet ? sheet!.Contrast * 100d : 100d;
        var hue = hasSheet ? sheet!.Hue : 0d;
        if (Math.Abs(BrightSlider.Value - bright) > 0.01
            || Math.Abs(ContrastSlider.Value - contrast) > 0.01
            || Math.Abs(HueSlider.Value - hue) > 0.01)
        {
            _syncing = true;
            BrightSlider.Value = bright;
            ContrastSlider.Value = contrast;
            HueSlider.Value = hue;
            _syncing = false;
        }

        BrightText.Text = string.Format(Culture, "{0,4:0}", bright);
        ContrastText.Text = string.Format(Culture, "{0,4:0}", contrast);
        HueText.Text = string.Format(Culture, "{0,4:0}", hue);
    }

    private static void MarkTool(Button button, bool on)
    {
        if (on)
            button.Classes.Add("on");
        else
            button.Classes.Remove("on");
    }

    private static string FormatPose(MapFragment sheet)
    {
        var angle = sheet.RotationDegrees % 360d;
        if (angle < 0)
            angle += 360d;

        var text = string.Format(Culture, "{0,6:0.0}°    ×{1,4:0.00}", angle, sheet.Scale);
        if (sheet.FlipX)
            text += "  mirror";
        if (sheet.HasCut)
            text += "  crop";
        return text;
    }

    private void SetLayersOpen(bool open)
    {
        _layersOpen = open;
        LayerPane.Width = open ? 280 : 48;
        LayerIcon.IsVisible = open;
        LayerTitle.IsVisible = open;
        LayerUpButton.IsVisible = open;
        LayerDownButton.IsVisible = open;
        LayerHint.IsVisible = open;
        LayerScroll.IsVisible = open;
        LayerToggle.Content = open ? "›" : "‹";
        LayerHeader.Padding = open ? new Thickness(8, 6) : new Thickness(0);
        Grid.SetColumn(LayerToggle, open ? 4 : 1);
        LayerToggle.HorizontalAlignment = open
            ? Avalonia.Layout.HorizontalAlignment.Stretch
            : Avalonia.Layout.HorizontalAlignment.Center;
        ToolTip.SetTip(LayerToggle, open ? "Collapse layers" : "Expand layers");
        if (open)
            _layerStructure = -1;
        UpdateLayers();
    }

    private void UpdateLayers()
    {
        if (!_layersOpen)
            return;

        if (Map.StructureVersion != _layerStructure)
            RebuildLayers();
        else
            RestyleLayers();

        PumpThumbs();
    }

    private void RebuildLayers()
    {
        _layerStructure = Map.StructureVersion;
        _dragFragment = null;
        _dragMoved = false;
        LayerList.Children.Clear();
        var sheets = Map.Sheets;
        if (sheets.Count == 0)
        {
            LayerList.Children.Add(new TextBlock
            {
                Text = "No sheets",
                Margin = new Thickness(12, 8, 12, 0),
                Foreground = new SolidColorBrush(Color.Parse("#8A8378")),
            });
            return;
        }

        for (var visual = 0; visual < sheets.Count; visual++)
            LayerList.Children.Add(BuildCard(sheets[sheets.Count - 1 - visual]));

        RestyleLayers();
    }

    private Border BuildCard(MapFragment sheet)
    {
        var image = new Image
        {
            Width = 72,
            Height = 48,
            Stretch = Stretch.UniformToFill,
        };
        var name = new TextBlock
        {
            Text = sheet.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(4),
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(image);
        grid.Children.Add(name);
        var card = new Border
        {
            Tag = sheet,
            Child = grid,
            Margin = new Thickness(8, 3),
            CornerRadius = new CornerRadius(6),
            Background = CardBrush,
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        card.PointerPressed += OnCardPressed;
        card.PointerMoved += OnCardMoved;
        card.PointerReleased += OnCardReleased;
        if (_thumbs.TryGetValue(sheet.Id, out var bitmap))
            image.Source = bitmap;
        return card;
    }

    private void RestyleLayers()
    {
        var selected = Map.Selected;
        var visual = 0;
        foreach (var child in LayerList.Children)
        {
            if (child is not Border card || card.Tag is not MapFragment sheet)
                continue;

            var isSelected = ReferenceEquals(sheet, selected);
            var isDrop = _dragMoved && visual == _dropVisual;
            var gapTop = _dragMoved && visual == _dropVisual ? 16 : 3;
            var gapBottom = _dragMoved && visual == _dropVisual - 1 ? 16 : 3;
            card.Margin = new Thickness(8, gapTop, 8, gapBottom);
            card.BorderBrush = isSelected || isDrop ? GoldBrush : Brushes.Transparent;
            card.Background = isSelected ? CardSelectedBrush : CardBrush;
            card.BorderThickness = new Thickness(isDrop ? 2 : 1);
            visual++;
        }
    }

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border card || card.Tag is not MapFragment sheet)
            return;
        if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
            return;

        Map.Select(sheet);
        _dragFragment = sheet;
        _dragMoved = false;
        _dropVisual = -1;
        _dragOrigin = e.GetPosition(LayerList);
        e.Pointer.Capture(card);
        e.Handled = true;
    }

    private void OnCardMoved(object? sender, PointerEventArgs e)
    {
        if (_dragFragment is null || sender is not Border card || e.Pointer.Captured != card)
            return;

        var point = e.GetPosition(LayerList);
        var dx = point.X - _dragOrigin.X;
        var dy = point.Y - _dragOrigin.Y;
        if (!_dragMoved && dx * dx + dy * dy < 16)
            return;

        _dragMoved = true;
        _dropVisual = DropVisual(point.Y);
        RestyleLayers();
        e.Handled = true;
    }

    private void OnCardReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragFragment is null)
            return;

        var fragment = _dragFragment;
        var moved = _dragMoved;
        var drop = _dropVisual;
        _dragFragment = null;
        _dragMoved = false;
        _dropVisual = -1;
        e.Pointer.Capture(null);

        if (moved && drop >= 0)
        {
            var count = Map.SheetCount;
            var visual = Math.Clamp(drop, 0, count - 1);
            Map.MoveSheet(fragment, count - 1 - visual);
        }
        else
        {
            RestyleLayers();
        }

        e.Handled = true;
    }

    private int DropVisual(double y)
    {
        var cards = 0;
        foreach (var child in LayerList.Children)
        {
            if (child is not Border card || card.Tag is not MapFragment)
                continue;

            var mid = card.Bounds.Y + card.Bounds.Height * 0.5;
            if (y < mid)
                return cards;

            cards++;
        }

        return Math.Max(0, cards - 1);
    }

    private void PumpThumbs()
    {
        foreach (var sheet in Map.Sheets)
        {
            if (_thumbs.ContainsKey(sheet.Id) || _thumbsLoading.Contains(sheet.Id))
                continue;

            var path = sheet.PreviewPath;
            if (path is null)
                continue;

            _thumbsLoading.Add(sheet.Id);
            _ = LoadThumb(sheet.Id, path, _thumbGeneration);
        }
    }

    private void ResetThumbs()
    {
        _thumbGeneration++;
        _thumbsLoading.Clear();
        foreach (var bitmap in _thumbs.Values)
            bitmap.Dispose();
        _thumbs.Clear();
    }

    private async Task LoadThumb(string id, string path, int generation)
    {
        Bitmap? bitmap = null;
        try
        {
            bitmap = await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, 144);
            });
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _thumbGeneration)
            {
                bitmap?.Dispose();
                return;
            }

            _thumbsLoading.Remove(id);
            if (bitmap is null)
                return;

            _thumbs[id] = bitmap;
            foreach (var child in LayerList.Children)
            {
                if (child is not Border { Tag: MapFragment sheet } card || sheet.Id != id || card.Child is not Grid grid)
                    continue;

                foreach (var item in grid.Children)
                {
                    if (item is Image image)
                        image.Source = bitmap;
                }
            }
        });
    }
}
