using System.Text.Json;
using System.Text.Json.Serialization;

namespace Galimapia.Canvas;

public sealed class RecentProject
{
    public required string Name { get; init; }
    public required string Folder { get; init; }
    public DateTime OpenedUtc { get; init; }

    public bool IsAvailable =>
        Directory.Exists(Folder) && File.Exists(Path.Combine(Folder, ProjectFile.FileName));
}

/// <summary>
/// The welcome screen's recent list. A missing folder stays in the list and shows as unavailable.
/// </summary>
public sealed class RecentProjects
{
    private static readonly string[] Reserved =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public RecentProjects(string storePath) => StorePath = storePath;

    public static RecentProjects Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Galimapia",
        "recent.json"));

    public string StorePath { get; }

    public static bool TryProjectName(string? raw, out string name)
    {
        name = (raw ?? "").Trim().TrimEnd('.', ' ');
        if (name.Length is 0 or > 80)
            return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        if (name is "." or "..")
            return false;

        var stem = Path.GetFileNameWithoutExtension(name);
        return !Reserved.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<RecentProject> Load()
    {
        if (!File.Exists(StorePath))
            return [];

        try
        {
            var dto = JsonSerializer.Deserialize<RecentFile>(File.ReadAllText(StorePath), Json);
            if (dto?.Projects is null)
                return [];

            return dto.Projects
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Folder))
                .Select(entry => new RecentProject
                {
                    Name = string.IsNullOrWhiteSpace(entry.Name) ? Path.GetFileName(entry.Folder) : entry.Name,
                    Folder = entry.Folder,
                    OpenedUtc = entry.OpenedUtc,
                })
                .Take(12)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    public void Remember(string folder, string name)
    {
        var full = Path.GetFullPath(folder);
        var projects = Load()
            .Where(project => !string.Equals(Path.GetFullPath(project.Folder), full, StringComparison.OrdinalIgnoreCase))
            .ToList();
        projects.Insert(0, new RecentProject
        {
            Name = name,
            Folder = full,
            OpenedUtc = DateTime.UtcNow,
        });
        if (projects.Count > 12)
            projects.RemoveRange(12, projects.Count - 12);

        var directory = Path.GetDirectoryName(StorePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var dto = new RecentFile
        {
            Projects = projects.Select(project => new RecentEntry
            {
                Name = project.Name,
                Folder = project.Folder,
                OpenedUtc = project.OpenedUtc,
            }).ToList(),
        };
        var temp = StorePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(dto, Json));
        File.Move(temp, StorePath, overwrite: true);
    }

    private sealed class RecentFile
    {
        public List<RecentEntry>? Projects { get; set; }
    }

    private sealed class RecentEntry
    {
        public string Name { get; set; } = "";
        public string Folder { get; set; } = "";
        public DateTime OpenedUtc { get; set; }
    }
}
