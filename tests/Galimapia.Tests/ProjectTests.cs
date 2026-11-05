using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Galimapia.Canvas;
using SkiaSharp;

namespace Galimapia.Tests;

public class ProjectTests : IDisposable
{
    private readonly string _root;
    private readonly TestUi _ui = new();

    public ProjectTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "galimapia-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Square_page_covers_the_extreme_pixels()
    {
        var wide = new MapFragment(Path.Combine(_root, "wide.jpg"), 0, 0, 200, 100);
        wide.RotationDegrees = 90;
        Assert.True(ExportPage.TryFrom([wide], out var page));
        wide.GetVisibleBounds(out var minX, out var minY, out var maxX, out var maxY);
        Assert.Equal(Math.Max(maxX - minX, maxY - minY), page.Size, 4);
        Assert.True(page.Contains(minX, minY));
        Assert.True(page.Contains(maxX, maxY));
        Assert.True(page.Contains(minX, maxY));
        Assert.True(page.Contains(maxX, minY));
    }

    [Fact]
    public async Task Save_opens_from_the_light_pyramid_without_rewriting_it()
    {
        var scan = WriteScan(Path.Combine(_root, "parcel.jpg"));
        var lod = Path.Combine(_root, "lod");
        var project = Path.Combine(_root, "project");
        var scene = new MapScene(new LodStore(lod), new ImageCache(32 * 1024 * 1024), _ui.Invoke);
        try
        {
            await scene.ImportAsync([scan], 10, 20);
            await scene.RunOnUi(() =>
            {
                var sheet = scene.Fragments[0];
                sheet.RotationDegrees = 15;
                sheet.Opacity = 0.4f;
                sheet.Crop.Add(new CropPoint(20, 10));
                sheet.Crop.Add(new CropPoint(80, 10));
                sheet.Crop.Add(new CropPoint(80, 40));
                sheet.Crop.Add(new CropPoint(10, 40));
                sheet.CropCommitted = true;
                sheet.CropIsRectangle = false;
            });

            List<SheetSnapshot> snapshots = [];
            ExportPage page = default;
            await scene.RunOnUi(() =>
            {
                snapshots = scene.SnapshotSheets().ToList();
                Assert.True(ExportPage.TryFrom(scene.Fragments, out page));
            });
            ProjectFile.Write(project, snapshots, 12.5, -4, 0.5, page, scene.LodRoot);

            var pyramid = Path.Combine(project, "cache_lod", "sheets", snapshots[0].Id, "512.jpg");
            Assert.True(File.Exists(pyramid));
            var written = File.GetLastWriteTimeUtc(pyramid);
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "project.json")));
            var source = json.RootElement.GetProperty("sheets")[0].GetProperty("source").GetString();
            Assert.StartsWith("sources/", source);
            Assert.Equal("parcel.jpg", json.RootElement.GetProperty("sheets")[0].GetProperty("name").GetString());
            Assert.True(Directory.Exists(Path.Combine(project, "masks")));

            ProjectFile.Write(project, snapshots, 12.5, -4, 0.5, page, scene.LodRoot);
            Assert.Equal(written, File.GetLastWriteTimeUtc(pyramid));

            var watch = Stopwatch.StartNew();
            var document = ProjectFile.Read(project);
            watch.Stop();
            Assert.InRange(watch.ElapsedMilliseconds, 0, 1000);
            Assert.Equal(12.5, document.CenterX, 4);
            Assert.Equal(-4, document.CenterY, 4);
            Assert.Equal(0.5, document.Zoom, 4);
            Assert.NotNull(document.Page);
            Assert.Equal(page.Size, document.Page.Value.Size, 4);
            Assert.NotNull(document.Sheets[0].Pyramid);

            var opened = new MapScene(new LodStore(Path.Combine(_root, "empty-lod")), new ImageCache(8 * 1024 * 1024), _ui.Invoke);
            try
            {
                await opened.RunOnUi(() => opened.Install(document));
                var sheet = opened.Fragments[0];
                Assert.True(sheet.PyramidReady);
                Assert.Equal("parcel.jpg", sheet.Name);
                Assert.Equal(15, sheet.RotationDegrees, 4);
                Assert.Equal(0.4f, sheet.Opacity, 3);
                Assert.True(sheet.HasCut);
                Assert.Equal(4, sheet.Crop.Count);
                Assert.Equal(20, sheet.Crop[0].X, 4);
                Assert.StartsWith(project, sheet.SourcePath, StringComparison.OrdinalIgnoreCase);
                sheet.GetVisibleBounds(out var minX, out var minY, out var maxX, out var maxY);
                Assert.True(opened.ExportPage!.Value.Contains(minX, minY));
                Assert.True(opened.ExportPage.Value.Contains(maxX, maxY));
            }
            finally
            {
                await opened.RunOnUi(opened.Dispose);
            }
        }
        finally
        {
            await scene.RunOnUi(scene.Dispose);
        }
    }

    public void Dispose()
    {
        _ui.Dispose();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string WriteScan(string path)
    {
        var info = new SKImageInfo(800, 600, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(70, 90, 60));
        using var pixmap = bitmap.PeekPixels();
        using var image = SKImage.FromPixelCopy(pixmap);
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
            _thread = new Thread(Pump) { IsBackground = true, Name = "galimapia-project-ui" };
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
