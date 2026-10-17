using System.Runtime.InteropServices;
using Avalonia.Controls.ApplicationLifetimes;

namespace KubeNimbus.App.Platform;

/// <summary>
/// macOS only: end the process ourselves the moment Avalonia's shutdown has run, rather
/// than letting AppKit (or <c>Main</c> returning) finish the exit. Ported from pgNimbus
/// (<c>PgNimbus.App.MacShutdown</c>), where a shipped release died the same way.
///
/// <para>
/// A quit — ⌘Q, the app menu's Quit, a logout, or <c>Main</c> returning after the smoke
/// test — ends in C's <c>exit()</c>, and that runs libAvaloniaNative's C++ static
/// destructors. One of them releases a <c>ComPtr&lt;IAvnDispatcher&gt;</c> whose vtable is a
/// managed MicroCom proxy, which is a reverse P/Invoke. By then NativeAOT has torn down the
/// main thread's runtime state, and managed code on a detached thread is a deliberate
/// fail-fast, not an exception any handler can catch. Recorded on a Mac for kubeNimbus too
/// (2026-10-17, an origin/main <c>--smoke-test</c> run): <c>EXC_CRASH SIGABRT</c>,
/// <c>exit → __cxa_finalize_ranges → ComPtr&lt;IAvnDispatcher&gt;::~ComPtr →
/// MicroComVtblBase.Release → Thread::ReversePInvokeAttachOrTrapThread → RhFailFast →
/// abort</c>. Every frame is Avalonia's or the runtime's (AvaloniaUI/Avalonia#12459), so the
/// only move available here is never to reach <c>__cxa_finalize</c>: <c>_exit(2)</c> ends the
/// process without running one atexit handler or static destructor.
/// </para>
///
/// <para>
/// It hangs off the lifetime's <c>Exit</c> event, which Avalonia raises after every window
/// has closed and only when the shutdown really goes through, so the workspace and settings
/// this app writes on close are already on disk. Two consequences: an <c>Exit</c> handler
/// registered after this one never runs, and neither does anything <c>Program.Main</c> would
/// have done after <c>StartWithClassicDesktopLifetime</c> returned. The smoke test's verdict
/// survives it, because the smoke test hands its own verdict in (<c>SmokeTest.Verdict</c>) and
/// its watchdog ends the process through <see cref="ExitNow"/> as well.
/// </para>
/// </summary>
internal static class MacShutdown
{
    /// <summary>
    /// Hooks the lifetime so a completed shutdown ends the process directly. No-op off
    /// macOS: Windows and Linux exit through this teardown cleanly.
    /// <paramref name="verdict"/> turns the lifetime's exit code into the process's; the smoke
    /// test passes its own (<c>SmokeTest.Verdict</c>), because once this handler runs the code
    /// after <c>StartWithClassicDesktopLifetime</c> never does.
    /// </summary>
    public static void ExitProcessOnShutdown(IClassicDesktopStyleApplicationLifetime desktop, Func<int, int>? verdict = null)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        desktop.Exit += (_, e) => ExitNow(verdict is null ? e.ApplicationExitCode : verdict(e.ApplicationExitCode));
    }

    /// <summary>
    /// The end of <c>Program.Main</c>: on macOS the process ends here with
    /// <paramref name="code"/> rather than returning into <c>exit()</c>; elsewhere the code is
    /// returned as it was.
    /// </summary>
    public static int Return(int code)
    {
        if (OperatingSystem.IsMacOS())
        {
            ExitNow(code);
        }

        return code;
    }

    /// <summary>
    /// Flushes the console and ends the process with <c>_exit</c>. Also what the smoke test's
    /// watchdog uses on macOS, for the same reason.
    /// </summary>
    internal static void ExitNow(int exitCode)
    {
        try
        {
            // _exit runs no atexit handler, so nothing downstream flushes these; the smoke
            // test's SMOKE-OK/SMOKE-FAIL line is read off stdout by people reading a red job.
            Console.Out.Flush();
            Console.Error.Flush();
        }
        catch
        {
            // A console that cannot be flushed is not a reason to stay alive.
        }

        try
        {
            Exit(exitCode);
        }
        catch (Exception)
        {
            // _exit did not resolve. Returning leaves the old behaviour (the process aborts on
            // the way out), which is still better than throwing out of a shutdown handler and
            // unwinding into Objective-C.
        }
    }

    /// <summary>
    /// <c>_exit(2)</c>: ends the process at once, skipping atexit handlers, C++ static
    /// destructors and stdio flushing. Deliberately not <c>Environment.Exit</c>, which runs
    /// the same <c>exit()</c> teardown this exists to avoid.
    /// </summary>
    [DllImport("libc", EntryPoint = "_exit")]
    private static extern void Exit(int status);
}
