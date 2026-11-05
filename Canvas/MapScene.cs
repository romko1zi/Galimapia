using SkiaSharp;

namespace Galimapia.Canvas;

/// <summary>
/// Sheets on the infinite canvas. Mutate it on the UI thread only.
/// Background work posts back through the invoker passed to the constructor.
/// </summary>
public sealed class MapScene : IDisposable
{
    public const double Gap = 48;

    private readonly LodStore _store;
    private LodStore? _projectStore;
    private readonly ImageCache _cache;
    private readonly Func<Action, Task> _runOnUi;
    private readonly List<MapFragment> _fragments = [];
    private readonly HashSet<string> _inFlight = [];
    private readonly HashSet<string> _failed = [];
    private readonly SemaphoreSlim _pyramids = new(2, 2);
    private readonly SemaphoreSlim _decodes = new(4, 4);
    private readonly CancellationTokenSource _cts = new();

    private Camera? _camera;
    private double _viewWidth;
    private double _viewHeight;
    private bool _reading;
    private int _skipped;
    private string? _error;
    private string? _notice;
    private bool _disposed;
    private readonly List<ISheetEdit> _undo = [];
    private readonly List<ISheetEdit> _redo = [];
    private string? _gestureId;
    private SheetPose? _gestureBefore;
    private string? _cropId;
    private CropPoint[]? _cropBefore;
    private CropPoint[]? _cropBeforeWarp;
    private bool _cropBeforeRect;
    private bool _cropBeforeCommitted;
    private string? _warpId;
    private CropPoint[]? _warpBefore;

    public const double MinScale = 0.05;
    public const double MaxScale = 8;

    public MapScene(LodStore store, ImageCache cache, Func<Action, Task> runOnUi)
    {
        _store = store;
        _cache = cache;
        _runOnUi = runOnUi;
    }

    public static MapScene CreateForApp() =>
        new(LodStore.Shared, new ImageCache(ImageCache.DefaultBudget), AvaloniaUi.RunAsync);

    public IReadOnlyList<MapFragment> Fragments => _fragments;

    public string? ProjectFolder { get; private set; }

    public ExportPage? ExportPage { get; private set; }

    public string LodRoot => _store.Root;

    public int Version { get; private set; }

    public int EditVersion { get; private set; }

    public int StructureVersion { get; private set; }

    public int CachedCount => _cache.Count;

    public event EventHandler? Changed;

    public Task RunOnUi(Action action) => _runOnUi(action);

    public string SheetSummary
    {
        get
        {
            if (_error is not null && _fragments.Count == 0)
                return _error;

            if (_reading)
                return "Reading images";

            if (_fragments.Count == 0)
                return "No sheets";

            var text = _fragments.Count == 1 ? "1 sheet" : $"{_fragments.Count} sheets";
            var preparing = 0;
            var failed = 0;
            foreach (var fragment in _fragments)
            {
                if (fragment.Failed)
                    failed++;
                else if (!fragment.PyramidReady)
                    preparing++;
            }

            if (preparing > 0)
                text += $", preparing {preparing}";
            if (failed > 0)
                text += $", {failed} failed";
            if (_skipped > 0)
                text += $", {_skipped} skipped";

            text += " · cache " + FormatBytes(_cache.LiveBytes);
            if (_notice is not null)
                text += " · " + _notice;
            return text;
        }
    }

    public async Task ImportAsync(
        IReadOnlyList<string> paths,
        double worldX,
        double worldY,
        Action<(double MinX, double MinY, double MaxX, double MaxY)?>? onPlaced = null)
    {
        if (paths.Count == 0 || _disposed)
            return;

        await RunOnUi(() =>
        {
            _reading = true;
            _error = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }).ConfigureAwait(false);

        List<Header> headers;
        try
        {
            headers = await Task.Run(() => ReadHeaders(paths), _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            await RunOnUi(() =>
            {
                _reading = false;
                _error = "Import failed";
                Changed?.Invoke(this, EventArgs.Empty);
            }).ConfigureAwait(false);
            return;
        }

        await RunOnUi(() =>
        {
            if (_disposed)
                return;

            _reading = false;
            var previous = _fragments.Count;
            var added = new List<MapFragment>(headers.Count);
            var columns = SheetGrid.Columns(headers.Count);
            double cellWidth = 0;
            double cellHeight = 0;
            foreach (var header in headers)
            {
                cellWidth = Math.Max(cellWidth, header.Width);
                cellHeight = Math.Max(cellHeight, header.Height);
            }

            for (var i = 0; i < headers.Count; i++)
            {
                var header = headers[i];
                var (x, y) = SheetGrid.Cell(i, columns, worldX, worldY, cellWidth, cellHeight, Gap);
                var fragment = new MapFragment(header.Path, x, y, header.Width, header.Height);
                _fragments.Add(fragment);
                added.Add(fragment);
            }

            _skipped += paths.Count - headers.Count;
            if (added.Count == 0)
                _error = "No images";

            (double MinX, double MinY, double MaxX, double MaxY)? frame = null;
            if (added.Count > 0 && (previous == 0 || added.Count >= 2) && TryBounds(added, out var minX, out var minY, out var maxX, out var maxY))
                frame = (minX, minY, maxX, maxY);

            onPlaced?.Invoke(frame);

            Version++;
            EditVersion++;
            StructureVersion++;
            Changed?.Invoke(this, EventArgs.Empty);
            foreach (var fragment in added)
                _ = BuildPyramidAsync(fragment);
        }).ConfigureAwait(false);
    }

    internal List<SheetVisual> Capture(Camera camera, double viewWidth, double viewHeight, string? selectedId)
    {
        _camera = camera;
        _viewWidth = viewWidth;
        _viewHeight = viewHeight;

        var visuals = new List<SheetVisual>();
        if (viewWidth <= 1 || viewHeight <= 1)
            return visuals;

        var (left, top, right, bottom) = ViewWorld(camera, viewWidth, viewHeight, 80);
        foreach (var fragment in _fragments)
        {
            if (!Intersects(fragment, left, top, right, bottom))
                continue;

            ImageLease? lease = null;
            if (fragment.PyramidReady && fragment.Levels.Count > 0 && !fragment.Failed)
            {
                var needed = NeededPixels(fragment, camera);
                var level = LodPlan.Choose(fragment.Levels, needed, fragment.HeldLongSide);
                fragment.HeldLongSide = level.LongSide;
                lease = BestAvailable(fragment, level.LongSide);
                Request(fragment, level);
            }

            var pose = fragment.Frame;
            visuals.Add(new SheetVisual(
                lease,
                fragment.X,
                fragment.Y,
                fragment.PixelWidth,
                fragment.PixelHeight,
                fragment.Name,
                fragment.Id == selectedId,
                fragment.RotationDegrees,
                fragment.Scale,
                fragment.FlipX,
                fragment.FlipY,
                fragment.Opacity,
                fragment.Crop.ToArray(),
                fragment.CropCommitted,
                fragment.Draft.ToArray(),
                pose.PivotX,
                pose.PivotY,
                pose.BoxLeft,
                pose.BoxTop,
                pose.BoxRight,
                pose.BoxBottom,
                fragment.Brightness,
                fragment.Contrast,
                fragment.Hue,
                fragment.Warp.ToArray()));
        }

        return visuals;
    }

    public void PrepareView(Camera camera, double viewWidth, double viewHeight)
    {
        _camera = camera;
        _viewWidth = viewWidth;
        _viewHeight = viewHeight;
        FillQueue();
    }

    public bool TryGetBounds(out double minX, out double minY, out double maxX, out double maxY) =>
        TryBounds(_fragments, out minX, out minY, out maxX, out maxY);

    public int IndexOf(MapFragment fragment) => _fragments.IndexOf(fragment);

    public bool Contains(MapFragment fragment) => _fragments.Contains(fragment);

    public MapFragment? HitTest(double worldX, double worldY)
    {
        for (var i = _fragments.Count - 1; i >= 0; i--)
        {
            if (_fragments[i].ContainsWorld(worldX, worldY))
                return _fragments[i];
        }

        return null;
    }

    public MapFragment? HitTestFrame(double worldX, double worldY)
    {
        for (var i = _fragments.Count - 1; i >= 0; i--)
        {
            if (_fragments[i].ContainsFrame(worldX, worldY))
                return _fragments[i];
        }

        return null;
    }

    public void BeginEdit(MapFragment fragment)
    {
        if (!_fragments.Contains(fragment))
            return;

        if (_gestureId == fragment.Id)
            return;

        if (_gestureId is not null)
            EndEdit();

        _gestureId = fragment.Id;
        _gestureBefore = SheetPose.Capture(fragment);
    }

    public void EndEdit()
    {
        if (_gestureId is null || _gestureBefore is not { } before)
        {
            _gestureId = null;
            _gestureBefore = null;
            return;
        }

        var id = _gestureId;
        _gestureId = null;
        _gestureBefore = null;
        var fragment = Find(id);
        if (fragment is null)
            return;

        var after = SheetPose.Capture(fragment);
        if (after == before)
            return;

        Push(new PoseEdit(id, before, after));
    }

    public void CancelGesture()
    {
        if (_gestureId is null || _gestureBefore is not { } before)
        {
            _gestureId = null;
            _gestureBefore = null;
            return;
        }

        var id = _gestureId;
        _gestureId = null;
        _gestureBefore = null;
        ApplyPose(id, before);
        Touch();
    }

    public void MoveTo(MapFragment fragment, double x, double y)
    {
        if (!_fragments.Contains(fragment))
            return;

        fragment.X = x;
        fragment.Y = y;
        Touch();
    }

    public void SetRotation(MapFragment fragment, double degrees)
    {
        if (!_fragments.Contains(fragment))
            return;

        fragment.RotationDegrees = degrees;
        Touch();
    }

    public void SetScale(MapFragment fragment, double scale)
    {
        if (!_fragments.Contains(fragment))
            return;

        fragment.Scale = Math.Clamp(scale, MinScale, MaxScale);
        Touch();
    }

    public void SetOpacity(MapFragment fragment, float opacity)
    {
        if (!_fragments.Contains(fragment))
            return;

        fragment.Opacity = Math.Clamp(opacity, 0f, 1f);
        Touch();
    }

    public void SetBrightness(MapFragment fragment, float brightness)
    {
        if (!_fragments.Contains(fragment))
            return;

        fragment.Brightness = Math.Clamp(brightness, -1f, 1f);
        Touch();
    }

    public void SetContrast(MapFragment fragment, float contrast)
    {
        if (!_fragments.Contains(fragment))
            return;

        fragment.Contrast = Math.Clamp(contrast, 0f, 2f);
        Touch();
    }

    public void SetHue(MapFragment fragment, float hue)
    {
        if (!_fragments.Contains(fragment))
            return;

        fragment.Hue = Math.Clamp(hue, -180f, 180f);
        Touch();
    }

    public void BeginWarp(MapFragment fragment)
    {
        if (!_fragments.Contains(fragment))
            return;

        fragment.EnsureWarp();
        _warpId = fragment.Id;
        _warpBefore = fragment.Warp.ToArray();
    }

    public void SetWarpOffset(MapFragment fragment, int index, double x, double y)
    {
        if (!_fragments.Contains(fragment))
            return;

        fragment.EnsureWarp();
        if (index < 0 || index >= fragment.Warp.Count)
            return;

        fragment.Warp[index] = new CropPoint(x, y);
        Touch();
    }

    public void EndWarp()
    {
        if (_warpId is null || _warpBefore is not { } before)
        {
            _warpId = null;
            _warpBefore = null;
            return;
        }

        var id = _warpId;
        _warpId = null;
        _warpBefore = null;
        var fragment = Find(id);
        if (fragment is null || CropPolygon.Same(before, fragment.Warp))
            return;

        Push(new WarpEdit(id, before, fragment.Warp.ToArray()));
    }

    public void CancelWarp()
    {
        if (_warpId is null || _warpBefore is not { } before)
        {
            _warpId = null;
            _warpBefore = null;
            return;
        }

        var id = _warpId;
        _warpId = null;
        _warpBefore = null;
        ReplaceWarp(id, before);
        Touch();
    }

    public void SetFlipX(MapFragment fragment, bool flip)
    {
        if (!_fragments.Contains(fragment))
            return;

        fragment.FlipX = flip;
        Touch();
    }

    public void SetFlipY(MapFragment fragment, bool flip)
    {
        if (!_fragments.Contains(fragment))
            return;

        fragment.FlipY = flip;
        Touch();
    }

    public void ResetAppearance(MapFragment fragment)
    {
        if (!_fragments.Contains(fragment))
            return;

        BeginEdit(fragment);
        SetRotation(fragment, 0);
        SetScale(fragment, 1);
        SetFlipX(fragment, false);
        SetFlipY(fragment, false);
        EndEdit();
    }

    public void BeginCrop(MapFragment fragment)
    {
        if (!_fragments.Contains(fragment))
            return;
        if (_cropId == fragment.Id)
            return;
        if (_cropId is not null)
            EndCrop();

        _cropId = fragment.Id;
        _cropBefore = fragment.Crop.ToArray();
        _cropBeforeWarp = fragment.Warp.ToArray();
        _cropBeforeRect = fragment.CropIsRectangle;
        _cropBeforeCommitted = fragment.CropCommitted;
    }

    public void CopyCropToDraft(MapFragment fragment)
    {
        if (!_fragments.Contains(fragment) || fragment.Draft.Count > 0)
            return;

        fragment.Draft.AddRange(fragment.Crop);
        fragment.DraftIsRectangle = fragment.CropIsRectangle;
        Touch();
    }

    public void SetDraft(MapFragment fragment, IReadOnlyList<CropPoint> points, bool rectangle)
    {
        if (!_fragments.Contains(fragment))
            return;

        fragment.Draft.Clear();
        fragment.Draft.AddRange(points);
        fragment.DraftIsRectangle = rectangle;
        Touch();
    }

    public void AddDraftPoint(MapFragment fragment, double x, double y)
    {
        if (!_fragments.Contains(fragment))
            return;

        fragment.Draft.Add(new CropPoint(x, y));
        fragment.DraftIsRectangle = false;
        Touch();
    }

    public void PopDraftPoint(MapFragment fragment)
    {
        if (!_fragments.Contains(fragment) || fragment.Draft.Count == 0)
            return;

        fragment.Draft.RemoveAt(fragment.Draft.Count - 1);
        fragment.DraftIsRectangle = false;
        Touch();
    }

    public bool CancelDraft()
    {
        var changed = false;
        foreach (var fragment in _fragments)
        {
            if (fragment.Draft.Count == 0)
                continue;

            fragment.Draft.Clear();
            fragment.DraftIsRectangle = false;
            changed = true;
        }

        if (changed)
            Touch();
        return changed;
    }

    public bool ConfirmDraft(MapFragment fragment)
    {
        if (!_fragments.Contains(fragment) || fragment.Draft.Count < 4 || !CropPolygon.IsSimple(fragment.Draft))
            return false;
        if (!MapFragment.TryCropBox(fragment.Draft, out _, out _, out _, out _))
            return false;

        var before = fragment.Crop.ToArray();
        var beforeRect = fragment.CropIsRectangle;
        var beforeCommitted = fragment.CropCommitted;
        var beforeX = fragment.X;
        var beforeY = fragment.Y;
        var beforeWarp = fragment.Warp.ToArray();
        var oldPivotX = fragment.Frame.PivotLocalX;
        var oldPivotY = fragment.Frame.PivotLocalY;
        fragment.Crop.Clear();
        fragment.Crop.AddRange(fragment.Draft);
        fragment.CropIsRectangle = fragment.DraftIsRectangle;
        fragment.CropCommitted = true;
        fragment.Draft.Clear();
        fragment.DraftIsRectangle = false;
        MovePivot(fragment, oldPivotX, oldPivotY);

        if (CropPolygon.Same(before, fragment.Crop) && beforeRect == fragment.CropIsRectangle && beforeCommitted)
        {
            fragment.X = beforeX;
            fragment.Y = beforeY;
            Touch();
            return true;
        }

        fragment.Warp.Clear();
        Push(new CropEdit(
            fragment.Id,
            before,
            beforeRect,
            beforeCommitted,
            beforeX,
            beforeY,
            fragment.Crop.ToArray(),
            fragment.CropIsRectangle,
            true,
            fragment.X,
            fragment.Y,
            beforeWarp,
            []));
        Touch();
        return true;
    }

    private static (double X, double Y) PivotOf(IReadOnlyList<CropPoint> points, bool committed, int width, int height)
    {
        if (committed && points.Count >= 4 && MapFragment.TryCropBox(points, out var left, out var top, out var right, out var bottom))
            return ((left + right) * 0.5, (top + bottom) * 0.5);

        return (width * 0.5, height * 0.5);
    }

    private static void MovePivot(MapFragment fragment, double oldPivotX, double oldPivotY)
    {
        var newPivotX = fragment.Frame.PivotLocalX;
        var newPivotY = fragment.Frame.PivotLocalY;
        var scale = fragment.Scale;
        var sx = (newPivotX - oldPivotX) * (fragment.FlipX ? -scale : scale);
        var sy = (newPivotY - oldPivotY) * (fragment.FlipY ? -scale : scale);
        var radians = fragment.RotationDegrees * (Math.PI / 180d);
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        fragment.X += oldPivotX + (sx * cos - sy * sin) - newPivotX;
        fragment.Y += oldPivotY + (sx * sin + sy * cos) - newPivotY;
    }

    public void AddCropPoint(MapFragment fragment, double x, double y)
    {
        if (!_fragments.Contains(fragment))
            return;

        BeginCrop(fragment);
        fragment.Crop.Add(new CropPoint(x, y));
        fragment.CropIsRectangle = false;
        Touch();
    }

    public void PreviewCrop(MapFragment fragment, IReadOnlyList<CropPoint> points, bool rectangle)
    {
        if (!_fragments.Contains(fragment))
            return;

        BeginCrop(fragment);
        fragment.Crop.Clear();
        fragment.Crop.AddRange(points);
        fragment.CropIsRectangle = rectangle;
        Touch();
    }

    public void PopCropPoint(MapFragment fragment)
    {
        if (!_fragments.Contains(fragment) || fragment.Crop.Count == 0)
            return;

        BeginCrop(fragment);
        fragment.Crop.RemoveAt(fragment.Crop.Count - 1);
        fragment.CropIsRectangle = false;
        Touch();
    }

    public void EndCrop()
    {
        if (_cropId is null || _cropBefore is not { } before)
        {
            _cropId = null;
            _cropBefore = null;
            return;
        }

        var id = _cropId;
        var beforeRect = _cropBeforeRect;
        var beforeCommitted = _cropBeforeCommitted;
        var beforeWarp = _cropBeforeWarp ?? [];
        _cropId = null;
        _cropBefore = null;
        _cropBeforeWarp = null;
        var fragment = Find(id);
        if (fragment is null)
            return;

        var beforeX = fragment.X;
        var beforeY = fragment.Y;
        var oldPivot = PivotOf(before, beforeCommitted, fragment.PixelWidth, fragment.PixelHeight);
        var restored = false;
        if (fragment.Crop.Count is > 0 and < 3)
        {
            fragment.Crop.Clear();
            fragment.Crop.AddRange(before);
            fragment.CropIsRectangle = beforeRect;
            fragment.CropCommitted = beforeCommitted;
            restored = true;
        }
        else if (fragment.Crop.Count >= 4)
        {
            fragment.CropCommitted = true;
        }

        if (!restored)
            MovePivot(fragment, oldPivot.X, oldPivot.Y);

        if (CropPolygon.Same(before, fragment.Crop)
            && fragment.CropIsRectangle == beforeRect
            && fragment.CropCommitted == beforeCommitted)
        {
            fragment.X = beforeX;
            fragment.Y = beforeY;
            if (restored)
                Touch();
            return;
        }

        if (!CropPolygon.Same(before, fragment.Crop))
            fragment.Warp.Clear();

        Push(new CropEdit(
            id,
            before,
            beforeRect,
            beforeCommitted,
            beforeX,
            beforeY,
            fragment.Crop.ToArray(),
            fragment.CropIsRectangle,
            fragment.CropCommitted,
            fragment.X,
            fragment.Y,
            beforeWarp,
            fragment.Warp.ToArray()));
        Touch();
    }

    public void CancelCrop()
    {
        if (_cropId is null || _cropBefore is not { } before)
        {
            _cropId = null;
            _cropBefore = null;
            return;
        }

        var id = _cropId;
        var beforeRect = _cropBeforeRect;
        var beforeCommitted = _cropBeforeCommitted;
        var beforeWarp = _cropBeforeWarp ?? [];
        _cropId = null;
        _cropBefore = null;
        _cropBeforeWarp = null;
        var fragment = Find(id);
        ReplaceCrop(id, before, beforeRect, beforeCommitted, fragment?.X ?? 0, fragment?.Y ?? 0, beforeWarp);
        Touch();
    }

    public void AlignTo(MapFragment fragment, double x, double y, double rotation, double scale)
    {
        if (!_fragments.Contains(fragment))
            return;

        BeginEdit(fragment);
        fragment.X = x;
        fragment.Y = y;
        fragment.RotationDegrees = rotation;
        fragment.Scale = Math.Clamp(scale, MinScale, MaxScale);
        Touch();
        EndEdit();
    }

    public bool BringForward(MapFragment fragment)
    {
        var index = _fragments.IndexOf(fragment);
        if (index < 0 || index >= _fragments.Count - 1)
            return false;

        MoveItem(index, index + 1);
        Push(new OrderEdit(index, index + 1));
        Touch();
        return true;
    }

    public bool MoveToIndex(MapFragment fragment, int index)
    {
        var from = _fragments.IndexOf(fragment);
        if (from < 0)
            return false;

        var to = Math.Clamp(index, 0, _fragments.Count - 1);
        if (from == to)
            return false;

        MoveItem(from, to);
        Push(new OrderEdit(from, to));
        Touch();
        return true;
    }

    public bool SendBackward(MapFragment fragment)
    {
        var index = _fragments.IndexOf(fragment);
        if (index <= 0)
            return false;

        MoveItem(index, index - 1);
        Push(new OrderEdit(index, index - 1));
        Touch();
        return true;
    }

    public bool Delete(MapFragment fragment)
    {
        var index = _fragments.IndexOf(fragment);
        if (index < 0)
            return false;

        if (_gestureId == fragment.Id)
        {
            _gestureId = null;
            _gestureBefore = null;
        }
        else if (_gestureId is not null)
        {
            EndEdit();
        }

        if (_cropId == fragment.Id)
            CancelCrop();
        fragment.Draft.Clear();

        _fragments.RemoveAt(index);
        StructureVersion++;
        Push(new DeleteEdit(fragment, index));
        Touch();
        return true;
    }

    public bool Undo()
    {
        if (_warpId is not null)
        {
            CancelWarp();
            return true;
        }

        if (_gestureId is not null)
        {
            CancelGesture();
            return true;
        }

        if (CancelDraft())
            return true;

        if (_cropId is not null)
        {
            CancelCrop();
            return true;
        }

        if (_undo.Count == 0)
            return false;

        var edit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        edit.Undo(this);
        _redo.Add(edit);
        Touch();
        return true;
    }

    public bool Redo()
    {
        if (_warpId is not null)
        {
            CancelWarp();
            return true;
        }

        if (_gestureId is not null)
        {
            CancelGesture();
            return true;
        }

        if (CancelDraft())
            return true;

        if (_cropId is not null)
        {
            CancelCrop();
            return true;
        }

        if (_redo.Count == 0)
            return false;

        var edit = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        edit.Redo(this);
        _undo.Add(edit);
        Touch();
        return true;
    }

    public void Touch()
    {
        Version++;
        EditVersion++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void ApplyPose(string id, SheetPose pose)
    {
        var fragment = Find(id);
        if (fragment is not null)
            pose.Apply(fragment);
    }

    internal void ReplaceCrop(
        string id,
        IReadOnlyList<CropPoint> points,
        bool rectangle,
        bool committed,
        double x,
        double y,
        IReadOnlyList<CropPoint> warp)
    {
        var fragment = Find(id);
        if (fragment is null)
            return;

        fragment.Crop.Clear();
        fragment.Crop.AddRange(points);
        fragment.CropIsRectangle = rectangle;
        fragment.CropCommitted = committed;
        fragment.X = x;
        fragment.Y = y;
        fragment.Draft.Clear();
        fragment.SetWarp(warp);
    }

    internal void ReplaceWarp(string id, IReadOnlyList<CropPoint> warp)
    {
        var fragment = Find(id);
        if (fragment is null)
            return;

        fragment.SetWarp(warp);
    }

    internal void Insert(int index, MapFragment fragment)
    {
        _fragments.Insert(Math.Clamp(index, 0, _fragments.Count), fragment);
        StructureVersion++;
    }

    internal void Remove(MapFragment fragment)
    {
        if (_fragments.Remove(fragment))
            StructureVersion++;
    }

    internal void MoveItem(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= _fragments.Count || to >= _fragments.Count)
            return;

        var item = _fragments[from];
        _fragments.RemoveAt(from);
        _fragments.Insert(to, item);
        StructureVersion++;
    }

    public bool IsCached(string fragmentId, int longSide) =>
        _cache.Contains(fragmentId + ":" + longSide);

    public long CacheBytes => _cache.LiveBytes;

    public int? CachedPixelWidth(string fragmentId, int longSide)
    {
        var lease = _cache.TryLease(fragmentId + ":" + longSide);
        if (lease is null)
            return null;

        var width = lease.Image.Width;
        lease.Dispose();
        return width;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Cancel();
        _cache.Dispose();
    }

    public string? Notice => _notice;

    public void SetNotice(string? notice)
    {
        _notice = notice;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<SheetSnapshot> SnapshotSheets() => _fragments.Select(SheetSnapshot.From).ToList();

    public void ApplySave(string folder, ExportPage? page, IReadOnlyList<PublishedSheet> published, string notice = "Saved")
    {
        ProjectFolder = Path.GetFullPath(folder);
        _projectStore = new LodStore(Path.Combine(ProjectFolder, "cache_lod"));
        ExportPage = page;
        foreach (var item in published)
        {
            var fragment = Find(item.Id);
            if (fragment is null)
                continue;

            fragment.Retarget(item.SourcePath);
            fragment.SetLevels(item.Pyramid.Levels);
            fragment.PyramidReady = true;
            fragment.Failed = false;
        }

        _notice = notice;
        _failed.Clear();
        Version++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Install(ProjectDocument document)
    {
        _gestureId = null;
        _gestureBefore = null;
        _cropId = null;
        _cropBefore = null;
        _cropBeforeWarp = null;
        _warpId = null;
        _warpBefore = null;
        _undo.Clear();
        _redo.Clear();
        _failed.Clear();
        CancelDraft();
        _fragments.Clear();
        ProjectFolder = document.Folder;
        _projectStore = new LodStore(Path.Combine(document.Folder, "cache_lod"));
        ExportPage = document.Page;
        foreach (var loaded in document.Sheets)
        {
            var sheet = loaded.Sheet;
            var fragment = new MapFragment(
                sheet.SourcePath,
                sheet.X,
                sheet.Y,
                sheet.PixelWidth,
                sheet.PixelHeight,
                sheet.Id,
                sheet.Name)
            {
                RotationDegrees = sheet.Rotation,
                Scale = sheet.Scale,
                FlipX = sheet.FlipX,
                FlipY = sheet.FlipY,
                Opacity = sheet.Opacity,
                Brightness = sheet.Brightness,
                Contrast = sheet.Contrast,
                Hue = sheet.Hue,
                CropIsRectangle = sheet.CropRectangle,
                CropCommitted = sheet.CropCommitted,
            };
            fragment.Crop.AddRange(sheet.Crop);
            fragment.SetWarp(sheet.Warp);
            if (loaded.Pyramid is { } pyramid)
            {
                fragment.SetLevels(pyramid.Levels);
                fragment.PyramidReady = true;
            }

            _fragments.Add(fragment);
            if (!fragment.PyramidReady)
                _ = BuildPyramidAsync(fragment);
        }

        _notice = "Opened";
        _error = null;
        _reading = false;
        Version++;
        StructureVersion++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task BuildPyramidAsync(MapFragment fragment)
    {
        if (_projectStore is { } project
            && project.TryLoadFragment(fragment.Id, fragment.SourcePath, fragment.PixelWidth, fragment.PixelHeight, out var cached)
            && cached is not null)
        {
            await ApplyPyramid(fragment, cached, failed: false).ConfigureAwait(false);
            return;
        }

        PreparedImage? prepared = null;
        var failed = false;
        var store = _store;
        try
        {
            await _pyramids.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                prepared = await Task.Run(() => store.Prepare(fragment.SourcePath), _cts.Token).ConfigureAwait(false);
            }
            finally
            {
                try { _pyramids.Release(); }
                catch (ObjectDisposedException) { }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (Exception)
        {
            failed = true;
        }

        try
        {
            await ApplyPyramid(fragment, prepared, failed).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task ApplyPyramid(MapFragment fragment, PreparedImage? prepared, bool failed) =>
        RunOnUi(() =>
        {
            if (_disposed || !_fragments.Contains(fragment))
                return;

            if (failed || prepared is null)
                fragment.Failed = true;
            else
            {
                fragment.SetLevels(prepared.Levels);
                fragment.PyramidReady = true;
            }

            Version++;
            FillQueue();
            Changed?.Invoke(this, EventArgs.Empty);
        });

    private async Task LoadAsync(MapFragment fragment, LodFile level, string key)
    {
        SKImage? image = null;
        var failed = false;
        try
        {
            await _decodes.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                image = await Task.Run(() => ImageFiles.Decode(level.Path), _cts.Token).ConfigureAwait(false);
            }
            finally
            {
                try { _decodes.Release(); }
                catch (ObjectDisposedException) { }
            }
        }
        catch (OperationCanceledException)
        {
            image?.Dispose();
            return;
        }
        catch (ObjectDisposedException)
        {
            image?.Dispose();
            return;
        }
        catch (Exception)
        {
            image?.Dispose();
            image = null;
            failed = true;
        }

        var decoded = image;
        try
        {
            await RunOnUi(() =>
            {
                _inFlight.Remove(key);
                if (_disposed || failed || decoded is null)
                {
                    decoded?.Dispose();
                    if (!_disposed)
                        _failed.Add(key);
                    return;
                }

                var bytes = (long)decoded.Width * decoded.Height * 4;
                _cache.Add(key, decoded, bytes);
                Version++;
                FillQueue();
                Changed?.Invoke(this, EventArgs.Empty);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            decoded?.Dispose();
        }
    }

    private void FillQueue()
    {
        if (_camera is null || _viewWidth <= 1 || _viewHeight <= 1)
            return;

        var (left, top, right, bottom) = ViewWorld(_camera, _viewWidth, _viewHeight, 80);
        foreach (var fragment in _fragments)
        {
            if (!fragment.PyramidReady || fragment.Failed || fragment.Levels.Count == 0)
                continue;
            if (!Intersects(fragment, left, top, right, bottom))
                continue;

            var needed = NeededPixels(fragment, _camera);
            var level = LodPlan.Choose(fragment.Levels, needed, fragment.HeldLongSide);
            if (fragment.HeldLongSide != level.LongSide)
                ForgetFailedLevels(fragment.Id);
            fragment.HeldLongSide = level.LongSide;
            Request(fragment, level);
        }
    }

    private void ForgetFailedLevels(string fragmentId)
    {
        var prefix = fragmentId + ":";
        _failed.RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));
    }

    private void Request(MapFragment fragment, LodFile level)
    {
        var key = fragment.CacheKey(level.LongSide);
        if (_cache.Contains(key) || _failed.Contains(key) || !_inFlight.Add(key))
            return;

        _ = LoadAsync(fragment, level, key);
    }

    private ImageLease? BestAvailable(MapFragment fragment, int targetLongSide)
    {
        LodFile? best = null;
        var bestDistance = int.MaxValue;
        foreach (var level in fragment.Levels)
        {
            if (!_cache.Contains(fragment.CacheKey(level.LongSide)))
                continue;

            var distance = Math.Abs(level.LongSide - targetLongSide);
            if (distance < bestDistance)
            {
                best = level;
                bestDistance = distance;
            }
        }

        return best is null ? null : _cache.TryLease(fragment.CacheKey(best.LongSide));
    }

    private static int NeededPixels(MapFragment fragment, Camera camera)
    {
        var screen = Math.Max(fragment.PixelWidth, fragment.PixelHeight) * Math.Abs(fragment.Scale) * camera.Zoom;
        if (double.IsNaN(screen) || screen < 1)
            return 1;

        return (int)Math.Ceiling(screen);
    }

    private static (double Left, double Top, double Right, double Bottom) ViewWorld(
        Camera camera, double viewWidth, double viewHeight, double margin)
    {
        var topLeft = camera.ScreenToWorld(-margin, -margin, viewWidth, viewHeight);
        var bottomRight = camera.ScreenToWorld(viewWidth + margin, viewHeight + margin, viewWidth, viewHeight);
        return (topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
    }

    private static bool Intersects(MapFragment fragment, double left, double top, double right, double bottom)
    {
        fragment.GetVisibleBounds(out var minX, out var minY, out var maxX, out var maxY);
        return minX < right && maxX > left && minY < bottom && maxY > top;
    }

    private static bool TryBounds(IReadOnlyList<MapFragment> fragments, out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = minY = maxX = maxY = 0;
        if (fragments.Count == 0)
            return false;

        minX = double.PositiveInfinity;
        minY = double.PositiveInfinity;
        maxX = double.NegativeInfinity;
        maxY = double.NegativeInfinity;
        foreach (var fragment in fragments)
        {
            fragment.GetVisibleBounds(out var left, out var top, out var right, out var bottom);
            minX = Math.Min(minX, left);
            minY = Math.Min(minY, top);
            maxX = Math.Max(maxX, right);
            maxY = Math.Max(maxY, bottom);
        }

        return true;
    }

    private MapFragment? Find(string id)
    {
        foreach (var fragment in _fragments)
        {
            if (fragment.Id == id)
                return fragment;
        }

        return null;
    }

    private void Push(ISheetEdit edit)
    {
        _redo.Clear();
        _undo.Add(edit);
        if (_undo.Count > 100)
            _undo.RemoveAt(0);
    }

    private static List<Header> ReadHeaders(IReadOnlyList<string> paths)
    {
        var results = new Header?[paths.Count];
        Parallel.For(
            0,
            paths.Count,
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            index =>
            {
                var path = paths[index];
                if (ImageFiles.TryReadSize(path, out var width, out var height))
                    results[index] = new Header(path, width, height);
            });

        var headers = new List<Header>(paths.Count);
        foreach (var header in results)
        {
            if (header is not null)
                headers.Add(header);
        }

        return headers;
    }

    private static string FormatBytes(long bytes)
    {
        var megabytes = bytes / (1024d * 1024d);
        return megabytes >= 10 ? $"{megabytes:0} MB" : $"{megabytes:0.0} MB";
    }

    private sealed record Header(string Path, int Width, int Height);
}
