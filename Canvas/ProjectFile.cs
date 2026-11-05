using System.Text.Json;
using System.Text.Json.Serialization;

namespace Galimapia.Canvas;

public sealed class SheetSnapshot
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string SourcePath { get; init; }
    public required int PixelWidth { get; init; }
    public required int PixelHeight { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Rotation { get; init; }
    public required double Scale { get; init; }
    public required bool FlipX { get; init; }
    public required bool FlipY { get; init; }
    public required float Opacity { get; init; }
    public float Brightness { get; init; }
    public float Contrast { get; init; } = 1;
    public float Hue { get; init; }
    public required bool CropCommitted { get; init; }
    public required bool CropRectangle { get; init; }
    public required CropPoint[] Crop { get; init; }
    public CropPoint[] Warp { get; init; } = [];

    public static SheetSnapshot From(MapFragment fragment) => new()
    {
        Id = fragment.Id,
        Name = fragment.Name,
        SourcePath = fragment.SourcePath,
        PixelWidth = fragment.PixelWidth,
        PixelHeight = fragment.PixelHeight,
        X = fragment.X,
        Y = fragment.Y,
        Rotation = fragment.RotationDegrees,
        Scale = fragment.Scale,
        FlipX = fragment.FlipX,
        FlipY = fragment.FlipY,
        Opacity = fragment.Opacity,
        Brightness = fragment.Brightness,
        Contrast = fragment.Contrast,
        Hue = fragment.Hue,
        CropCommitted = fragment.CropCommitted,
        CropRectangle = fragment.CropIsRectangle,
        Crop = fragment.Crop.ToArray(),
        Warp = fragment.Warp.ToArray(),
    };
}

public sealed class PublishedSheet
{
    public PublishedSheet(string id, string sourcePath, PreparedImage pyramid)
    {
        Id = id;
        SourcePath = sourcePath;
        Pyramid = pyramid;
    }

    public string Id { get; }
    public string SourcePath { get; }
    public PreparedImage Pyramid { get; }
}

public sealed class LoadedSheet
{
    public LoadedSheet(SheetSnapshot sheet, PreparedImage? pyramid)
    {
        Sheet = sheet;
        Pyramid = pyramid;
    }

    public SheetSnapshot Sheet { get; }
    public PreparedImage? Pyramid { get; }
}

public sealed class ProjectDocument
{
    public ProjectDocument(
        string folder,
        double centerX,
        double centerY,
        double zoom,
        ExportPage? page,
        IReadOnlyList<LoadedSheet> sheets)
    {
        Folder = folder;
        CenterX = centerX;
        CenterY = centerY;
        Zoom = zoom;
        Page = page;
        Sheets = sheets;
    }

    public string Folder { get; }
    public double CenterX { get; }
    public double CenterY { get; }
    public double Zoom { get; }
    public ExportPage? Page { get; }
    public IReadOnlyList<LoadedSheet> Sheets { get; }
}

/// <summary>
/// A project folder: project.json, sources/, cache_lod/, masks/.
/// Paths in the json are relative. Pyramids are reused, not rebuilt, when the stamp matches.
/// </summary>
public static class ProjectFile
{
    public const string FileName = "project.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static bool IsProject(string folder) =>
        File.Exists(Path.Combine(folder, FileName));

    public static void CreateEmpty(string folder) =>
        Write(folder, [], 0, 0, 1, null, folder);

    public static IReadOnlyList<PublishedSheet> Write(
        string folder,
        IReadOnlyList<SheetSnapshot> sheets,
        double centerX,
        double centerY,
        double zoom,
        ExportPage? page,
        string previousLodRoot)
    {
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "sources"));
        Directory.CreateDirectory(Path.Combine(folder, "cache_lod"));
        Directory.CreateDirectory(Path.Combine(folder, "masks"));

        var projectLod = new LodStore(Path.Combine(folder, "cache_lod"));
        var previousLod = new LodStore(previousLodRoot);
        var published = new List<PublishedSheet>(sheets.Count);
        var dtoSheets = new List<SheetDto>(sheets.Count);
        foreach (var sheet in sheets)
        {
            var destination = CopySource(folder, sheet);
            var reuse = previousLod.FindPyramid(sheet.SourcePath);
            var pyramid = projectLod.PublishFragment(
                sheet.Id,
                destination,
                sheet.PixelWidth,
                sheet.PixelHeight,
                reuse);
            published.Add(new PublishedSheet(sheet.Id, destination, pyramid));
            dtoSheets.Add(ToDto(folder, sheet, destination));
        }

        var dto = new ProjectDto
        {
            Version = 1,
            Camera = new CameraDto { CenterX = centerX, CenterY = centerY, Zoom = zoom },
            Export = page is { } frame
                ? new ExportDto { X = frame.X, Y = frame.Y, Size = frame.Size }
                : null,
            Sheets = dtoSheets,
        };
        var jsonPath = Path.Combine(folder, FileName);
        var temp = jsonPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(dto, Json));
        File.Move(temp, jsonPath, overwrite: true);
        return published;
    }

    public static ProjectDocument Read(string folder)
    {
        var jsonPath = Path.Combine(folder, FileName);
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException("This folder has no project.json.", jsonPath);

        var dto = JsonSerializer.Deserialize<ProjectDto>(File.ReadAllText(jsonPath), Json)
            ?? throw new InvalidDataException("project.json is empty.");
        if (dto.Version != 1)
            throw new InvalidDataException($"project.json version {dto.Version} is not supported.");

        var store = new LodStore(Path.Combine(folder, "cache_lod"));
        var sheets = new List<LoadedSheet>();
        foreach (var sheet in dto.Sheets ?? [])
        {
            if (string.IsNullOrWhiteSpace(sheet.Id) || sheet.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("project.json has a sheet id that is not a file name.");

            var source = Resolve(folder, sheet.Source ?? "");
            var snapshot = new SheetSnapshot
            {
                Id = sheet.Id,
                Name = string.IsNullOrWhiteSpace(sheet.Name) ? Path.GetFileName(source) : sheet.Name,
                SourcePath = source,
                PixelWidth = sheet.PixelWidth,
                PixelHeight = sheet.PixelHeight,
                X = sheet.X,
                Y = sheet.Y,
                Rotation = sheet.Rotation,
                Scale = sheet.Scale <= 0 ? 1 : sheet.Scale,
                FlipX = sheet.FlipX,
                FlipY = sheet.FlipY,
                Opacity = sheet.Opacity,
                Brightness = (float)(sheet.Brightness ?? 0),
                Contrast = (float)(sheet.Contrast ?? 1),
                Hue = (float)(sheet.Hue ?? 0),
                CropCommitted = sheet.CropCommitted,
                CropRectangle = sheet.CropRectangle,
                Crop = (sheet.Crop ?? []).Select(point => new CropPoint(point.X, point.Y)).ToArray(),
                Warp = (sheet.Warp ?? []).Select(point => new CropPoint(point.X, point.Y)).ToArray(),
            };
            PreparedImage? pyramid = null;
            if (store.TryLoadFragment(snapshot.Id, source, snapshot.PixelWidth, snapshot.PixelHeight, out var loaded))
                pyramid = loaded;
            sheets.Add(new LoadedSheet(snapshot, pyramid));
        }

        ExportPage? page = dto.Export is { Size: > 0 } export
            ? new ExportPage(export.X, export.Y, export.Size)
            : null;
        var camera = dto.Camera;
        return new ProjectDocument(
            Path.GetFullPath(folder),
            camera?.CenterX ?? 0,
            camera?.CenterY ?? 0,
            camera?.Zoom is > 0 and var zoom ? zoom : 1,
            page,
            sheets);
    }

    private static string CopySource(string folder, SheetSnapshot sheet)
    {
        var extension = Path.GetExtension(sheet.SourcePath);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".img";
        var destination = Path.GetFullPath(Path.Combine(folder, "sources", sheet.Id + extension.ToLowerInvariant()));
        var source = Path.GetFullPath(sheet.SourcePath);
        if (!File.Exists(source))
            throw new FileNotFoundException($"Missing scan {sheet.Name}.", source);

        if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            var incoming = new FileInfo(source);
            var current = new FileInfo(destination);
            if (!current.Exists || current.Length != incoming.Length || incoming.LastWriteTimeUtc > current.LastWriteTimeUtc)
                File.Copy(source, destination, overwrite: true);
        }

        return destination;
    }

    private static SheetDto ToDto(string folder, SheetSnapshot sheet, string sourcePath) => new()
    {
        Id = sheet.Id,
        Name = sheet.Name,
        Source = Relative(folder, sourcePath),
        PixelWidth = sheet.PixelWidth,
        PixelHeight = sheet.PixelHeight,
        X = sheet.X,
        Y = sheet.Y,
        Rotation = sheet.Rotation,
        Scale = sheet.Scale,
        FlipX = sheet.FlipX,
        FlipY = sheet.FlipY,
        Opacity = sheet.Opacity,
        Brightness = sheet.Brightness,
        Contrast = sheet.Contrast,
        Hue = sheet.Hue,
        CropCommitted = sheet.CropCommitted,
        CropRectangle = sheet.CropRectangle,
        Crop = sheet.Crop.Select(point => new PointDto { X = point.X, Y = point.Y }).ToList(),
        Warp = sheet.Warp.Length == 0
            ? null
            : sheet.Warp.Select(point => new PointDto { X = point.X, Y = point.Y }).ToList(),
    };

    private static string Relative(string folder, string fullPath)
    {
        var relative = Path.GetRelativePath(folder, fullPath).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException($"Refusing to store a path outside the project: {fullPath}");
        return relative;
    }

    private static string Resolve(string folder, string relative)
    {
        var combined = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(folder);
        if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(combined, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"project.json points outside the folder: {relative}");
        return combined;
    }

    private sealed class ProjectDto
    {
        public int Version { get; set; }
        public CameraDto? Camera { get; set; }
        public ExportDto? Export { get; set; }
        public List<SheetDto>? Sheets { get; set; }
    }

    private sealed class CameraDto
    {
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public double Zoom { get; set; }
    }

    private sealed class ExportDto
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Size { get; set; }
    }

    private sealed class SheetDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Source { get; set; }
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Rotation { get; set; }
        public double Scale { get; set; }
        public bool FlipX { get; set; }
        public bool FlipY { get; set; }
        public float Opacity { get; set; }
        public double? Brightness { get; set; }
        public double? Contrast { get; set; }
        public double? Hue { get; set; }
        public bool CropCommitted { get; set; }
        public bool CropRectangle { get; set; }
        public List<PointDto>? Crop { get; set; }
        public List<PointDto>? Warp { get; set; }
    }

    private sealed class PointDto
    {
        public double X { get; set; }
        public double Y { get; set; }
    }
}
