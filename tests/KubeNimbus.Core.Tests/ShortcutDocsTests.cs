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
    // repository's own marker rather than hardcoding a relative hop count.
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
               ?? throw new InvalidOperationException("Repository root not found from " + AppContext.BaseDirectory);
    }
}
