using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace Galimapia.Canvas;

public sealed record CrashWatchOptions(string LogPath, string? DumpFolder, string ExeName, TimeSpan DumpWait);

/// <summary>
/// A hidden second Galimapia process that waits for the app to end. Heap corruption, an access
/// violation, or a fail-fast kills the app before any handler inside it runs, so the crash is
/// recorded from outside: the exit code, the Windows dump, and the black box lines.
/// </summary>
public static class CrashWatch
{
    public const string Argument = "--crash-watch";
    public const string LocalDumpsKey = @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps";

    public static int Launch(int processId, long startTicks, string logPath, string boxName)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The app path is unknown.");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(CrashWatch).Assembly.Location);
        start.ArgumentList.Add(Argument);
        start.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(startTicks.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(logPath);
        start.ArgumentList.Add(boxName);
        using var watcher = Process.Start(start) ?? throw new InvalidOperationException("The crash watcher did not start.");
        return watcher.Id;
    }

    /// <summary>True when this process is the watcher. Main returns right after it.</summary>
    public static bool TryRun(string[] args)
    {
        if (args.Length == 0 || args[0] != Argument)
            return false;
        if (!OperatingSystem.IsWindows()
            || args.Length < 5
            || !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId)
            || !long.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startTicks))
            return true;

        try
        {
            using var app = Process.GetProcessById(processId);
            _ = app.SafeHandle;
            if (app.StartTime.ToUniversalTime().Ticks != startTicks)
                return true;

            using var box = BlackBox.Open(args[4]);
            var exeName = Path.GetFileName(Environment.ProcessPath) ?? AppInfo.Name + ".exe";
            Watch(app, box, new CrashWatchOptions(args[3], DumpFolder(exeName), exeName, TimeSpan.FromSeconds(20)));
        }
        catch (Exception)
        {
            // The watcher has no window. An exception here must not become a second crash dialog and dump.
        }

        return true;
    }

    /// <summary>
    /// Waits for <paramref name="app"/> to end. Returns the crash report path, or null when the
    /// exit was clean or was not a crash.
    /// </summary>
    public static string? Watch(Process app, BlackBox? box, CrashWatchOptions options)
    {
        var processId = app.Id;
        DateTime? started = null;
        try
        {
            started = app.StartTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }

        app.WaitForExit();
        var code = unchecked((uint)app.ExitCode);
        var ended = DateTime.Now;
        if (code == 0)
            return null;

        if (!IsCrash(code))
        {
            if (box?.IsClean != true)
                AppendLine(options.LogPath, "end", $"exit={Hex(code)} {Describe(code)}");
            return null;
        }

        var dump = FindDump(options.DumpFolder, options.ExeName, processId, options.DumpWait);
        var folder = Path.GetDirectoryName(options.LogPath) ?? ".";
        var report = Path.Combine(folder, $"crash-{ended.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{processId}.txt");
        File.WriteAllText(report, Report(processId, started, ended, code, options, dump, box));
        AppendLine(options.LogPath, "crash", $"exit={Hex(code)} {Describe(code)} report={report} dump={dump ?? "none"}");
        return report;
    }

    public static bool IsCrash(uint code) =>
        code is 0x80131506 or 0x80131623
        || ((code & 0xC0000000) == 0xC0000000 && code is not 0xFFFFFFFF and not 0xC000013A);

    public static string Describe(uint code) => code switch
    {
        0 => "clean exit",
        1 => "terminated from outside (Task Manager or another program)",
        0xFFFFFFFF => "terminated from outside (a debugger stop or a kill)",
        0xC000013A => "closed with Ctrl+C or by closing its console",
        0x40010004 => "terminated by a debugger",
        0xC0000005 => "access violation: code read or wrote an address it does not own",
        0xC0000374 => "heap corruption: native code freed or overwrote heap memory it did not own",
        0xC0000409 => "fail-fast: a security check or __fastfail stopped the process",
        0xC00000FD => "stack overflow",
        0xC0000017 => "out of memory",
        0xC000012D => "out of memory: the commit limit was reached",
        0xC0000602 => "fail-fast exception raised by the runtime",
        0xE0434352 => "unhandled .NET exception; its stack is in the fault lines of the session log",
        0x80131506 => ".NET runtime internal error",
        0x80131623 => "Environment.FailFast",
        _ => IsCrash(code) ? "native crash" : "ended without a clean exit",
    };

    /// <summary>Where Windows Error Reporting writes a local dump of this exe, or null when it keeps none.</summary>
    [SupportedOSPlatform("windows")]
    public static string? DumpFolder(string exeName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(LocalDumpsKey);
        if (key is null)
            return null;

        using var app = key.OpenSubKey(exeName);
        var folder = app?.GetValue("DumpFolder") as string ?? key.GetValue("DumpFolder") as string;
        return Environment.ExpandEnvironmentVariables(folder ?? @"%LOCALAPPDATA%\CrashDumps");
    }

    private static string? FindDump(string? folder, string exeName, int processId, TimeSpan wait)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;

        var path = Path.Combine(folder, $"{exeName}.{processId}.dmp");
        var deadline = DateTime.UtcNow + wait;
        long last = -1;
        while (true)
        {
            var length = File.Exists(path) ? new FileInfo(path).Length : -1;
            if (length > 0 && length == last)
                return path;
            if (DateTime.UtcNow >= deadline)
                return length > 0 ? path : null;

            last = length;
            Thread.Sleep(500);
        }
    }

    private static string Report(
        int processId,
        DateTime? started,
        DateTime ended,
        uint code,
        CrashWatchOptions options,
        string? dump,
        BlackBox? box)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{AppInfo.Name} crash report");
        text.AppendLine(CultureInfo.InvariantCulture, $"Version {AppInfo.Version}, {AppInfo.Author}");
        var ran = started is { } start ? $", ran {Duration(ended - start)}" : "";
        var from = started is { } at ? $", started {Stamp(at)}" : "";
        text.AppendLine(CultureInfo.InvariantCulture, $"Process {processId}{from}, ended {Stamp(ended)}{ran}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Exit code {Hex(code)}: {Describe(code)}");
        if (box?.IsClean == true)
            text.AppendLine("The crash came after the app had begun a normal exit.");
        text.AppendLine(CultureInfo.InvariantCulture, $"Session log: {options.LogPath}");
        if (dump is not null)
        {
            var megabytes = new FileInfo(dump).Length / (1024d * 1024d);
            text.AppendLine(CultureInfo.InvariantCulture, $"Dump: {dump} ({megabytes:0.0} MB)");
            text.AppendLine("Read the dump with cdb, which ships inside WinDbg:");
            text.AppendLine(CultureInfo.InvariantCulture, $"  cdb -z \"{dump}\" -y \"srv*C:\\symbols*https://msdl.microsoft.com/download/symbols\" -c \"!analyze -v; .ecxr; kn 60; q\"");
        }
        else if (options.DumpFolder is null)
        {
            text.AppendLine("Dump: none. Windows keeps a dump only when this key exists. Run once as administrator:");
            text.AppendLine(CultureInfo.InvariantCulture, $"  reg add \"HKLM\\{LocalDumpsKey}\" /f");
        }
        else
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Dump: none found in {options.DumpFolder}");
        }

        text.AppendLine();
        var lines = box?.Read() ?? [];
        if (lines.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Last {lines.Count} events, oldest first. Frame and mesh lines are kept only here, not in the session log.");
        }
        else
        {
            text.AppendLine("The black box was not available. Last lines of the session log:");
            lines = Tail(options.LogPath, 80);
        }

        foreach (var line in lines)
            text.AppendLine(line);
        return text.ToString();
    }

    private static List<string> Tail(string path, int count)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new Queue<string>(count);
            while (reader.ReadLine() is { } line)
            {
                if (lines.Count == count)
                    lines.Dequeue();
                lines.Enqueue(line);
            }

            return [.. lines];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void AppendLine(string path, string kind, string message)
    {
        try
        {
            var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss.fff} {kind} {message}\n");
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            stream.Write(Encoding.UTF8.GetBytes(line));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Hex(uint code) => "0x" + code.ToString("X8", CultureInfo.InvariantCulture);

    private static string Stamp(DateTime time) => time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Duration(TimeSpan span) =>
        span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours} h {span.Minutes} min")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes} min {span.Seconds} s");
}
