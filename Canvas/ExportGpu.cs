using System.Runtime.InteropServices;
using SkiaSharp;

namespace Galimapia.Canvas;

/// <summary>
/// A hidden OpenGL context for export. One context, one thread. If the machine has no
/// usable video driver, <see cref="TryOpen"/> returns null and the export stays on the CPU.
/// </summary>
sealed class ExportGpu : IDisposable
{
    private readonly IntPtr _window;
    private readonly IntPtr _dc;
    private readonly IntPtr _context;
    private bool _disposed;

    public GRContext Context { get; }

    private ExportGpu(IntPtr window, IntPtr dc, IntPtr context, GRContext gpu)
    {
        _window = window;
        _dc = dc;
        _context = context;
        Context = gpu;
    }

    public static ExportGpu? TryOpen()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            return Open();
        }
        catch (Exception ex)
        {
            SessionLog.Action("export video card failed: " + ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Context.Dispose();
        wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        wglDeleteContext(_context);
        ReleaseDC(_window, _dc);
        DestroyWindow(_window);
    }

    private static ExportGpu? Open()
    {
        var window = CreateWindowExW(0, "Static", string.Empty, unchecked((int)0x80000000), 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (window == IntPtr.Zero)
            return null;

        var dc = GetDC(window);
        var format = new PixelFormat
        {
            Size = (ushort)Marshal.SizeOf<PixelFormat>(),
            Version = 1,
            Flags = 0x4 | 0x20,
            ColorBits = 32,
            DepthBits = 24,
        };
        var chosen = ChoosePixelFormat(dc, ref format);
        if (chosen == 0 || !SetPixelFormat(dc, chosen, ref format))
        {
            ReleaseDC(window, dc);
            DestroyWindow(window);
            return null;
        }

        var context = wglCreateContext(dc);
        if (context == IntPtr.Zero || !wglMakeCurrent(dc, context))
        {
            if (context != IntPtr.Zero)
                wglDeleteContext(context);
            ReleaseDC(window, dc);
            DestroyWindow(window);
            return null;
        }

        using var gl = GRGlInterface.Create();
        var gpu = gl is null ? null : GRContext.CreateGl(gl);
        if (gpu is null)
        {
            wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            wglDeleteContext(context);
            ReleaseDC(window, dc);
            DestroyWindow(window);
            return null;
        }

        return new ExportGpu(window, dc, context, gpu);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int style, string className, string windowName, int windowStyle, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? module);

    [DllImport("gdi32.dll")]
    private static extern int ChoosePixelFormat(IntPtr dc, ref PixelFormat format);

    [DllImport("gdi32.dll")]
    private static extern bool SetPixelFormat(IntPtr dc, int format, ref PixelFormat descriptor);

    [DllImport("opengl32.dll")]
    private static extern IntPtr wglCreateContext(IntPtr dc);

    [DllImport("opengl32.dll")]
    private static extern bool wglMakeCurrent(IntPtr dc, IntPtr context);

    [DllImport("opengl32.dll")]
    private static extern bool wglDeleteContext(IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelFormat
    {
        public ushort Size;
        public ushort Version;
        public uint Flags;
        public byte PixelType;
        public byte ColorBits;
        public byte RedBits;
        public byte RedShift;
        public byte GreenBits;
        public byte GreenShift;
        public byte BlueBits;
        public byte BlueShift;
        public byte AlphaBits;
        public byte AlphaShift;
        public byte AccumBits;
        public byte AccumRedBits;
        public byte AccumGreenBits;
        public byte AccumBlueBits;
        public byte AccumAlphaBits;
        public byte DepthBits;
        public byte StencilBits;
        public byte AuxBuffers;
        public byte LayerType;
        public byte Reserved;
        public uint LayerMask;
        public uint VisibleMask;
        public uint DamageMask;
    }
}
