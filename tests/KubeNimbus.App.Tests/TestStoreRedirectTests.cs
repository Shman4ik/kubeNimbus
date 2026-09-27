using KubeNimbus.Core.Settings;

namespace KubeNimbus.App.Tests;

/// <summary>
/// ENG-37: what <see cref="TestObjects.RedirectStores"/> actually guarantees, pinned so its
/// doc comment cannot drift back into promising more. The bug it had: <c>App</c> held its
/// settings store in a static field, which fixed the file path the first time <c>App</c>
/// was touched, so every later redirect moved the workspace and left <c>settings.json</c>
/// where it was.
///
/// <para>
/// <c>[NotInParallel]</c>: the redirect is a process-wide static, and these tests write
/// through it and read back.
/// </para>
/// </summary>
[NotInParallel]
public class TestStoreRedirectTests
{
    [Test]
    public async Task A_redirect_moves_Apps_settings_to_a_fresh_file()
    {
        TestObjects.RedirectStores();
        var first = AppSettingsStore.DefaultPath;
        App.Update(s => s with { OpenLogsMaximized = true });

        // Written where the redirect points, and read back from there.
        await Assert.That(File.Exists(first)).IsTrue();
        await Assert.That(App.LoadSettings().OpenLogsMaximized).IsTrue();

        TestObjects.RedirectStores();

        // A new directory, and App follows it: the preference just written is not what the
        // next test reads. With the path fixed at type initialization this read came back
        // true from the first file.
        await Assert.That(AppSettingsStore.DefaultPath).IsNotEqualTo(first);
        await Assert.That(App.LoadSettings().OpenLogsMaximized).IsFalse();

        App.Update(s => s with { OpenLogsMaximized = false });
        await Assert.That(File.Exists(AppSettingsStore.DefaultPath)).IsTrue();
    }

    /// <summary>
    /// The half that holds even for a test that never calls the helper: the assembly's
    /// module initializer has already pointed both stores into the temp directory.
    /// </summary>
    [Test]
    public async Task The_stores_never_point_at_the_developers_own_files()
    {
        var temp = Path.GetFullPath(Path.GetTempPath());

        await Assert.That(AppSettingsStore.DirectoryOverride).IsNotNull();
        await Assert.That(WorkspaceStore.DirectoryOverride).IsNotNull();
        await Assert.That(Path.GetFullPath(AppSettingsStore.DirectoryOverride!)).StartsWith(temp);
        await Assert.That(Path.GetFullPath(WorkspaceStore.DirectoryOverride!)).StartsWith(temp);
    }
}
