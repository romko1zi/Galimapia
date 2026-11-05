using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Unicode;

namespace Galimapia.Canvas;

/// <summary>
/// The newest log lines in named shared memory. The app writes them and the crash watcher holds the
/// same memory open, so the lines outlive a process that a native crash killed at once.
/// </summary>
public sealed class BlackBox : IDisposable
{
    public const int SlotCount = 1024;
    public const int SlotSize = 256;

    private const int Magic = 0x58424247;
    private const int HeaderSize = 64;
    private const int CleanOffset = 8;
    private const int LengthOffset = 8;
    private const int TextOffset = 10;
    private const int TextCapacity = SlotSize - TextOffset;

    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly object _gate = new();
    private readonly byte[] _buffer = new byte[TextCapacity];
    private long _sequence;
    private bool _disposed;

    private BlackBox(MemoryMappedFile map)
    {
        _map = map;
        _view = map.CreateViewAccessor();
    }

    public static long Capacity => HeaderSize + (long)SlotCount * SlotSize;

    public static string NameFor(int processId, long startTicks) =>
        $"Local\\Galimapia-blackbox-{processId}-{startTicks}";

    public static BlackBox Create(string name)
    {
        var box = new BlackBox(MemoryMappedFile.CreateNew(name, Capacity));
        box._view.Write(0, Magic);
        return box;
    }

    [SupportedOSPlatform("windows")]
    public static BlackBox? Open(string name)
    {
        MemoryMappedFile map;
        try
        {
            map = MemoryMappedFile.OpenExisting(name);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var box = new BlackBox(map);
        if (box._view.ReadInt32(0) == Magic)
            return box;

        box.Dispose();
        return null;
    }

    public bool IsClean => _view.ReadInt32(CleanOffset) != 0;

    public void MarkClean() => _view.Write(CleanOffset, 1);

    public void Write(string line)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            Utf8.FromUtf16(line, _buffer, out _, out var length);
            var sequence = ++_sequence;
            var slot = HeaderSize + sequence % SlotCount * SlotSize;
            // Sequence last: a slot cut off by a crash mid-write reads as empty, not as a mixed line.
            _view.Write(slot, 0L);
            _view.WriteArray(slot + TextOffset, _buffer, 0, length);
            _view.Write(slot + LengthOffset, (ushort)length);
            _view.Write(slot, sequence);
        }
    }

    /// <summary>Lines oldest first. At most <see cref="SlotCount"/>.</summary>
    public List<string> Read()
    {
        var found = new List<(long Sequence, string Text)>();
        var bytes = new byte[TextCapacity];
        for (var i = 0; i < SlotCount; i++)
        {
            var slot = HeaderSize + (long)i * SlotSize;
            var sequence = _view.ReadInt64(slot);
            var length = _view.ReadUInt16(slot + LengthOffset);
            if (sequence <= 0 || length > TextCapacity)
                continue;

            _view.ReadArray(slot + TextOffset, bytes, 0, length);
            found.Add((sequence, Encoding.UTF8.GetString(bytes, 0, length)));
        }

        found.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
        return found.ConvertAll(item => item.Text);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _view.Dispose();
            _map.Dispose();
        }
    }
}
