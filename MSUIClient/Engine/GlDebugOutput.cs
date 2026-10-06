using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace MSUIClient.Engine;

/// <summary>
/// MSUI_GL_DEBUG=1: driver debug output (KHR_debug, synchronous) for GL ERRORS only, each distinct message
/// logged once with the managed call stack that raised it. Diagnostic only - off by default. Exists because
/// a frame-burst probe showed GL_INVALID_OPERATION on every frame after a Real Portals world promotion
/// (WoW Karting, 2026-10-04) and glGetError alone cannot name the call.
/// </summary>
public static class GlDebugOutput
{
    private static DebugProc? _proc;   // kept alive: the driver holds the pointer
    private static readonly ConcurrentDictionary<string, int> Seen = new();

    public static void EnableIfRequested(GL gl)
    {
        if (Environment.GetEnvironmentVariable("MSUI_GL_DEBUG") != "1") return;
        _proc = OnMessage;
        gl.Enable(EnableCap.DebugOutput);
        gl.Enable(EnableCap.DebugOutputSynchronous);
        gl.DebugMessageCallback(_proc, IntPtr.Zero);
        Console.WriteLine("[gl-debug] synchronous debug output on (errors only)");
    }

    private static void OnMessage(GLEnum source, GLEnum type, int id, GLEnum severity, int length, IntPtr message, IntPtr user)
    {
        if (type != GLEnum.DebugTypeError) return;
        string text = Marshal.PtrToStringAnsi(message, length) ?? "";
        int count = Seen.AddOrUpdate(text, 1, (_, c) => c + 1);
        if (count == 1 || count == 100 || count == 1000)
        {
            string stack = string.Join(" <- ", Environment.StackTrace.Split('\n')
                .Select(l => l.Trim()).Where(l => l.StartsWith("at MSUIClient", StringComparison.Ordinal))
                .Take(8).Select(l => l[3..].Split(" in ")[0]));
            Console.WriteLine($"[gl-debug] x{count} {text} :: {stack}");
        }
    }
}
