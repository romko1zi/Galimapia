using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Galimapia.Canvas;

/// <summary>
/// One text file per launch, plus the <see cref="BlackBox"/> that the crash watcher reads after a
/// native crash. File lines go straight to the operating system without a buffer, so a crash that
/// kills the process still leaves them. Trace lines go only to the black box.
/// Interpolated messages are formatted with the invariant culture, so 27514.2 never reads as 27514,2.
/// </summary>
public static class SessionLog
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Seen = new(StringComparer.Ordinal);
    private static FileStream? _stream;
    private static BlackBox? _box;
    private static long _lastMove;

    public static string? FilePath { get; private set; }

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppInfo.Name,
        "logs");

    internal static bool IsRecording => _stream is not null || _box is not null;

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            FilePath = Path.Combine(Folder, "galimapia-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
            _stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0);
            var box = OpenBlackBox();
            Write("session", AppInfo.Name + " " + AppInfo.Version + " " + AppInfo.Author);
            Write("machine", Machine());
            if (box is { } opened)
                StartWatcher(FilePath, opened);
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                Fault("unhandled", args.ExceptionObject as Exception);
                if (args.ExceptionObject is not Exception)
                    Fault(args.ExceptionObject?.ToString() ?? "unhandled non-exception");
            };
            TaskScheduler.UnobservedTaskException += (_, args) => Fault("unobserved task", args.Exception);
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                Action("process exit");
                _box?.MarkClean();
            };
            Console.WriteLine("Galimapia log: " + FilePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Galimapia log failed: " + ex.Message);
        }
    }

    public static void Action(string message) => Write("action", message);

    public static void Action(ref LogText message)
    {
        if (message.Enabled)
            Action(message.ToStringAndClear());
    }

    public static void Move(string message)
    {
        var now = Environment.TickCount64;
        if (now - _lastMove < 250)
            return;

        _lastMove = now;
        Write("move", message);
    }

    public static void Move(ref LogText message)
    {
        if (message.Enabled)
            Move(message.ToStringAndClear());
    }

    /// <summary>Black box only. Cheap enough for every frame on the render thread.</summary>
    public static void Trace(ref LogText message)
    {
        if (message.Enabled)
            Write("trace", message.ToStringAndClear(), toFile: false);
    }

    public static void Once(string key, string message)
    {
        lock (Gate)
        {
            if (!Seen.Add(key))
                return;
        }

        Write("once", message);
    }

    public static void Fault(string message, Exception? exception = null)
    {
        Write("fault", message);
        if (exception is not null)
        {
            foreach (var line in exception.ToString().Split('\n'))
                Write("fault", line.TrimEnd('\r'));
        }
        Write("machine", Machine());
    }

    public static void Fault(ref LogText message, Exception? exception = null)
    {
        if (message.Enabled)
            Fault(message.ToStringAndClear(), exception);
    }

    public static string Machine()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var gc = GC.GetGCMemoryInfo();
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"os={Environment.OSVersion} cores={Environment.ProcessorCount} pid={process.Id} ");
            text.Append(CultureInfo.InvariantCulture, $"ws={Megabytes(process.WorkingSet64)}MB private={Megabytes(process.PrivateMemorySize64)}MB ");
            text.Append(CultureInfo.InvariantCulture, $"gc={Megabytes(gc.HeapSizeBytes)}MB handles={process.HandleCount} ");
            if (TryMemory(out var status))
            {
                text.Append(CultureInfo.InvariantCulture, $"phys={Megabytes((long)status.TotalPhys)}MB avail={Megabytes((long)status.AvailPhys)}MB load={status.MemoryLoad}% ");
                text.Append(CultureInfo.InvariantCulture, $"page={Megabytes((long)status.TotalPage)}MB availPage={Megabytes((long)status.AvailPage)}MB");
            }

            return text.ToString();
        }
        catch (Exception ex)
        {
            return "machine snapshot failed: " + ex.Message;
        }
    }

    private static (int ProcessId, long StartTicks, string Name)? OpenBlackBox()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            using var self = Process.GetCurrentProcess();
            var startTicks = self.StartTime.ToUniversalTime().Ticks;
            var name = BlackBox.NameFor(self.Id, startTicks);
            _box = BlackBox.Create(name);
            return (self.Id, startTicks, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Write("watch", "black box failed: " + ex.Message);
            return null;
        }
    }

    private static void StartWatcher(string logPath, (int ProcessId, long StartTicks, string Name) box)
    {
        try
        {
            var watcher = CrashWatch.Launch(box.ProcessId, box.StartTicks, logPath, box.Name);
            Write("watch", string.Create(CultureInfo.InvariantCulture, $"crash watcher pid={watcher}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Write("watch", "crash watcher failed: " + ex.Message);
        }
    }

    private static void Write(string kind, string message, bool toFile = true)
    {
        if (!IsRecording)
            return;

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.Now:HH:mm:ss.fff} {kind} {message.Replace('\r', ' ').Replace('\n', ' ')}");
        _box?.Write(line);
        var stream = _stream;
        if (!toFile || stream is null)
            return;

        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (Gate)
            stream.Write(bytes);
    }

    private static int Megabytes(long bytes) => (int)(bytes / (1024 * 1024));

    private static bool TryMemory(out MemoryNumbers numbers)
    {
        numbers = default;
        if (!OperatingSystem.IsWindows())
            return false;

        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
            return false;

        numbers = new MemoryNumbers(status.MemoryLoad, status.TotalPhys, status.AvailPhys, status.TotalPageFile, status.AvailPageFile);
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    private readonly record struct MemoryNumbers(uint MemoryLoad, ulong TotalPhys, ulong AvailPhys, ulong TotalPage, ulong AvailPage);
}

/// <summary>
/// Formats a log message with the invariant culture, and skips the formatting when nothing is recording.
/// </summary>
[InterpolatedStringHandler]
public ref struct LogText
{
    private DefaultInterpolatedStringHandler _text;

    public LogText(int literalLength, int formattedCount, out bool enabled)
    {
        Enabled = enabled = SessionLog.IsRecording;
        _text = enabled
            ? new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.InvariantCulture)
            : default;
    }

    public bool Enabled { get; }

    public void AppendLiteral(string value) => _text.AppendLiteral(value);

    public void AppendFormatted<T>(T value) => _text.AppendFormatted(value);

    public void AppendFormatted<T>(T value, string? format) => _text.AppendFormatted(value, format);

    public string ToStringAndClear() => _text.ToStringAndClear();
}
