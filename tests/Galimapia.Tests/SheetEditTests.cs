using System.Collections.Concurrent;
using System.Diagnostics;
using Galimapia.Canvas;
using SkiaSharp;

namespace Galimapia.Tests;

public class SheetEditTests : IDisposable
{
    private readonly string _root;
    private readonly TestUi _ui = new();

    public SheetEditTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "galimapia-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Grid_is_square_and_cells_step_by_the_gap()
    {
        Assert.Equal(1, SheetGrid.Columns(1));
        Assert.Equal(2, SheetGrid.Columns(4));
        Assert.Equal(8, SheetGrid.Columns(50));
        Assert.Equal(9, SheetGrid.Columns(70));

        var cell = SheetGrid.Cell(3, 2, 10, 20, 100, 50, 48);
        Assert.Equal(158, cell.X);
        Assert.Equal(118, cell.Y);
    }

    [Fact]
    public void Rotation_and_scale_are_around_the_center()
    {
        var flat = new SheetFrame(10, 20, 100, 40, 0, 1);
        Assert.True(flat.Contains(10, 20));
        Assert.True(flat.Contains(109, 59));
        Assert.False(flat.Contains(9, 20));
        Assert.False(flat.Contains(111, 20));

        var scaled = new SheetFrame(0, 0, 100, 50, 0, 2);
        scaled.GetBounds(out var minX, out var minY, out var maxX, out var maxY);
        Assert.Equal(-50, minX, 5);
        Assert.Equal(-25, minY, 5);
        Assert.Equal(150, maxX, 5);
        Assert.Equal(75, maxY, 5);

        var turned = new SheetFrame(0, 0, 200, 100, 90, 1);
        turned.GetBounds(out minX, out minY, out maxX, out maxY);
        Assert.Equal(50, minX, 5);
        Assert.Equal(-50, minY, 5);
        Assert.Equal(150, maxX, 5);
        Assert.Equal(150, maxY, 5);
        Assert.True(turned.Contains(100, 50));
        Assert.False(turned.Contains(0, 0));

        var mirrored = new SheetFrame(0, 0, 100, 50, 0, 1, FlipX: true);
        var right = mirrored.ToWorld(100, 25);
        Assert.Equal(0, right.X, 5);
        Assert.Equal(25, right.Y, 5);
    }

    [Fact]
    public void Four_crop_points_hide_the_page_margin()
    {
        var sheet = new MapFragment(Path.Combine(_root, "unused.jpg"), 0, 0, 100, 80);
        Assert.True(sheet.ContainsWorld(1, 1));
        sheet.Crop.Add(new CropPoint(20, 20));
        sheet.Crop.Add(new CropPoint(80, 20));
        sheet.Crop.Add(new CropPoint(80, 60));
        Assert.True(sheet.ContainsWorld(1, 1));
        sheet.Crop.Add(new CropPoint(20, 60));
        Assert.True(sheet.ContainsWorld(1, 1));
        sheet.CropCommitted = true;
        Assert.False(sheet.ContainsWorld(1, 1));
        Assert.True(sheet.ContainsWorld(50, 40));
        sheet.GetVisibleBounds(out var minX, out var minY, out var maxX, out var maxY);
        Assert.Equal(20, minX, 5);
        Assert.Equal(20, minY, 5);
        Assert.Equal(80, maxX, 5);
        Assert.Equal(60, maxY, 5);
    }

    [Fact]
    public void Outline_rejects_a_crossing_and_keeps_a_square()
    {
        var chain = new CropPoint[] { new(0, 0), new(10, 0), new(10, 10) };
        Assert.False(CropPolygon.CanAppend(chain, new CropPoint(5, -5)));
        Assert.True(CropPolygon.CanAppend(chain, new CropPoint(0, 10)));
        Assert.True(CropPolygon.CanClose([new(0, 0), new(10, 0), new(0, 10)]));

        Assert.False(CropPolygon.IsSimple([new(0, 0), new(1, 1), new(0, 1), new(1, 0)]));
        Assert.True(CropPolygon.IsSimple([new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
    }

    [Fact]
    public void Two_points_place_the_sheet_on_both_targets()
    {
        var frame = new SheetFrame(0, 0, 100, 50, 0, 1);
        Assert.True(SheetAlign.TryPlace(
            frame,
            10, 10,
            90, 10,
            100, 200,
            100, 280,
            0.05,
            8,
            out var x,
            out var y,
            out var rotation,
            out var scale));

        Assert.Equal(35, x, 4);
        Assert.Equal(215, y, 4);
        Assert.Equal(90, rotation, 4);
        Assert.Equal(1, scale, 4);

        var placed = frame with { X = x, Y = y, RotationDegrees = rotation, Scale = scale };
        var first = placed.ToWorld(10, 10);
        var second = placed.ToWorld(90, 10);
        Assert.Equal(100, first.X, 4);
        Assert.Equal(200, first.Y, 4);
        Assert.Equal(100, second.X, 4);
        Assert.Equal(280, second.Y, 4);
    }

    [Fact]
    public void Applied_crop_box_is_the_extreme_pixels()
    {
        var sheet = new MapFragment(Path.Combine(_root, "unused.jpg"), 0, 0, 100, 80);
        sheet.Crop.Add(new CropPoint(10, 10));
        sheet.Crop.Add(new CropPoint(30, 10));
        sheet.Crop.Add(new CropPoint(30, 20));
        sheet.Crop.Add(new CropPoint(10, 20));
        sheet.CropCommitted = true;

        Assert.Equal(10, sheet.Frame.EdgeLeft, 4);
        Assert.Equal(10, sheet.Frame.EdgeTop, 4);
        Assert.Equal(30, sheet.Frame.EdgeRight, 4);
        Assert.Equal(20, sheet.Frame.EdgeBottom, 4);
        Assert.Equal(20, sheet.Frame.PivotLocalX, 4);
        Assert.Equal(15, sheet.Frame.PivotLocalY, 4);

        sheet.RotationDegrees = 90;
        var pivot = sheet.Frame.ToWorld(20, 15);
        Assert.Equal(20, pivot.X, 4);
        Assert.Equal(15, pivot.Y, 4);
    }

    [Fact]
    public async Task Edits_hit_the_front_sheet_and_undo()
    {
        var paths = new string[4];
        for (var i = 0; i < paths.Length; i++)
            paths[i] = WriteScan(Path.Combine(_root, $"sheet_{i}.jpg"));

        var scene = new MapScene(new LodStore(Path.Combine(_root, "lod")), new ImageCache(8 * 1024 * 1024), _ui.Invoke);
        try
        {
            await scene.ImportAsync(paths, 0, 0);
            await WaitUntil(scene, () => scene.Fragments.Count == 4 && scene.Fragments.All(sheet => sheet.PyramidReady), TimeSpan.FromSeconds(30));

            await scene.RunOnUi(() =>
            {
                var sheets = scene.Fragments;
                Assert.Equal((0d, 0d), (sheets[0].X, sheets[0].Y));
                Assert.Equal((78d, 0d), (sheets[1].X, sheets[1].Y));
                Assert.Equal((0d, 68d), (sheets[2].X, sheets[2].Y));
                Assert.Equal((78d, 68d), (sheets[3].X, sheets[3].Y));

                var moved = sheets[2];
                scene.BeginEdit(moved);
                scene.MoveTo(moved, moved.X + 10, moved.Y);
                scene.MoveTo(moved, moved.X + 15, moved.Y);
                scene.EndEdit();
                Assert.Equal(25, moved.X, 5);
                Assert.True(scene.Undo());
                Assert.Equal(0, moved.X, 5);
                Assert.True(scene.Redo());
                Assert.Equal(25, moved.X, 5);
                Assert.True(scene.Undo());

                scene.BeginEdit(moved);
                scene.SetOpacity(moved, 0.25f);
                scene.EndEdit();
                Assert.Equal(0.25f, moved.Opacity);
                Assert.True(scene.Undo());
                Assert.Equal(1f, moved.Opacity);

                scene.BeginEdit(moved);
                scene.SetScale(moved, 100);
                scene.EndEdit();
                Assert.Equal(MapScene.MaxScale, moved.Scale);
                Assert.True(scene.Undo());
                Assert.Equal(1, moved.Scale, 5);

                scene.BeginEdit(moved);
                scene.SetRotation(moved, 90);
                scene.EndEdit();
                moved.Frame.GetBounds(out var minX, out var minY, out var maxX, out var maxY);
                Assert.Equal(5, minX, 5);
                Assert.Equal(63, minY, 5);
                Assert.Equal(25, maxX, 5);
                Assert.Equal(93, maxY, 5);
                Assert.True(scene.Undo());
                Assert.Equal(0, moved.RotationDegrees, 5);

                var back = sheets[0];
                var front = sheets[1];
                scene.BeginEdit(front);
                scene.MoveTo(front, 0, 0);
                scene.EndEdit();
                Assert.Same(front, scene.HitTest(15, 10));
                Assert.True(scene.SendBackward(front));
                Assert.Same(back, scene.HitTest(15, 10));
                Assert.True(scene.Undo());
                Assert.Same(front, scene.HitTest(15, 10));

                var id = front.Id;
                var index = scene.IndexOf(front);
                Assert.True(scene.Delete(front));
                Assert.Equal(3, scene.Fragments.Count);
                Assert.Same(back, scene.HitTest(15, 10));
                Assert.True(scene.Undo());
                Assert.Equal(4, scene.Fragments.Count);
                Assert.Equal(id, scene.Fragments[index].Id);
                Assert.Same(scene.Fragments[index], scene.HitTest(15, 10));
            });
        }
        finally
        {
            await scene.RunOnUi(scene.Dispose);
        }
    }

    [Fact]
    public async Task Reset_crop_and_reorder_are_single_undo_steps()
    {
        var first = WriteScan(Path.Combine(_root, "a.jpg"));
        var second = WriteScan(Path.Combine(_root, "b.jpg"));
        var scene = new MapScene(new LodStore(Path.Combine(_root, "lod")), new ImageCache(8 * 1024 * 1024), _ui.Invoke);
        try
        {
            await scene.ImportAsync([first, second], 0, 0);
            await scene.RunOnUi(() =>
            {
                var back = scene.Fragments[0];
                var front = scene.Fragments[1];
                scene.BeginEdit(back);
                scene.SetRotation(back, 20);
                scene.SetScale(back, 2);
                scene.SetFlipX(back, true);
                scene.EndEdit();
                scene.ResetAppearance(back);
                Assert.Equal(0, back.RotationDegrees, 5);
                Assert.Equal(1, back.Scale, 5);
                Assert.False(back.FlipX);
                Assert.True(scene.Undo());
                Assert.Equal(20, back.RotationDegrees, 5);
                Assert.Equal(2, back.Scale, 5);
                Assert.True(back.FlipX);

                scene.AddCropPoint(back, 5, 5);
                scene.AddCropPoint(back, 20, 5);
                scene.AddCropPoint(back, 20, 15);
                scene.AddCropPoint(back, 5, 15);
                var anchor = back.Frame.ToWorld(10, 10);
                scene.EndCrop();
                Assert.Equal(4, back.Crop.Count);
                Assert.True(back.CropCommitted);
                var anchored = back.Frame.ToWorld(10, 10);
                Assert.Equal(anchor.X, anchored.X, 3);
                Assert.Equal(anchor.Y, anchored.Y, 3);
                Assert.Equal(5, back.Frame.EdgeTop, 3);
                var outside = back.Frame.ToWorld(0, 0);
                var inside = back.Frame.ToWorld(10, 10);
                Assert.False(back.ContainsWorld(outside.X, outside.Y));
                Assert.True(back.ContainsWorld(inside.X, inside.Y));
                Assert.True(scene.Undo());
                Assert.Empty(back.Crop);
                Assert.False(back.CropCommitted);

                Assert.True(scene.MoveToIndex(back, 1));
                Assert.Same(back, scene.Fragments[1]);
                Assert.Same(front, scene.Fragments[0]);
                Assert.True(scene.Undo());
                Assert.Same(back, scene.Fragments[0]);
                Assert.Same(front, scene.Fragments[1]);
            });
        }
        finally
        {
            await scene.RunOnUi(scene.Dispose);
        }
    }

    [Fact]
    public async Task A_cropped_front_sheet_does_not_hide_the_sheet_in_its_old_margin()
    {
        var first = WriteScan(Path.Combine(_root, "back.jpg"));
        var second = WriteScan(Path.Combine(_root, "front.jpg"));
        var scene = new MapScene(new LodStore(Path.Combine(_root, "lod")), new ImageCache(8 * 1024 * 1024), _ui.Invoke);
        try
        {
            await scene.ImportAsync([first, second], 0, 0);
            await scene.RunOnUi(() =>
            {
                var back = scene.Fragments[0];
                var front = scene.Fragments[1];
                scene.MoveTo(back, 0, 0);
                scene.MoveTo(front, 0, 0);
                front.Crop.Add(new CropPoint(15, 0));
                front.Crop.Add(new CropPoint(30, 0));
                front.Crop.Add(new CropPoint(30, 20));
                front.Crop.Add(new CropPoint(15, 20));
                front.CropCommitted = true;

                Assert.Same(front, scene.HitTestFrame(5, 10));
                Assert.Same(back, scene.HitTestCrop(5, 10, null));
                Assert.Same(front, scene.HitTestCrop(5, 10, front));
                Assert.Same(front, scene.HitTestCrop(22, 10, null));
            });
        }
        finally
        {
            await scene.RunOnUi(scene.Dispose);
        }
    }

    public void Dispose()
    {
        _ui.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static async Task WaitUntil(MapScene scene, Func<bool> ready, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            var done = false;
            await scene.RunOnUi(() => done = ready());
            if (done)
                return;

            await Task.Delay(30);
        }

        throw new TimeoutException($"Timed out after {timeout}. {scene.SheetSummary}, cached {scene.CachedCount}.");
    }

    private static string WriteScan(string path)
    {
        var info = new SKImageInfo(30, 20, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        bitmap.Erase(new SKColor(80, 110, 70));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }

    private sealed class TestUi : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        private int _threadId;

        public TestUi()
        {
            _thread = new Thread(Pump)
            {
                IsBackground = true,
                Name = "galimapia-edit-ui",
            };
            _thread.Start();
            _ready.Wait();
        }

        public Task Invoke(Action action)
        {
            if (Environment.CurrentManagedThreadId == _threadId)
            {
                action();
                return Task.CompletedTask;
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try
                {
                    action();
                    done.SetResult();
                }
                catch (Exception exception)
                {
                    done.SetException(exception);
                }
            });
            return done.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(2));
            _queue.Dispose();
            _ready.Dispose();
        }

        private void Pump()
        {
            _threadId = Environment.CurrentManagedThreadId;
            _ready.Set();
            foreach (var action in _queue.GetConsumingEnumerable())
                action();
        }
    }
}
