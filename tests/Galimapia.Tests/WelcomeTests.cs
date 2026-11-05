using Galimapia.Canvas;

namespace Galimapia.Tests;

public class WelcomeTests : IDisposable
{
    private readonly string _root;

    public WelcomeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "galimapia-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Project_name_rejects_a_path()
    {
        Assert.True(RecentProjects.TryProjectName("  Vienna 1820  ", out var name));
        Assert.Equal("Vienna 1820", name);
        Assert.False(RecentProjects.TryProjectName("maps/vienna", out _));
        Assert.False(RecentProjects.TryProjectName("CON", out _));
        Assert.False(RecentProjects.TryProjectName("   ", out _));
    }

    [Fact]
    public void Recent_list_keeps_a_missing_folder_and_marks_it()
    {
        var store = new RecentProjects(Path.Combine(_root, "recent.json"));
        var missing = Path.Combine(_root, "gone");
        var present = Path.Combine(_root, "kept");
        ProjectFile.CreateEmpty(present);
        store.Remember(missing, "Gone");
        store.Remember(present, "Kept");
        store.Remember(present, "Kept again");

        var projects = store.Load();
        Assert.Equal(2, projects.Count);
        Assert.Equal("Kept again", projects[0].Name);
        Assert.True(projects[0].IsAvailable);
        Assert.Equal("Gone", projects[1].Name);
        Assert.False(projects[1].IsAvailable);
        Assert.True(File.Exists(Path.Combine(present, "project.json")));
        Assert.True(Directory.Exists(Path.Combine(present, "sources")));
        Assert.True(Directory.Exists(Path.Combine(present, "masks")));
    }

    [Fact]
    public void Empty_project_opens_without_sheets()
    {
        var folder = Path.Combine(_root, "Atlas");
        ProjectFile.CreateEmpty(folder);
        var document = ProjectFile.Read(folder);
        Assert.Empty(document.Sheets);
        Assert.Equal(1, document.Zoom);
        Assert.Null(document.Page);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
