using Avalonia;

namespace KubeNimbus.App;

internal static class Program
{
    // NativeAOT/trimming note: keep initialization inside BuildAvaloniaApp and
    // avoid any reflection-based startup so the published binary stays AOT-clean.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

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
