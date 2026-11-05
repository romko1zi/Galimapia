using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Galimapia.Canvas;

/// <summary>
/// Infinite canvas. Middle button pans. The wheel zooms toward the cursor.
/// Left button selects and edits a sheet. Drop image files to place scans.
/// </summary>
public class MapCanvas : Control
{
    private enum Gesture
    {
        None,
        Pan,
        Move,
        Scale,
        Rotate,
        CropRect,
        CropPoint,
        Warp,
    }

    public enum MapTool
    {
        None,
        Rect,
        Poly,
        Align,
        Warp,
    }

    private static readonly IBrush CanvasBrush = new SolidColorBrush(Color.Parse("#242220"));
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    private readonly Camera _camera = new();
    private readonly MapScene _scene;
    private Gesture _gesture;
    private Point _panLast;
    private Point? _cursor;
    private bool _acceptingDrop;
    private MapFragment? _selected;
    private (double X, double Y) _pressWorld;
    private double _startX;
    private double _startY;
    private double _startRotation;
    private double _startScale;
    private MapTool _tool;
    private bool _polyOpen;
    private bool _saving;
    private bool _exporting;
    private bool _inSceneChange;
    private int _cropDragIndex = -1;
    private (double X, double Y) _rectAnchor;
    private int _alignCount;
    private int _warpIndex = -1;
    private bool _glue;
    private double _glueX;
    private double _glueY;
    private (double X, double Y) _warpGrab;
    private (double X, double Y) _warpPress;
    private MapFragment? _alignFixed;
    private MapFragment? _alignMoving;
    private (double X, double Y) _alignA;
    private (double X, double Y) _alignB;
    private (double X, double Y) _alignC;

    public MapCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        _scene = MapScene.CreateForApp();
        _scene.Changed += OnSceneChanged;
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, OnDragOver);
        DragDrop.AddDragLeaveHandler(this, OnDragLeave);
        DragDrop.AddDropHandler(this, OnDrop);
    }

    public Camera Camera => _camera;

    public event EventHandler? ViewChanged;

    public string ZoomText => string.Format(Culture, "Zoom {0,8:0.0}%", _camera.Zoom * 100);

    public string CenterText => "Center " + Pair(_camera.CenterX, _camera.CenterY);

    public string CursorText
    {
        get
        {
            var blank = new string(' ', Pair(0, 0).Length);
            if (_cursor is not { } point || Bounds.Width <= 0 || Bounds.Height <= 0)
                return "Cursor " + blank;

            var world = _camera.ScreenToWorld(point.X, point.Y, Bounds.Width, Bounds.Height);
            return "Cursor " + Pair(world.X, world.Y);
        }
    }

    public string SheetsText => _scene.SheetSummary;

    public MapFragment? Selected =>
        _selected is not null && _scene.Contains(_selected) ? _selected : null;

    public int SheetCount => _scene.Fragments.Count;

    public int EditVersion => _scene.EditVersion;

    public int IndexOf(MapFragment fragment) => _scene.IndexOf(fragment);

    public IReadOnlyList<MapFragment> Sheets => _scene.Fragments;

    public int StructureVersion => _scene.StructureVersion;

    public MapTool Tool => _tool;

    public bool Cropping => _tool is MapTool.Rect or MapTool.Poly;

    public bool CanConfirmCrop =>
        Cropping
        && Selected is { } sheet
        && sheet.Draft.Count >= 4
        && CropPolygon.IsSimple(sheet.Draft)
        && MapFragment.TryCropBox(sheet.Draft, out _, out _, out _, out _);

    public string? ProjectFolder => _scene.ProjectFolder;

    public string? Notice => _scene.Notice;

    public async Task<bool> SaveProjectAsync(string folder, string notice = "Saved")
    {
        if (_saving)
            return false;

        _saving = true;
        try
        {
            _scene.SetNotice("Saving");
            var sheets = _scene.SnapshotSheets();
            ExportPage? page = ExportPage.TryFrom(_scene.Fragments, out var frame) ? frame : null;
            var centerX = _camera.CenterX;
            var centerY = _camera.CenterY;
            var zoom = _camera.Zoom;
            var lodRoot = _scene.LodRoot;
            var published = await Task.Run(() => ProjectFile.Write(folder, sheets, centerX, centerY, zoom, page, lodRoot));
            await _scene.RunOnUi(() => _scene.ApplySave(folder, page, published, notice));
            InvalidateVisual();
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            _scene.SetNotice(ex.Message);
            return false;
        }
        finally
        {
            _saving = false;
        }
    }

    public async Task<bool> ExportAsync(
        string path,
        ExportSettings settings,
        IProgress<ExportReport>? progress,
        CancellationToken cancellation)
    {
        if (_exporting || _saving)
            return false;
        if (!ExportPage.TryFrom(_scene.Fragments, out var page))
        {
            _scene.SetNotice("Nothing to export");
            return false;
        }

        var sheets = _scene.Fragments.Select(ExportSheet.From).ToList();
        _exporting = true;
        try
        {
            _scene.SetNotice("Exporting");
            await Task.Run(() => PageExport.Write(path, page, sheets, settings, progress, cancellation), cancellation);
            await _scene.RunOnUi(() => _scene.SetNotice("Exported"));
            return true;
        }
        catch (OperationCanceledException)
        {
            await _scene.RunOnUi(() => _scene.SetNotice("Export cancelled"));
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            await _scene.RunOnUi(() => _scene.SetNotice(ex.Message));
            return false;
        }
        finally
        {
            _exporting = false;
        }
    }

    public async Task<bool> OpenProjectAsync(string folder)
    {
        try
        {
            var document = await Task.Run(() => ProjectFile.Read(folder));
            await _scene.RunOnUi(() => _scene.Install(document));
            _selected = null;
            _tool = MapTool.None;
            _polyOpen = false;
            _alignCount = 0;
            _gesture = Gesture.None;
            Cursor = null;
            _camera.CenterX = document.CenterX;
            _camera.CenterY = document.CenterY;
            _camera.Zoom = Math.Clamp(document.Zoom, Camera.MinZoom, Camera.MaxZoom);
            InvalidateVisual();
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            _scene.SetNotice(ex.Message);
            return false;
        }
    }

    public string ToolHint => CloseReady
        ? "Start point matched. Click it, then press the check."
        : (_tool switch
    {
        MapTool.Rect => "Drag a rectangle. The whole page stays visible until you press the check.",
        MapTool.Poly => _polyOpen
            ? "Click the next corner. Move onto the large start point to close. Lines cannot cross."
            : "Click corners. The page stays whole until you press the check.",
        MapTool.Align => _alignCount switch
        {
            0 => "Click a spot on the sheet that should stay put.",
            1 => "Click a second spot on that same sheet.",
            2 => "Click the same first spot on the sheet that should move.",
            _ => "Click the same second spot. That sheet then turns, scales, and slides into place.",
        },
        MapTool.Warp => "Drag a blue point to stretch that corner. A cropped sheet stretches at each crop point. The sheet itself does not move.",
        _ => "",
    });

    private bool CloseReady
    {
        get
        {
            if (_tool != MapTool.Poly || !_polyOpen || _cursor is not { } cursor || Selected is not { } sheet)
                return false;
            if (sheet.Draft.Count < 4 || Bounds.Width <= 0 || Bounds.Height <= 0 || !CropPolygon.CanClose(sheet.Draft))
                return false;

            var start = sheet.Frame.Screen(_camera, Bounds.Width, Bounds.Height, sheet.Draft[0].X, sheet.Draft[0].Y);
            var dx = start.X - cursor.X;
            var dy = start.Y - cursor.Y;
            return dx * dx + dy * dy <= CropPolygon.CloseSnapDips * CropPolygon.CloseSnapDips;
        }
    }

    public override void Render(DrawingContext context)
    {
        var sheets = _scene.Capture(_camera, Bounds.Width, Bounds.Height, Selected?.Id);
        context.FillRectangle(CanvasBrush, new Rect(Bounds.Size));
        context.Custom(new MapDrawOperation(
            new Rect(Bounds.Size),
            _camera.CenterX,
            _camera.CenterY,
            _camera.Zoom,
            sheets,
            _scene.Fragments.Count,
            _acceptingDrop,
            Cropping,
            _tool == MapTool.Warp,
            _glue,
            _glueX,
            _glueY,
            _cursor is not null && (_tool == MapTool.Align || (_tool == MapTool.Poly && _polyOpen)),
            _cursor?.X ?? 0,
            _cursor?.Y ?? 0,
            AlignMarks(),
            _scene.ExportPage is not null,
            _scene.ExportPage?.X ?? 0,
            _scene.ExportPage?.Y ?? 0,
            _scene.ExportPage?.Size ?? 0,
            _scene.Version));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _scene.Changed -= OnSceneChanged;
        _scene.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var position = e.GetPosition(this);
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsMiddleButtonPressed)
        {
            EndSheetGesture();
            _gesture = Gesture.Pan;
            _panLast = position;
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Handled = true;
            return;
        }

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var world = _camera.ScreenToWorld(position.X, position.Y, Bounds.Width, Bounds.Height);
        if (_tool == MapTool.Align && (point.Properties.IsRightButtonPressed || point.Properties.IsLeftButtonPressed))
        {
            if (point.Properties.IsRightButtonPressed)
                SetTool(MapTool.None);
            else
                HandleAlignPress(world);
            e.Handled = true;
            return;
        }

        if (Cropping && (point.Properties.IsRightButtonPressed || point.Properties.IsLeftButtonPressed))
        {
            HandleCropPress(e, point, position, world);
            return;
        }

        if (_tool == MapTool.Warp && (point.Properties.IsRightButtonPressed || point.Properties.IsLeftButtonPressed))
        {
            HandleWarpPress(e, point, position, world);
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;

        if (Selected is { } selected
            && selected.Frame.TryPickHandle(_camera, Bounds.Width, Bounds.Height, position.X, position.Y, out var part))
        {
            _scene.BeginEdit(selected);
            _pressWorld = world;
            _startRotation = selected.RotationDegrees;
            _startScale = selected.Scale;
            _gesture = part == SheetPart.Rotate ? Gesture.Rotate : Gesture.Scale;
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.Hand);
            e.Handled = true;
            return;
        }

        var hit = _scene.HitTest(world.X, world.Y);
        _selected = hit;
        if (hit is not null)
        {
            _scene.BeginEdit(hit);
            _pressWorld = world;
            _startX = hit.X;
            _startY = hit.Y;
            _gesture = Gesture.Move;
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.SizeAll);
        }

        _scene.Touch();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        _cursor = position;

        if (_gesture == Gesture.Pan)
        {
            var delta = position - _panLast;
            _panLast = position;
            _camera.PanByScreenDelta(delta.X, delta.Y);
            InvalidateVisual();
            e.Handled = true;
        }
        else if (_gesture == Gesture.Move && _selected is not null && Bounds.Width > 0 && Bounds.Height > 0)
        {
            var world = _camera.ScreenToWorld(position.X, position.Y, Bounds.Width, Bounds.Height);
            _scene.MoveTo(_selected, _startX + world.X - _pressWorld.X, _startY + world.Y - _pressWorld.Y);
            e.Handled = true;
        }
        else if (_gesture == Gesture.Scale && _selected is not null && Bounds.Width > 0 && Bounds.Height > 0)
        {
            var world = _camera.ScreenToWorld(position.X, position.Y, Bounds.Width, Bounds.Height);
            var center = _selected.Frame.Center;
            var start = Distance(_pressWorld, center);
            var now = Distance(world, center);
            if (start > 1e-4)
                _scene.SetScale(_selected, _startScale * (now / start));
            e.Handled = true;
        }
        else if (_gesture == Gesture.Rotate && _selected is not null && Bounds.Width > 0 && Bounds.Height > 0)
        {
            var world = _camera.ScreenToWorld(position.X, position.Y, Bounds.Width, Bounds.Height);
            var center = _selected.Frame.Center;
            var start = Math.Atan2(_pressWorld.Y - center.Y, _pressWorld.X - center.X);
            var now = Math.Atan2(world.Y - center.Y, world.X - center.X);
            _scene.SetRotation(_selected, _startRotation + (now - start) * (180d / Math.PI));
            e.Handled = true;
        }
        else if (_gesture == Gesture.CropRect && _selected is not null && Bounds.Width > 0 && Bounds.Height > 0)
        {
            if (_selected.Frame.TryToLocal(World(position).X, World(position).Y, out var localX, out var localY))
                _scene.SetDraft(_selected, CropPolygon.Rectangle(_rectAnchor.X, _rectAnchor.Y, localX, localY), true);
            e.Handled = true;
        }
        else if (_gesture == Gesture.CropPoint && _selected is not null && Bounds.Width > 0 && Bounds.Height > 0)
        {
            if (_selected.Frame.TryToLocal(World(position).X, World(position).Y, out var localX, out var localY))
                PreviewCropVertex(localX, localY);
            e.Handled = true;
        }
        else if (_gesture == Gesture.Warp && _selected is not null && Bounds.Width > 0 && Bounds.Height > 0)
        {
            MoveWarpPoint(_selected, World(position));
            e.Handled = true;
        }
        else if (_tool != MapTool.None)
        {
            InvalidateVisual();
        }

        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton == MouseButton.Middle && _gesture == Gesture.Pan)
        {
            EndPan();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (e.InitialPressMouseButton == MouseButton.Left
            && _gesture is Gesture.Move or Gesture.Scale or Gesture.Rotate)
        {
            EndSheetGesture();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (e.InitialPressMouseButton == MouseButton.Left
            && _gesture is Gesture.CropRect or Gesture.CropPoint)
        {
            EndCropGesture(commit: true);
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (e.InitialPressMouseButton == MouseButton.Left && _gesture == Gesture.Warp)
        {
            EndWarpGesture(commit: true);
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_gesture == Gesture.Pan)
            EndPan();
        else if (_gesture is Gesture.CropRect or Gesture.CropPoint)
            EndCropGesture(commit: true);
        else if (_gesture == Gesture.Warp)
            EndWarpGesture(commit: true);
        else
            EndSheetGesture();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var ticks = Math.Clamp(e.Delta.Y, -8, 8);
        if (Math.Abs(ticks) < 0.01)
            return;

        var position = e.GetPosition(this);
        _cursor = position;
        _camera.ZoomAt(position.X, position.Y, Bounds.Width, Bounds.Height, Math.Pow(1.1, ticks));
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_gesture != Gesture.None)
            return;

        _cursor = null;
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSceneChanged(object? sender, EventArgs e)
    {
        if (_inSceneChange)
            return;

        _inSceneChange = true;
        try
        {
            if (Selected is null && _tool != MapTool.None)
                ClearTool(notify: false);
            InvalidateVisual();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _inSceneChange = false;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var accepts = HasImage(e.DataTransfer);
        e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (_acceptingDrop == accepts)
            return;

        _acceptingDrop = accepts;
        InvalidateVisual();
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (!_acceptingDrop)
            return;

        _acceptingDrop = false;
        InvalidateVisual();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = TakePaths(e.DataTransfer);
        _acceptingDrop = false;
        e.DragEffects = paths.Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (paths.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            InvalidateVisual();
            return;
        }

        EndPan();
        EndSheetGesture();
        var position = e.GetPosition(this);
        var world = _camera.ScreenToWorld(position.X, position.Y, Bounds.Width, Bounds.Height);
        _ = _scene.ImportAsync(paths, world.X, world.Y, bounds =>
        {
            if (bounds is not { } box || Bounds.Width <= 1 || Bounds.Height <= 1)
                return;

            _camera.Fit(box.MinX, box.MinY, box.MaxX, box.MaxY, Bounds.Width, Bounds.Height, 48);
        });
    }

    private static bool HasImage(IDataTransfer transfer)
    {
        var files = transfer.TryGetFiles();
        if (files is null)
            return false;

        foreach (var item in files)
        {
            var path = item.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
                continue;
            if (Directory.Exists(path) || ImageFiles.IsImage(path))
                return true;
        }

        return false;
    }

    private static List<string> TakePaths(IDataTransfer transfer)
    {
        var files = transfer.TryGetFiles();
        var paths = new List<string>();
        if (files is null)
            return paths;

        foreach (var item in files)
        {
            var path = item.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
                continue;

            if (Directory.Exists(path))
            {
                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateFiles(path);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var child in children)
                {
                    if (ImageFiles.IsImage(child))
                        paths.Add(child);
                }
            }
            else if (ImageFiles.IsImage(path))
            {
                paths.Add(path);
            }
        }

        paths.Sort(StringComparer.OrdinalIgnoreCase);
        return paths;
    }

    public bool HandleKey(Key key, KeyModifiers modifiers)
    {
        var control = modifiers.HasFlag(KeyModifiers.Control);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        if (control && key == Key.Z && !shift)
            return UndoKey();

        if (control && ((key == Key.Y && !shift) || (key == Key.Z && shift)))
            return RedoKey();

        if (Cropping && modifiers == KeyModifiers.None && key == Key.Back)
        {
            if (Selected is { } sheet)
            {
                _scene.PopDraftPoint(sheet);
                _polyOpen = _tool == MapTool.Poly && sheet.Draft.Count > 0;
            }

            return true;
        }

        if (Cropping && modifiers == KeyModifiers.None && key is Key.Enter or Key.Return)
        {
            if (CanConfirmCrop)
                ConfirmCrop();
            return true;
        }

        if (modifiers == KeyModifiers.None && key is Key.Delete or Key.Back)
            return DeleteSelected();

        if (modifiers == KeyModifiers.None && key == Key.Escape)
        {
            if (_gesture is Gesture.CropRect or Gesture.CropPoint)
            {
                EndCropGesture(commit: false);
                _polyOpen = false;
                InvalidateVisual();
                ViewChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }

            if (_tool != MapTool.None)
            {
                _scene.CancelDraft();
                _scene.CancelCrop();
                _tool = MapTool.None;
                _polyOpen = false;
                _alignCount = 0;
                Cursor = null;
                InvalidateVisual();
                ViewChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }

            if (_gesture is Gesture.Move or Gesture.Scale or Gesture.Rotate)
            {
                _scene.CancelGesture();
                _gesture = Gesture.None;
                Cursor = null;
                return true;
            }

            if (_gesture == Gesture.Warp)
            {
                EndWarpGesture(commit: false);
                return true;
            }

            if (_selected is null)
                return false;

            _selected = null;
            _scene.Touch();
            return true;
        }

        if (!control && !alt && key is Key.D0 or Key.NumPad0)
        {
            _camera.Zoom = 1;
            InvalidateVisual();
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        if (modifiers == KeyModifiers.None && key == Key.OemOpenBrackets)
            return SendBackward();

        if (modifiers == KeyModifiers.None && key == Key.OemCloseBrackets)
            return BringForward();

        return false;
    }

    public void Select(MapFragment fragment)
    {
        if (!_scene.Contains(fragment))
            return;

        EndActiveGesture();
        _scene.CancelDraft();

        _selected = fragment;
        Reveal(fragment);
        _scene.Touch();
    }

    public bool MoveSheet(MapFragment fragment, int index) => _scene.MoveToIndex(fragment, index);

    public void StepRotation(double delta)
    {
        if (Selected is not { } sheet)
            return;

        CommitRotation(sheet.RotationDegrees + delta);
    }

    public void SetTool(MapTool tool)
    {
        if (Selected is null)
        {
            ClearTool(notify: true);
            return;
        }

        if (_tool == tool)
            tool = MapTool.None;

        if (_gesture == Gesture.Warp)
            EndWarpGesture(commit: false);
        if (_gesture is Gesture.CropRect or Gesture.CropPoint)
            _gesture = Gesture.None;
        _scene.CancelDraft();
        _scene.CancelCrop();

        _tool = tool;
        _polyOpen = false;
        _alignCount = 0;
        Cursor = tool == MapTool.None ? null : new Cursor(StandardCursorType.Cross);
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearTool(bool notify)
    {
        if (_gesture == Gesture.Warp)
            EndWarpGesture(commit: false);
        if (_gesture is Gesture.CropRect or Gesture.CropPoint)
            _gesture = Gesture.None;
        _scene.CancelDraft();
        _scene.CancelCrop();
        _tool = MapTool.None;
        _glue = false;
        _polyOpen = false;
        _alignCount = 0;
        Cursor = null;
        if (!notify)
            return;

        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CommitRotation(double degrees)
    {
        if (Selected is not { } sheet)
            return;

        EndActiveGesture();
        _scene.BeginEdit(sheet);
        _scene.SetRotation(sheet, degrees);
        _scene.EndEdit();
    }

    public void ToggleMirror()
    {
        if (Selected is not { } sheet)
            return;

        EndSheetGesture();
        _scene.BeginEdit(sheet);
        _scene.SetFlipX(sheet, !sheet.FlipX);
        _scene.EndEdit();
    }

    public void ResetAppearance()
    {
        if (Selected is not { } sheet)
            return;

        EndSheetGesture();
        _scene.ResetAppearance(sheet);
    }

    public void StopCrop()
    {
        if (!Cropping)
            return;

        _gesture = Gesture.None;
        _scene.CancelDraft();
        _scene.CancelCrop();

        _tool = MapTool.None;
        _polyOpen = false;
        Cursor = null;
    }

    public void ConfirmCrop()
    {
        if (Selected is not { } sheet || !_scene.ConfirmDraft(sheet))
            return;

        _gesture = Gesture.None;
        _polyOpen = false;
        _tool = MapTool.None;
        _selected = null;
        Cursor = null;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void BeginOpacityEdit()
    {
        if (Selected is not { } sheet)
            return;

        EndSheetGesture();
        _scene.BeginEdit(sheet);
    }

    public void PreviewOpacity(double opacity)
    {
        if (Selected is not { } sheet)
            return;

        _scene.SetOpacity(sheet, (float)opacity);
    }

    public void PreviewBrightness(double brightness)
    {
        if (Selected is not { } sheet)
            return;

        _scene.SetBrightness(sheet, (float)brightness);
    }

    public void PreviewContrast(double contrast)
    {
        if (Selected is not { } sheet)
            return;

        _scene.SetContrast(sheet, (float)contrast);
    }

    public void PreviewHue(double hue)
    {
        if (Selected is not { } sheet)
            return;

        _scene.SetHue(sheet, (float)hue);
    }

    public void EndOpacityEdit() => _scene.EndEdit();

    public bool DeleteSelected()
    {
        if (Selected is not { } sheet)
            return false;

        if (_gesture is Gesture.Move or Gesture.Scale or Gesture.Rotate)
        {
            _gesture = Gesture.None;
            RestoreToolCursor();
            _scene.CancelGesture();
        }
        else if (_gesture is Gesture.CropRect or Gesture.CropPoint)
        {
            _gesture = Gesture.None;
            RestoreToolCursor();
        }
        else if (_gesture == Gesture.Warp)
        {
            EndWarpGesture(commit: false);
        }

        return _scene.Delete(sheet);
    }

    public bool BringForward()
    {
        if (Selected is not { } sheet)
            return false;

        return _scene.BringForward(sheet);
    }

    public bool SendBackward()
    {
        if (Selected is not { } sheet)
            return false;

        return _scene.SendBackward(sheet);
    }

    private bool UndoKey()
    {
        var changed = _scene.Undo();
        if (_gesture is Gesture.Move or Gesture.Scale or Gesture.Rotate or Gesture.CropRect or Gesture.CropPoint or Gesture.Warp)
        {
            _gesture = Gesture.None;
            _polyOpen = false;
            _glue = false;
            RestoreToolCursor();
        }

        if (!changed)
            return false;

        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool RedoKey()
    {
        var changed = _scene.Redo();
        if (_gesture is Gesture.Move or Gesture.Scale or Gesture.Rotate or Gesture.CropRect or Gesture.CropPoint or Gesture.Warp)
        {
            _gesture = Gesture.None;
            _polyOpen = false;
            _glue = false;
            RestoreToolCursor();
        }

        if (!changed)
            return false;

        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void HandleCropPress(PointerPressedEventArgs e, PointerPoint point, Point position, (double X, double Y) world)
    {
        if (point.Properties.IsRightButtonPressed)
        {
            _scene.CancelDraft();
            _polyOpen = false;
            e.Handled = true;
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var hit = _scene.HitTestFrame(world.X, world.Y);
        if (hit is null || !hit.Frame.TryToLocal(world.X, world.Y, out var localX, out var localY))
        {
            e.Handled = true;
            return;
        }

        if (!ReferenceEquals(hit, Selected))
        {
            _scene.CancelDraft();
            _polyOpen = false;
            _selected = hit;
            _scene.Touch();
            e.Handled = true;
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (hit.Draft.Count == 0 && hit.HasCut)
            _scene.CopyCropToDraft(hit);

        var vertex = HitCropVertex(hit, position);
        if (_tool == MapTool.Poly && _polyOpen && hit.Draft.Count >= 4 && vertex == 0 && CropPolygon.CanClose(hit.Draft))
        {
            _polyOpen = false;
            e.Handled = true;
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (vertex >= 0 && hit.Draft.Count >= 4 && !_polyOpen)
        {
            StartCropDrag(hit, vertex);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (_tool == MapTool.Rect)
        {
            _selected = hit;
            _rectAnchor = (localX, localY);
            _scene.SetDraft(hit, [new CropPoint(localX, localY)], false);
            _gesture = Gesture.CropRect;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (_tool == MapTool.Poly && (_polyOpen || vertex < 0))
        {
            if (!_polyOpen && hit.Draft.Count >= 4)
                _scene.SetDraft(hit, [], false);

            var next = new CropPoint(localX, localY);
            if (!CropPolygon.CanAppend(hit.Draft, next))
            {
                e.Handled = true;
                return;
            }

            _selected = hit;
            _polyOpen = true;
            _scene.AddDraftPoint(hit, localX, localY);
            e.Handled = true;
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (vertex >= 0)
        {
            StartCropDrag(hit, vertex);
            e.Pointer.Capture(this);
        }

        e.Handled = true;
    }

    private void HandleWarpPress(PointerPressedEventArgs e, PointerPoint point, Point position, (double X, double Y) world)
    {
        if (point.Properties.IsRightButtonPressed)
        {
            EndWarpGesture(commit: false);
            _tool = MapTool.None;
            Cursor = null;
            e.Handled = true;
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (Selected is { } selected)
        {
            var index = HitWarpHandle(selected, position);
            if (index >= 0)
            {
                var local = selected.WarpedLocal(index);
                var grabbed = selected.Frame.ToWorld(local.X, local.Y);
                _warpIndex = index;
                _warpGrab = grabbed;
                _warpPress = world;
                _scene.BeginWarp(selected);
                _gesture = Gesture.Warp;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        var hit = _scene.HitTest(world.X, world.Y);
        if (hit is not null && !ReferenceEquals(hit, Selected))
        {
            EndWarpGesture(commit: true);
            _selected = hit;
            _scene.Touch();
        }

        e.Handled = true;
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MoveWarpPoint(MapFragment sheet, (double X, double Y) world)
    {
        if (_warpIndex < 0)
            return;

        var targetX = _warpGrab.X + world.X - _warpPress.X;
        var targetY = _warpGrab.Y + world.Y - _warpPress.Y;
        _glue = TryGlue(sheet, world.X, world.Y, out var gluedX, out var gluedY);
        if (_glue && sheet.Frame.TryToLocal(gluedX, gluedY, out var glueLocalX, out var glueLocalY))
        {
            var placed = PlacedPoints(sheet, _warpIndex, glueLocalX, glueLocalY);
            if (!WarpMesh.Collides(placed, _warpIndex, glueLocalX, glueLocalY))
            {
                targetX = gluedX;
                targetY = gluedY;
            }
            else
            {
                _glue = false;
            }
        }

        _glueX = targetX;
        _glueY = targetY;
        if (!double.IsFinite(targetX) || !double.IsFinite(targetY))
            return;
        if (!sheet.Frame.TryToLocal(targetX, targetY, out var localX, out var localY))
            return;

        var control = sheet.ControlPoint(_warpIndex);
        _scene.SetWarpOffset(sheet, _warpIndex, localX - control.X, localY - control.Y);
    }

    private static List<CropPoint> PlacedPoints(MapFragment sheet, int index, double x, double y)
    {
        var placed = new List<CropPoint>(sheet.ControlCount);
        for (var i = 0; i < sheet.ControlCount; i++)
            placed.Add(i == index ? new CropPoint(x, y) : sheet.WarpedLocal(i));
        return placed;
    }

    private bool TryGlue(MapFragment self, double worldX, double worldY, out double gluedX, out double gluedY)
    {
        gluedX = worldX;
        gluedY = worldY;
        var best = WarpMesh.GlueDips * WarpMesh.GlueDips;
        var found = false;
        foreach (var other in _scene.Fragments)
        {
            if (ReferenceEquals(other, self))
                continue;

            for (var i = 0; i < other.ControlCount; i++)
            {
                var local = other.WarpedLocal(i);
                var world = other.Frame.ToWorld(local.X, local.Y);
                var screen = _camera.WorldToScreen(world.X, world.Y, Bounds.Width, Bounds.Height);
                var cursor = _camera.WorldToScreen(worldX, worldY, Bounds.Width, Bounds.Height);
                var dx = screen.X - cursor.X;
                var dy = screen.Y - cursor.Y;
                var distance = dx * dx + dy * dy;
                if (distance > best)
                    continue;

                best = distance;
                gluedX = world.X;
                gluedY = world.Y;
                found = true;
            }
        }

        return found;
    }

    private int HitWarpHandle(MapFragment sheet, Point position)
    {
        var best = -1;
        var bestDistance = WarpMesh.HandleDips * WarpMesh.HandleDips;
        for (var i = 0; i < sheet.ControlCount; i++)
        {
            var local = sheet.WarpedLocal(i);
            var screen = sheet.Frame.Screen(_camera, Bounds.Width, Bounds.Height, local.X, local.Y);
            var dx = screen.X - position.X;
            var dy = screen.Y - position.Y;
            var distance = dx * dx + dy * dy;
            if (distance > bestDistance)
                continue;

            best = i;
            bestDistance = distance;
        }

        return best;
    }

    private void EndWarpGesture(bool commit)
    {
        if (_gesture != Gesture.Warp && _warpIndex < 0 && !_glue)
            return;

        _gesture = Gesture.None;
        _warpIndex = -1;
        _glue = false;
        if (commit)
            _scene.EndWarp();
        else
            _scene.CancelWarp();
        RestoreToolCursor();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleAlignPress((double X, double Y) world)
    {
        var hit = _scene.HitTest(world.X, world.Y);
        if (hit is null || !hit.Frame.TryToLocal(world.X, world.Y, out var localX, out var localY))
            return;

        if (_alignCount == 0)
        {
            _alignFixed = hit;
            _alignA = world;
            _alignCount = 1;
            _selected = hit;
            _scene.Touch();
            return;
        }

        if (_alignCount == 1)
        {
            if (!ReferenceEquals(hit, _alignFixed) || !FarOnScreen(_alignA, world))
                return;

            _alignB = world;
            _alignCount = 2;
            InvalidateVisual();
            return;
        }

        if (_alignCount == 2)
        {
            if (ReferenceEquals(hit, _alignFixed))
                return;

            _alignMoving = hit;
            _alignC = (localX, localY);
            _alignCount = 3;
            _selected = hit;
            _scene.Touch();
            return;
        }

        if (!ReferenceEquals(hit, _alignMoving) || _alignMoving is null)
            return;

        var localDistance = Math.Sqrt(Math.Pow(localX - _alignC.X, 2) + Math.Pow(localY - _alignC.Y, 2));
        if (localDistance < 4)
            return;

        if (!SheetAlign.TryPlace(
                _alignMoving.Frame,
                _alignC.X,
                _alignC.Y,
                localX,
                localY,
                _alignA.X,
                _alignA.Y,
                _alignB.X,
                _alignB.Y,
                MapScene.MinScale,
                MapScene.MaxScale,
                out var x,
                out var y,
                out var rotation,
                out var scale))
            return;

        _scene.AlignTo(_alignMoving, x, y, rotation, scale);
        _tool = MapTool.None;
        _alignCount = 0;
        Cursor = null;
        InvalidateVisual();
    }

    private void StartCropDrag(MapFragment sheet, int index)
    {
        _selected = sheet;
        _cropDragIndex = index;
        if (sheet.DraftIsRectangle && sheet.Draft.Count == 4)
            _rectAnchor = (sheet.Draft[(index + 2) % 4].X, sheet.Draft[(index + 2) % 4].Y);
        _gesture = Gesture.CropPoint;
    }

    private void PreviewCropVertex(double localX, double localY)
    {
        if (_selected is null || _cropDragIndex < 0)
            return;

        if (_selected.DraftIsRectangle && _selected.Draft.Count == 4)
        {
            _scene.SetDraft(_selected, CropPolygon.Rectangle(_rectAnchor.X, _rectAnchor.Y, localX, localY), true);
            return;
        }

        if (_cropDragIndex >= _selected.Draft.Count)
            return;

        var next = _selected.Draft.ToArray();
        next[_cropDragIndex] = new CropPoint(localX, localY);
        if (next.Length >= 4 && !CropPolygon.IsSimple(next))
            return;

        _scene.SetDraft(_selected, next, false);
    }

    private int HitCropVertex(MapFragment sheet, Point position)
    {
        if (sheet.Draft.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
            return -1;

        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < sheet.Draft.Count; i++)
        {
            var screen = sheet.Frame.Screen(_camera, Bounds.Width, Bounds.Height, sheet.Draft[i].X, sheet.Draft[i].Y);
            var dx = screen.X - position.X;
            var dy = screen.Y - position.Y;
            var distance = dx * dx + dy * dy;
            var closing = i == 0
                && _polyOpen
                && sheet.Draft.Count >= 4
                && CropPolygon.CanClose(sheet.Draft);
            var radius = closing ? CropPolygon.CloseSnapDips : i == 0 ? 16d : 11d;
            var limit = radius * radius;
            if (distance <= limit && distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }

    private (double X, double Y)[] AlignMarks() => _alignCount switch
    {
        1 => [_alignA],
        2 => [_alignA, _alignB],
        >= 3 => [_alignA, _alignB, SheetLocalToWorld()],
        _ => [],
    };

    private (double X, double Y) SheetLocalToWorld()
    {
        if (_alignMoving is null)
            return _alignC;

        return _alignMoving.Frame.ToWorld(_alignC.X, _alignC.Y);
    }

    private (double X, double Y) World(Point position) =>
        _camera.ScreenToWorld(position.X, position.Y, Bounds.Width, Bounds.Height);

    private void Reveal(MapFragment fragment)
    {
        if (Bounds.Width <= 1 || Bounds.Height <= 1)
            return;

        fragment.GetVisibleBounds(out var minX, out var minY, out var maxX, out var maxY);
        var topLeft = _camera.ScreenToWorld(0, 0, Bounds.Width, Bounds.Height);
        var bottomRight = _camera.ScreenToWorld(Bounds.Width, Bounds.Height, Bounds.Width, Bounds.Height);
        var visible = minX < bottomRight.X && maxX > topLeft.X && minY < bottomRight.Y && maxY > topLeft.Y;
        if (visible)
            return;

        _camera.CenterX = (minX + maxX) * 0.5;
        _camera.CenterY = (minY + maxY) * 0.5;
        InvalidateVisual();
    }

    private void EndPan()
    {
        if (_gesture != Gesture.Pan)
            return;

        _gesture = Gesture.None;
        RestoreToolCursor();
    }

    private void EndSheetGesture()
    {
        if (_gesture is not (Gesture.Move or Gesture.Scale or Gesture.Rotate))
            return;

        _gesture = Gesture.None;
        _scene.EndEdit();
        RestoreToolCursor();
    }

    private void EndCropGesture(bool commit)
    {
        if (_gesture is not (Gesture.CropRect or Gesture.CropPoint))
            return;

        _gesture = Gesture.None;
        if (!commit)
        {
            _scene.CancelDraft();
            _polyOpen = false;
        }

        RestoreToolCursor();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EndActiveGesture()
    {
        if (_gesture is Gesture.CropRect or Gesture.CropPoint)
            EndCropGesture(commit: true);
        else if (_gesture == Gesture.Warp)
            EndWarpGesture(commit: true);
        else
            EndSheetGesture();
    }

    private void RestoreToolCursor() =>
        Cursor = _tool == MapTool.None ? null : new Cursor(StandardCursorType.Cross);

    private bool FarOnScreen((double X, double Y) a, (double X, double Y) b)
    {
        var left = _camera.WorldToScreen(a.X, a.Y, Bounds.Width, Bounds.Height);
        var right = _camera.WorldToScreen(b.X, b.Y, Bounds.Width, Bounds.Height);
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return dx * dx + dy * dy >= 64;
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static string Pair(double x, double y) =>
        string.Format(Culture, "{0,12:0.0}, {1,12:0.0}", x, y);
}
