using System.Diagnostics;
using Galimapia.Canvas;

namespace Galimapia.Tests;

public class CrashReportTests : IDisposable
{
    private const string HeapCorruption = "-1073740940";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "galimapia-crash-" + Guid.NewGuid().ToString("n"));
    private readonly string _log;
    private readonly string _dumps;

    public CrashReportTests()
    {
        _dumps = Path.Combine(_root, "dumps");
        Directory.CreateDirectory(_dumps);
        _log = Path.Combine(_root, "session.log");
        File.WriteAllText(_log, "20:13:48.487 action warp end commit=True sheet=186-1-5774_0057.jpg\n");
    }

    [Fact]
    public void Black_box_keeps_the_newest_lines_in_order()
    {
        using var box = BlackBox.Create(UniqueName());
        for (var i = 0; i < BlackBox.SlotCount + 10; i++)
            box.Write("line " + i);

        var lines = box.Read();
        Assert.Equal(BlackBox.SlotCount, lines.Count);
        Assert.Equal("line 10", lines[0]);
        Assert.Equal("line " + (BlackBox.SlotCount + 9), lines[^1]);
    }

    [Fact]
    public void A_long_line_is_cut_between_characters()
    {
        using var box = BlackBox.Create(UniqueName());
        box.Write(new string('ї', 400));

        var line = Assert.Single(box.Read());
        Assert.InRange(line.Length, 100, 399);
        Assert.All(line, character => Assert.Equal('ї', character));
    }

    [Fact]
    public void Another_handle_reads_the_lines_and_the_clean_mark()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var name = UniqueName();
        using var writer = BlackBox.Create(name);
        writer.Write("20:13:48.500 trace frame version=7 sheets=3 zoom=4");
        using var reader = BlackBox.Open(name);
        Assert.NotNull(reader);
        Assert.False(reader.IsClean);

        writer.MarkClean();
        Assert.True(reader.IsClean);
        Assert.Equal(new[] { "20:13:48.500 trace frame version=7 sheets=3 zoom=4" }, reader.Read());
        Assert.Null(BlackBox.Open(UniqueName()));
    }

    [Theory]
    [InlineData(0xC0000374u, true)]
    [InlineData(0xC0000005u, true)]
    [InlineData(0xC0000409u, true)]
    [InlineData(0xE0434352u, true)]
    [InlineData(0x80131623u, true)]
    [InlineData(0u, false)]
    [InlineData(1u, false)]
    [InlineData(0xFFFFFFFFu, false)]
    [InlineData(0xC000013Au, false)]
    public void Only_crash_codes_get_a_report(uint code, bool crash) =>
        Assert.Equal(crash, CrashWatch.IsCrash(code));

    [Fact]
    public void A_heap_corruption_exit_writes_a_report_with_the_dump_and_the_black_box()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var box = BlackBox.Create(UniqueName());
        box.Write("20:13:48.520 trace mesh 186-1-5774_0057.jpg triangles=67 image=5642x4684");
        using var app = Exit(HeapCorruption);
        var dump = Path.Combine(_dumps, $"cmd.exe.{app.Id}.dmp");
        File.WriteAllBytes(dump, new byte[64]);

        var report = CrashWatch.Watch(app, box, Options());

        Assert.NotNull(report);
        var text = File.ReadAllText(report);
        Assert.Contains("Exit code 0xC0000374: heap corruption", text);
        Assert.Contains(dump, text);
        Assert.Contains("cdb -z", text);
        Assert.Contains("trace mesh 186-1-5774_0057.jpg triangles=67 image=5642x4684", text);
        var last = File.ReadAllLines(_log)[^1];
        Assert.Contains("crash exit=0xC0000374", last);
        Assert.Contains("report=" + report, last);
    }

    [Fact]
    public void Without_a_black_box_the_report_falls_back_to_the_session_log()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var app = Exit(HeapCorruption);

        var report = CrashWatch.Watch(app, null, Options() with { DumpFolder = null });

        Assert.NotNull(report);
        var text = File.ReadAllText(report);
        Assert.Contains("warp end commit=True sheet=186-1-5774_0057.jpg", text);
        Assert.Contains("reg add", text);
    }

    [Fact]
    public void A_kill_from_outside_adds_one_line_and_no_report()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var box = BlackBox.Create(UniqueName());
        using var app = Exit("1");

        Assert.Null(CrashWatch.Watch(app, box, Options()));

        Assert.Contains("end exit=0x00000001 terminated from outside", File.ReadAllLines(_log)[^1]);
        Assert.Empty(Directory.GetFiles(_root, "crash-*.txt"));
    }

    [Fact]
    public void A_clean_exit_leaves_the_log_alone()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var box = BlackBox.Create(UniqueName());
        box.MarkClean();
        var before = File.ReadAllText(_log);
        using (var app = Exit("0"))
            Assert.Null(CrashWatch.Watch(app, box, Options()));
        using (var app = Exit("1"))
            Assert.Null(CrashWatch.Watch(app, box, Options()));

        Assert.Equal(before, File.ReadAllText(_log));
        Assert.Empty(Directory.GetFiles(_root, "crash-*.txt"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private CrashWatchOptions Options() => new(_log, _dumps, "cmd.exe", TimeSpan.Zero);

    private static Process Exit(string code)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/c", "exit", code },
        };
        return Process.Start(start) ?? throw new InvalidOperationException("cmd.exe did not start.");
    }

    private static string UniqueName() => "Local\\Galimapia-test-" + Guid.NewGuid().ToString("n");
}
