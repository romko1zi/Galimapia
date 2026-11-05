using SkiaSharp;

namespace Galimapia.Canvas;

/// <summary>
/// LRU of decoded sheets. A leased image stays alive until the frame that draws it is finished,
/// even if a newer frame has already evicted it from the cache.
/// </summary>
public sealed class ImageCache : IDisposable
{
    public const long DefaultBudget = 1024L * 1024L * 1024L;

    private readonly long _budget;
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheNode> _nodes = new();
    private readonly LinkedList<string> _order = new();
    private readonly List<CacheNode> _retired = [];
    private long _liveBytes;
    private bool _disposed;

    public ImageCache(long budget)
    {
        _budget = Math.Max(budget, 1);
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _nodes.Count;
        }
    }

    public long LiveBytes
    {
        get
        {
            lock (_gate)
                return _liveBytes;
        }
    }

    public bool Contains(string key)
    {
        lock (_gate)
            return _nodes.ContainsKey(key);
    }

    public void Add(string key, SKImage image, long bytes)
    {
        ArgumentNullException.ThrowIfNull(image);
        lock (_gate)
        {
            if (_disposed)
            {
                image.Dispose();
                return;
            }

            if (_nodes.TryGetValue(key, out var existing))
                Evict(existing);

            var node = new CacheNode(key, image, Math.Max(bytes, 1));
            node.Link = _order.AddFirst(key);
            _nodes.Add(key, node);
            _liveBytes += node.Bytes;
            Trim(key);
        }
    }

    public ImageLease? TryLease(string key)
    {
        lock (_gate)
        {
            if (_disposed || !_nodes.TryGetValue(key, out var node))
                return null;

            node.Use++;
            _order.Remove(node.Link!);
            node.Link = _order.AddFirst(key);
            return new ImageLease(this, node);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (var node in _nodes.Values)
                Destroy(node);
            foreach (var node in _retired)
                Destroy(node);

            _nodes.Clear();
            _order.Clear();
            _retired.Clear();
        }
    }

    internal void Release(CacheNode node)
    {
        lock (_gate)
        {
            node.Use--;
            if (node.Use == 0 && node.Evicted)
            {
                Destroy(node);
                _retired.Remove(node);
            }
        }
    }

    private void Trim(string justAdded)
    {
        while (_liveBytes > _budget && _order.Last is { } last)
        {
            if (last.Value == justAdded && _order.Count == 1)
                return;

            if (_nodes.TryGetValue(last.Value, out var node))
                Evict(node);
            else
                _order.RemoveLast();
        }
    }

    private void Evict(CacheNode node)
    {
        if (node.Link is not null)
            _order.Remove(node.Link);

        _nodes.Remove(node.Key);
        node.Evicted = true;
        if (node.Use == 0)
            Destroy(node);
        else
            _retired.Add(node);
    }

    private void Destroy(CacheNode node)
    {
        if (node.ImageDisposed)
            return;

        node.Image.Dispose();
        node.ImageDisposed = true;
        _liveBytes -= node.Bytes;
    }

    internal sealed class CacheNode
    {
        public CacheNode(string key, SKImage image, long bytes)
        {
            Key = key;
            Image = image;
            Bytes = bytes;
        }

        public string Key { get; }
        public SKImage Image { get; }
        public long Bytes { get; }
        public int Use { get; set; }
        public bool Evicted { get; set; }
        public bool ImageDisposed { get; set; }
        public LinkedListNode<string>? Link { get; set; }
    }
}

public sealed class ImageLease : IDisposable
{
    private ImageCache? _cache;
    private readonly ImageCache.CacheNode _node;

    internal ImageLease(ImageCache cache, ImageCache.CacheNode node)
    {
        _cache = cache;
        _node = node;
    }

    public SKImage Image => _node.Image;

    public void Dispose()
    {
        var cache = Interlocked.Exchange(ref _cache, null);
        cache?.Release(_node);
    }
}
