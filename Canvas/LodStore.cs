using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Galimapia.Canvas;

/// <summary>
/// Disk pyramid for a source file. Originals stay where the user dropped them.
/// </summary>
public sealed class LodStore
{
    private readonly ConcurrentDictionary<string, object> _gates = new();

    public LodStore(string root)
    {
        Root = root;
    }

    public static LodStore Shared { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Galimapia",
        "lod"));

    public string Root { get; }

    public PreparedImage Prepare(string sourcePath)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        if (!ImageFiles.TryReadSize(fullPath, out var width, out var height))
            throw new InvalidDataException($"Cannot read {fullPath}.");

        var sourceLong = Math.Max(width, height);
        var reduced = LodPlan.ReducedLongSides(sourceLong);
        var directory = Path.Combine(Root, Fingerprint(fullPath));
        var gate = _gates.GetOrAdd(directory, static _ => new object());
        lock (gate)
        {
            Directory.CreateDirectory(directory);
            var stampPath = Path.Combine(directory, "ready.txt");
            var stamp = $"v1|{width}x{height}|{string.Join(',', reduced)}";
            var fresh = File.Exists(stampPath) && File.ReadAllText(stampPath) == stamp;

            if (!fresh)
            {
                foreach (var side in reduced)
                {
                    var destination = LevelPath(directory, side);
                    if (!File.Exists(destination))
                        ImageFiles.WriteScaledJpeg(fullPath, destination, side, width, height);
                }

                File.WriteAllText(stampPath, stamp);
            }

            var levels = new List<LodFile>(reduced.Length + 1);
            foreach (var side in reduced)
                levels.Add(new LodFile(side, LevelPath(directory, side), isFull: false));

            levels.Add(new LodFile(sourceLong, fullPath, isFull: true));
            return new PreparedImage(width, height, levels);
        }
    }

    public string? FindPyramid(string sourcePath)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath))
            return null;

        var directory = Path.Combine(Root, Fingerprint(fullPath));
        return File.Exists(Path.Combine(directory, "ready.txt")) ? directory : null;
    }

    public bool TryLoadFragment(
        string fragmentId,
        string sourcePath,
        int width,
        int height,
        out PreparedImage? image)
    {
        image = null;
        var directory = FragmentDirectory(fragmentId);
        if (!TryReadStamp(directory, out var stampWidth, out var stampHeight, out var sides))
            return false;
        if (stampWidth != width || stampHeight != height)
            return false;

        var reduced = LodPlan.ReducedLongSides(Math.Max(width, height));
        if (!sides.SequenceEqual(reduced))
            return false;
        foreach (var side in reduced)
        {
            if (!File.Exists(LevelPath(directory, side)))
                return false;
        }

        image = Describe(directory, sourcePath, width, height, reduced);
        return true;
    }

    public PreparedImage PublishFragment(
        string fragmentId,
        string sourcePath,
        int width,
        int height,
        string? reuseDirectory)
    {
        if (TryLoadFragment(fragmentId, sourcePath, width, height, out var existing) && existing is not null)
            return existing;

        var reduced = LodPlan.ReducedLongSides(Math.Max(width, height));
        var directory = FragmentDirectory(fragmentId);
        Directory.CreateDirectory(directory);
        if (!string.IsNullOrWhiteSpace(reuseDirectory) && Directory.Exists(reuseDirectory))
        {
            foreach (var side in reduced)
            {
                var from = LevelPath(reuseDirectory, side);
                var to = LevelPath(directory, side);
                if (File.Exists(from) && !File.Exists(to))
                    File.Copy(from, to);
            }
        }

        foreach (var side in reduced)
        {
            var destination = LevelPath(directory, side);
            if (!File.Exists(destination))
                ImageFiles.WriteScaledJpeg(sourcePath, destination, side, width, height);
        }

        File.WriteAllText(Path.Combine(directory, "ready.txt"), Stamp(width, height, reduced));
        return Describe(directory, sourcePath, width, height, reduced);
    }

    private string FragmentDirectory(string fragmentId)
    {
        if (fragmentId.Length == 0 || fragmentId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Sheet id cannot be a folder name.");

        return Path.Combine(Root, "sheets", fragmentId);
    }

    private static bool TryReadStamp(string directory, out int width, out int height, out int[] sides)
    {
        width = 0;
        height = 0;
        sides = [];
        var stampPath = Path.Combine(directory, "ready.txt");
        if (!File.Exists(stampPath))
            return false;

        var parts = File.ReadAllText(stampPath).Split('|');
        if (parts.Length < 3 || parts[0] != "v1")
            return false;

        var size = parts[1].Split('x');
        if (size.Length != 2 || !int.TryParse(size[0], out width) || !int.TryParse(size[1], out height))
            return false;

        if (parts[2].Length == 0)
        {
            sides = [];
            return true;
        }

        var parsed = parts[2].Split(',');
        sides = new int[parsed.Length];
        for (var i = 0; i < parsed.Length; i++)
        {
            if (!int.TryParse(parsed[i], out sides[i]))
                return false;
        }

        return true;
    }

    private static string Stamp(int width, int height, IReadOnlyList<int> reduced) =>
        $"v1|{width}x{height}|{string.Join(',', reduced)}";

    private static PreparedImage Describe(
        string directory,
        string sourcePath,
        int width,
        int height,
        IReadOnlyList<int> reduced)
    {
        var levels = new List<LodFile>(reduced.Count + 1);
        foreach (var side in reduced)
            levels.Add(new LodFile(side, LevelPath(directory, side), isFull: false));
        levels.Add(new LodFile(Math.Max(width, height), sourcePath, isFull: true));
        return new PreparedImage(width, height, levels);
    }

    private static string LevelPath(string directory, int longSide) =>
        Path.Combine(directory, longSide + ".jpg");

    private static string Fingerprint(string fullPath)
    {
        var info = new FileInfo(fullPath);
        var text = $"{fullPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }
}

public sealed class PreparedImage
{
    public PreparedImage(int width, int height, IReadOnlyList<LodFile> levels)
    {
        Width = width;
        Height = height;
        Levels = levels;
    }

    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<LodFile> Levels { get; }
}
