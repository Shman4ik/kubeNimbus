using Avalonia;
using KubeNimbus.Core;
using KubeNimbus.Core.Settings;
using Nimbus.Ui.Fonts;

namespace KubeNimbus.App;

internal static class Program
{
    // NativeAOT/trimming note: keep initialization inside BuildAvaloniaApp and
    // avoid any reflection-based startup so the published binary stays AOT-clean.
    [STAThread]
    public static int Main(string[] args)
    {
        // `--smoke-test` is CI's launch check: same startup, same window, but it exits
        // once the window has rendered instead of waiting to be closed. See SmokeTest
        // for why the check lives in the app rather than outside it. A launch without
        // the flag takes exactly the path it always did.
        args = SmokeTest.Consume(args);
        ApplyIsolatedProfile();

        if (SmokeTest.IsRequested)
        {
            return SmokeTest.Run(BuildAvaloniaApp(), args);
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// The environment variable that runs the app on an isolated profile.
    /// </summary>
    internal const string ProfileVariable = "KUBENIMBUS_PROFILE_DIR";

    /// <summary>
    /// With <c>KUBENIMBUS_PROFILE_DIR</c> set, the app reads and writes its settings and
    /// workspace in that directory, and searches <em>only</em> the files named in
    /// <c>$KUBECONFIG</c> — never <c>~/.kube/config</c>. It exists for agents driving a
    /// running build (<c>scripts/qa-app.ps1</c>, the <c>kn-qa</c> agent): without it a
    /// launched Debug build restores the developer's own tabs and lists their real
    /// contexts, and an automated check that presses Delete or Drain would be pressing it
    /// on whatever cluster the developer last had open. Nothing here can widen what the
    /// app reaches; it only narrows it.
    /// </summary>
    private static void ApplyIsolatedProfile()
    {
        var directory = Environment.GetEnvironmentVariable(ProfileVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        AppSettingsStore.DirectoryOverride = directory;
        WorkspaceStore.DirectoryOverride = directory;
        Kubeconfig.EnvironmentSearchOverride =
            (Environment.GetEnvironmentVariable("KUBECONFIG") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .WithNimbusFonts()
            .LogToTrace();
#if DEBUG
        builder = builder.WithDeveloperTools();
#endif
        return builder;
    }
}
