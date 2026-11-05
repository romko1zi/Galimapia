using System.Collections.Concurrent;
using System.Diagnostics;
using Galimapia.Canvas;
using SkiaSharp;

namespace Galimapia.Tests;

public class ImportFiftyTests : IDisposable
{
    private readonly string _root;
    private readonly string _images;
    private readonly TestUi _ui = new();

    public ImportFiftyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "galimapia-tests", Guid.NewGuid().ToString("n"));
        _images = Path.Combine(_root, "scans");
        Directory.CreateDirectory(_images);
    }

    [Fact]
    public async Task Fifty_sheets_stay_on_the_small_level_until_one_is_zoomed_in()
    {
        const int count = 50;
        var paths = new string[count];
        for (var i = 0; i < count; i++)
            paths[i] = WriteScan(Path.Combine(_images, $"scan_{i:00}.jpg"), i);

        var scene = new MapScene(new LodStore(Path.Combine(_root, "lod")), new ImageCache(ImageCache.DefaultBudget), _ui.Invoke);
        try
        {
            await scene.ImportAsync(paths, 0, 0);
            await WaitUntil(scene, () => scene.Fragments.Count == count && scene.Fragments.All(fragment => fragment.PyramidReady), TimeSpan.FromMinutes(2));
            Assert.DoesNotContain(scene.Fragments, fragment => fragment.Failed);

            var camera = new Camera { CenterX = 1_000_000, CenterY = 0, Zoom = 1 };
            await scene.RunOnUi(() => scene.PrepareView(camera, 1200, 800));
            await Task.Delay(400);
            Assert.Equal(0, scene.CachedCount);

            var overview = Stopwatch.StartNew();
            await scene.RunOnUi(() =>
            {
                Assert.True(scene.TryGetBounds(out var minX, out var minY, out var maxX, out var maxY));
                camera.Fit(minX, minY, maxX, maxY, 1200, 800, 48);
                scene.PrepareView(camera, 1200, 800);
            });

            await WaitUntil(
                scene,
                () => scene.Fragments.All(fragment => scene.IsCached(fragment.Id, 512)),
                TimeSpan.FromSeconds(30));
            overview.Stop();
            Assert.True(overview.Elapsed < TimeSpan.FromSeconds(20), $"Overview decode took {overview.Elapsed}.");

            await scene.RunOnUi(() =>
            {
                Assert.True(scene.CacheBytes < 80L * 1024 * 1024, $"Cache is {scene.CacheBytes} bytes.");
                foreach (var fragment in scene.Fragments)
                {
                    Assert.Equal(512, fragment.HeldLongSide);
                    Assert.Equal(512, scene.CachedPixelWidth(fragment.Id, 512));
                    Assert.False(scene.IsCached(fragment.Id, fragment.LongSide));
                }
            });

            await scene.RunOnUi(() =>
            {
                var first = scene.Fragments[0];
                camera.CenterX = first.X + first.PixelWidth / 2d;
                camera.CenterY = first.Y + first.PixelHeight / 2d;
                camera.Zoom = 1;
                scene.PrepareView(camera, 1200, 800);
            });

            var firstId = scene.Fragments[0].Id;
            var fullSide = scene.Fragments[0].LongSide;
            var lastId = scene.Fragments[^1].Id;
            await WaitUntil(scene, () => scene.IsCached(firstId, fullSide), TimeSpan.FromSeconds(20));

            await scene.RunOnUi(() =>
            {
                Assert.Equal(1200, scene.CachedPixelWidth(firstId, fullSide));
                Assert.False(scene.IsCached(lastId, fullSide));
                var fullSheets = scene.Fragments.Count(fragment => scene.IsCached(fragment.Id, fragment.LongSide));
                Assert.InRange(fullSheets, 1, 3);
                Assert.True(scene.CacheBytes < 80L * 1024 * 1024, $"Cache is {scene.CacheBytes} bytes.");
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

    private static string WriteScan(string path, int index)
    {
        var info = new SKImageInfo(1200, 900, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor((byte)(40 + index * 3), (byte)(80 + index), (byte)(50 + index * 2)));
        using var font = new SKFont(SKTypeface.Default, 72);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawText(index.ToString(), 48, 120, font, paint);
        using var pixmap = bitmap.PeekPixels();
        using var image = SKImage.FromPixelCopy(pixmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 70);
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
                Name = "galimapia-test-ui",
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
