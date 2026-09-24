using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;
using KubeNimbus.Core.Settings;

namespace KubeNimbus.App;

/// <summary>
/// <c>kubeNimbus --smoke-test</c>: start the app for real, wait until the main window
/// has opened and composited a frame, then exit 0. Anything else exits non-zero.
///
/// <para>
/// <b>Why this exists.</b> CI and the release workflow published NativeAOT binaries and
/// never ran them, so a published binary that died on startup was indistinguishable
/// from a healthy one — which is exactly how
/// <c>FileNotFoundException: The resource /Assets/app.ico could not be found</c>
/// reached three shipped release RIDs (see <see cref="WindowIcons"/>). A publish step
/// alone cannot catch that class of bug: it is a *runtime* failure of trimmed asset
/// resolution, and it happens before the first frame.
/// </para>
///
/// <para>
/// <b>Why in the app rather than outside it.</b> A GUI app never exits on its own, so
/// "run it and see" needs some way to end the process, and "a window appeared" needs
/// some way to be observed. Observing it from outside means a different tool per
/// platform — <c>xdotool</c> on X11, <c>MainWindowHandle</c> polling on Windows,
/// scripted Accessibility on macOS — three mechanisms, three sets of flakiness, and
/// the macOS one needs permissions a runner will not grant. One flag inside the app is
/// uniform across all four shipped RIDs and needs no packages.
/// </para>
///
/// <para>
/// <b>It does not create its own window.</b> It observes the one
/// <see cref="App.OnFrameworkInitializationCompleted"/> already built, through the real
/// platform backend, so the path under test is the path a user gets. On Linux CI runs
/// it under Xvfb for the same reason: the X11 backend, not the headless one.
/// </para>
///
/// <para>
/// Ordinary launches never touch any of this — <see cref="Attach"/> returns
/// immediately unless the flag was passed.
/// </para>
///
/// <para>
/// <b>Scenarios.</b> Plain <c>--smoke-test</c> launches with whatever workspace and
/// kubeconfig the machine has, which on a CI runner is none — so it never restored a
/// tab or connected to anything, and a NativeAOT hang on exactly that path shipped past
/// it (see <see cref="Kubeconfig.BuildClientConfigAsync"/>).
/// <c>--smoke-test=unreachable-cluster</c> seeds, in a temporary directory, a
/// kubeconfig whose server nothing listens on and a workspace that restores it, then
/// passes only once the restored tab has reported its failed connection and a frame
/// has been composited after that — a UI thread that is still alive once the connect
/// path has run to its end, not just at the first frame.
/// </para>
/// </summary>
internal static class SmokeTest
{
    /// <summary>The command-line flag that turns this on.</summary>
    private const string FlagName = "--smoke-test";

    /// <summary><c>--smoke-test=&lt;scenario&gt;</c> selects a seeded launch; see the class remarks.</summary>
    private const string ScenarioPrefix = FlagName + "=";

    private const string UnreachableClusterScenario = "unreachable-cluster";

    /// <summary>The context the unreachable-cluster scenario seeds and restores.</summary>
    private const string UnreachableContextName = "kubenimbus-smoke-unreachable";

    /// <summary>Grep-able success line, so a CI log says why the step passed.</summary>
    private const string SuccessMarker = "SMOKE-OK";

    /// <summary>Grep-able failure line, carrying the stage that was reached.</summary>
    private const string FailureMarker = "SMOKE-FAIL";

    private const string TimeoutVariable = "KUBENIMBUS_SMOKE_TIMEOUT_SECONDS";

    // Generous: a cold NativeAOT start on a loaded shared runner, plus Xvfb coming up,
    // is still comfortably inside this. The point of the watchdog is to turn a hang
    // into a failed step, not to measure startup.
    private const int DefaultTimeoutSeconds = 90;

    // Distinct codes so a red CI step says which way it failed without reading the log.
    private const int ExitNoWindow = 64;
    private const int ExitWindowNotShown = 65;
    private const int ExitStartupFailed = 66;
    private const int ExitTimedOut = 67;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static bool _requested;
    private static string? _scenario;
    private static string? _seedDirectory;
    private static Timer? _watchdog;
    private static string _stage = "process started";

    // Set on the UI thread by Complete, read on the main thread once the message loop
    // has ended — volatile so the read cannot be hoisted past the loop that publishes it.
    private static volatile bool _passed;

    /// <summary>
    /// Reads the flag out of the command line and returns the arguments Avalonia should
    /// see. Stripped rather than passed through: the desktop lifetime parses its own
    /// options out of <c>args</c>, and an argument it does not recognize is one more
    /// thing that could change behaviour between the smoke run and a real one.
    /// </summary>
    public static string[] Consume(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var flag = Array.Find(args, IsSmokeFlag);
        if (flag is null)
        {
            return args;
        }

        _requested = true;
        _scenario = flag.StartsWith(ScenarioPrefix, StringComparison.Ordinal) ? flag[ScenarioPrefix.Length..] : null;
        return [.. args.Where(a => !IsSmokeFlag(a))];
    }

    private static bool IsSmokeFlag(string arg) =>
        string.Equals(arg, FlagName, StringComparison.Ordinal) || arg.StartsWith(ScenarioPrefix, StringComparison.Ordinal);

    /// <summary>Whether <see cref="Consume"/> saw the flag.</summary>
    public static bool IsRequested => _requested;

    /// <summary>
    /// Runs the app under the smoke harness. Unlike a normal launch this catches a
    /// startup exception rather than letting it reach the runtime's unhandled handler,
    /// so the failing type and message land in the CI log in one piece instead of
    /// wherever the platform decides to put a crash.
    /// </summary>
    public static int Run(AppBuilder builder, string[] args)
    {
        ArgumentNullException.ThrowIfNull(builder);

        Report($"launch check starting (timeout {TimeoutSeconds}s{(_scenario is null ? "" : $", scenario {_scenario}")})");

        // Armed here rather than in Attach, which is the obvious place and is wrong:
        // Attach runs inside OnFrameworkInitializationCompleted, so a hang anywhere
        // earlier — platform detect, App.Initialize loading XAML, a static
        // constructor — would never start the watchdog and would hang the runner
        // until the job timeout. Started here it covers the whole process.
        StartWatchdog();

        try
        {
            switch (_scenario)
            {
                case null:
                    break;
                case UnreachableClusterScenario:
                    SeedUnreachableCluster();
                    break;
                default:
                    StopWatchdog();
                    Fail($"unknown scenario '{_scenario}' (known: {UnreachableClusterScenario})", ExitStartupFailed);
                    return ExitStartupFailed;
            }

            var code = builder.StartWithClassicDesktopLifetime(args);
            StopWatchdog();

            if (code != 0)
            {
                Fail("application exited non-zero", code);
                return code;
            }

            // A clean exit is not the same as a passed check. If the message loop ended
            // without Complete having run — the window closed itself, something called
            // Shutdown() — then nothing ever observed a rendered window, and returning
            // that 0 would be the check passing for the one reason it must not: the app
            // did not stay up long enough to show anything.
            if (!_passed)
            {
                Fail($"the app exited before its main window rendered (last stage: {_stage})", ExitWindowNotShown);
                return ExitWindowNotShown;
            }

            return 0;
        }
        catch (Exception e)
        {
            // Deliberately broad: the whole job here is to notice that *anything* went
            // wrong before the first frame, and a startup failure is by definition the
            // exception nobody predicted.
            Fail($"startup threw {e.GetType().FullName}: {e.Message}", ExitStartupFailed);
            Console.Error.WriteLine(e);
            Console.Error.Flush();
            return ExitStartupFailed;
        }
        finally
        {
            DeleteSeed();
        }
    }

    /// <summary>
    /// Writes the unreachable-cluster scenario's kubeconfig and workspace, and points
    /// the app at them. Both stores are redirected, so the run neither reads nor
    /// writes the files of whoever runs it — the plain check does read them, which is
    /// how it came to hang on a developer's machine and pass on a runner.
    /// </summary>
    private static void SeedUnreachableCluster()
    {
        _stage = "seeding the unreachable-cluster scenario";
        _seedDirectory = Path.Combine(Path.GetTempPath(), $"kubenimbus-smoke-{Environment.ProcessId}");
        Directory.CreateDirectory(_seedDirectory);

        // Port 1 on loopback: nothing listens there, so the connect is refused rather
        // than timing out, and the scenario ends in seconds on every platform. A token
        // user and skip-verify keep the kubeconfig to what the library needs to build
        // a client — no certificate is involved, and none is a credential.
        var kubeconfig = Path.Combine(_seedDirectory, "kubeconfig.yaml");
        File.WriteAllText(kubeconfig, $"""
            apiVersion: v1
            kind: Config
            clusters:
            - name: unreachable
              cluster:
                server: https://127.0.0.1:1
                insecure-skip-tls-verify: true
            users:
            - name: smoke
              user:
                token: smoke-test-not-a-credential
            contexts:
            - name: {UnreachableContextName}
              context:
                cluster: unreachable
                user: smoke
            current-context: {UnreachableContextName}
            """);

        Environment.SetEnvironmentVariable("KUBECONFIG", kubeconfig);
        WorkspaceStore.DirectoryOverride = _seedDirectory;
        AppSettingsStore.DirectoryOverride = _seedDirectory;
        WorkspaceStore.Save(new WorkspaceSettings(Theme: null, Tabs: [new TabSnapshot(UnreachableContextName, kubeconfig)]));
        Report($"seeded {kubeconfig} and a workspace restoring {UnreachableContextName}");
    }

    private static void DeleteSeed()
    {
        if (_seedDirectory is null)
        {
            return;
        }

        try
        {
            Directory.Delete(_seedDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a temp directory left behind is not worth failing the check.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Watches the main window the app just built. No-op unless the flag was passed —
    /// this is called unconditionally from
    /// <see cref="App.OnFrameworkInitializationCompleted"/>, which is what keeps the
    /// smoke path from being a second, separately-rotting startup path.
    /// </summary>
    public static void Attach(IClassicDesktopStyleApplicationLifetime desktop)
    {
        ArgumentNullException.ThrowIfNull(desktop);

        if (!_requested)
        {
            return;
        }

        _stage = "framework initialized, waiting for the main window to open";

        if (desktop.MainWindow is not { } window)
        {
            Fail("the desktop lifetime has no MainWindow", ExitNoWindow);
            Environment.Exit(ExitNoWindow);
            return;
        }

        window.Opened += (_, _) =>
        {
            Report("main window opened");
            _stage = "window opened, waiting for the first composited frame";

            // Opened fires before layout and render, so the size and visibility checks
            // below would read a window that is legitimately still 0x0. Requesting an
            // animation frame schedules a compositor tick and calls back after it, so
            // what the assertions see is a window that has actually been painted —
            // which is the claim this check is supposed to be making.
            window.RequestAnimationFrame(_ => Complete(desktop, window));
        };
    }

    private static void Complete(IClassicDesktopStyleApplicationLifetime desktop, Window window)
    {
        var size = window.ClientSize;
        if (!window.IsVisible || size.Width <= 0 || size.Height <= 0)
        {
            StopWatchdog();
            Fail(
                $"a frame was composited but the window is not shown (IsVisible={window.IsVisible}, ClientSize={size})",
                ExitWindowNotShown);
            desktop.Shutdown(ExitWindowNotShown);
            return;
        }

        if (_scenario == UnreachableClusterScenario)
        {
            // The watchdog stays armed: a UI thread that wedges on the connect path
            // after the first frame is precisely what this scenario is here to catch.
            Report("first frame rendered");
            WaitForRestoredTabToFail(desktop, window);
            return;
        }

        Pass(desktop, window, "");
    }

    /// <summary>
    /// Polls from a <see cref="DispatcherTimer"/>, on the UI thread, until the restored
    /// tab has reported its refused connection, then asks for one more composited
    /// frame. Polling on the UI thread is deliberate: if that thread is blocked the
    /// timer never ticks, and the watchdog reports the stage it was stuck at.
    /// </summary>
    private static void WaitForRestoredTabToFail(IClassicDesktopStyleApplicationLifetime desktop, Window window)
    {
        _stage = "first frame rendered, waiting for the restored tab to appear";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            var tab = (window.DataContext as MainWindowViewModel)?.Tabs
                .FirstOrDefault(t => t.Context.Name == UnreachableContextName);
            if (tab is null)
            {
                return;
            }

            _stage = $"restored tab present, waiting for its connection to fail (tab status: {tab.Status})";
            if (tab.IsConnecting || tab.IsConnected || !tab.Status.StartsWith("Connection failed", StringComparison.Ordinal))
            {
                return;
            }

            timer.Stop();
            Report($"restored tab reported: {tab.Status}");
            _stage = "connection failed, waiting for a frame composited after it";
            window.RequestAnimationFrame(_ => Pass(desktop, window, ", restored an unreachable cluster"));
        };
        timer.Start();
    }

    private static void Pass(IClassicDesktopStyleApplicationLifetime desktop, Window window, string detail)
    {
        StopWatchdog();

        var size = window.ClientSize;
        Console.Out.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{SuccessMarker} main window rendered at {size.Width:0}x{size.Height:0} after {Clock.ElapsedMilliseconds} ms{detail}"));
        Console.Out.Flush();

        _passed = true;
        desktop.Shutdown(0);
    }

    private static int TimeoutSeconds =>
        int.TryParse(
            Environment.GetEnvironmentVariable(TimeoutVariable),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var seconds) && seconds > 0
            ? seconds
            : DefaultTimeoutSeconds;

    /// <summary>
    /// A plain <see cref="Timer"/> on a pool thread, not a <c>DispatcherTimer</c>: the
    /// failure this has to survive is a wedged UI thread, and a dispatcher timer would
    /// be wedged with it. For the same reason it ends the process with
    /// <see cref="Environment.Exit(int)"/> rather than asking the lifetime to shut
    /// down — a hung process eating the job's whole timeout is the outcome being
    /// avoided.
    /// </summary>
    private static void StartWatchdog() =>
        _watchdog = new Timer(
            _ =>
            {
                Fail($"no window after {TimeoutSeconds}s (last stage: {_stage})", ExitTimedOut);
                Environment.Exit(ExitTimedOut);
            },
            state: null,
            dueTime: TimeSpan.FromSeconds(TimeoutSeconds),
            period: Timeout.InfiniteTimeSpan);

    private static void StopWatchdog()
    {
        _watchdog?.Dispose();
        _watchdog = null;
    }

    private static void Report(string message)
    {
        Console.Out.WriteLine($"[smoke {Clock.ElapsedMilliseconds} ms] {message}");
        Console.Out.Flush();
    }

    private static void Fail(string message, int code)
    {
        Console.Error.WriteLine($"{FailureMarker} ({code}) {message}");
        Console.Error.Flush();
    }
}
