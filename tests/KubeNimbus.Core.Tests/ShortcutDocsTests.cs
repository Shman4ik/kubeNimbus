using KubeNimbus.Core.Commands;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Golden-file check for the published shortcut reference: the checked-in page must be
/// exactly what <see cref="ShortcutDocs.ToMarkdown"/> produces today, so the docs
/// cannot quietly fall behind the app the way a hand-written table would. Set
/// <c>KUBENIMBUS_UPDATE_DOCS=1</c> to rewrite it after changing the catalog.
/// </summary>
public class ShortcutDocsTests
{
    [Test]
    public async Task GeneratedPageMatchesTheCheckedInFile()
    {
        var expected = ShortcutDocs.ToMarkdown();
        var path = Path.Combine(RepositoryRoot(), ShortcutDocs.RelativePath);

        if (Environment.GetEnvironmentVariable("KUBENIMBUS_UPDATE_DOCS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, expected);
            return;
        }

        await Assert.That(File.Exists(path)).IsTrue();

        // Normalize line endings before comparing, so a checkout with CRLF doesn't fail
        // the whole file over something git did.
        var actual = (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n");
        await Assert.That(actual).IsEqualTo(expected.ReplaceLineEndings("\n"));
    }

    [Test]
    public async Task PageCoversEveryDocumentedShortcut()
    {
        var markdown = ShortcutDocs.ToMarkdown();

        foreach (var descriptor in CommandCatalog.On(CommandSurface.CheatSheet))
        {
            await Assert.That(markdown).Contains(descriptor.DisplayName);
        }
    }

    // The tests run from bin/<config>/net10.0; walk up to the directory that holds the
    // repository's own marker rather than hardcoding a relative hop count. Then from
    // $KUBENIMBUS_REPO_ROOT, for a build made with --artifacts-path outside the repository
    // (the test platform sets the working directory to the binary's folder).
    private static string RepositoryRoot()
    {
        foreach (var start in (string[])[AppContext.BaseDirectory, Environment.GetEnvironmentVariable("KUBENIMBUS_REPO_ROOT") ?? ""])
        {
            for (var dir = start.Length == 0 ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
                {
                    return dir.FullName;
                }
            }
        }

        throw new InvalidOperationException(
            $"Repository root not found from {AppContext.BaseDirectory}; set KUBENIMBUS_REPO_ROOT for a build outside the repository");
    }
}
