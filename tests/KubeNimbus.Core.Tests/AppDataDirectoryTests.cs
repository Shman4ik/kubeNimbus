using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// ENG-39: where the app's own files go must never be a relative path. With a fresh
/// Linux HOME, <c>GetFolderPath</c> returned "" and the discovery cache was written to
/// <c>./kubeNimbus/discovery</c> of whatever directory the app was started from.
/// </summary>
public class AppDataDirectoryTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "kubenimbus-appdata-test");
    private static readonly string Temp = Path.Combine(Root, "tmp");

    [Test]
    public async Task The_platform_answer_wins_when_it_is_absolute()
    {
        var special = Path.Combine(Root, "special");

        await Assert.That(AppDataDirectory.Choose(special, Path.Combine(Root, "xdg"), Root, ".config", Temp)).IsEqualTo(special);
    }

    [Test]
    public async Task An_empty_platform_answer_falls_back_to_xdg_then_home_then_temp()
    {
        var xdg = Path.Combine(Root, "xdg");

        await Assert.That(AppDataDirectory.Choose("", xdg, Root, ".config", Temp)).IsEqualTo(xdg);
        await Assert.That(AppDataDirectory.Choose("", null, Root, ".config", Temp)).IsEqualTo(Path.Combine(Root, ".config"));
        await Assert.That(AppDataDirectory.Choose("", null, "", ".config", Temp)).IsEqualTo(Path.Combine(Temp, "kubeNimbus-fallback"));
    }

    [Test]
    public async Task A_relative_value_anywhere_is_skipped_never_used()
    {
        // The XDG spec: a relative $XDG_CONFIG_HOME is invalid and must be ignored. A
        // relative HOME is no better. Either would put files in the current directory.
        var chosen = AppDataDirectory.Choose("kubeNimbus", "relative/xdg", "relative-home", ".config", Temp);

        await Assert.That(chosen).IsEqualTo(Path.Combine(Temp, "kubeNimbus-fallback"));
        await Assert.That(Path.IsPathFullyQualified(chosen)).IsTrue();
    }

    [Test]
    public async Task Every_store_resolves_to_an_absolute_path()
    {
        await Assert.That(Path.IsPathFullyQualified(AppDataDirectory.Roaming)).IsTrue();
        await Assert.That(Path.IsPathFullyQualified(AppDataDirectory.Local)).IsTrue();
        await Assert.That(Path.IsPathFullyQualified(TerminalLauncher.StateDirectory) || TerminalLauncher.DirectoryOverride is not null).IsTrue();
    }
}
