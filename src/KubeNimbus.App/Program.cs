using Avalonia;

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

        if (SmokeTest.IsRequested)
        {
            return SmokeTest.Run(BuildAvaloniaApp(), args);
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
#if DEBUG
        builder = builder.WithDeveloperTools();
#endif
        return builder;
    }
}
