using System.Text.RegularExpressions;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #298, the #288 defect as a source check. A Button inside a flyout that has both a
/// <c>Command</c> and a <c>Click</c> handler runs the handler first and reads its Command after
/// the handler returns. A handler that hides the flyout synchronously takes the flyout's content
/// out of the logical tree, so the Button's <c>{Binding …Command}</c> loses its DataContext and
/// the command never runs: the log pane's ⋯ menu Copy wrote nothing to the clipboard, and its
/// Clear and Save did nothing. The fix is to post the hide (<c>Dispatcher.UIThread.Post</c>), which
/// runs it after the command. This test reads every view's XAML for an element with both
/// attributes and fails when its handler hides a flyout or closes a popup outside a posted call.
/// <c>ClipboardChecks.LogMenuCopy</c> in the harness is the rendered half for the log menus.
/// </summary>
public class FlyoutHideOrderTests
{
    [Test]
    public async Task No_click_handler_beside_a_command_hides_its_flyout_before_the_command_runs()
    {
        var views = Path.Combine(RepositoryRoot(), "src", "KubeNimbus.App");
        var checkedHandlers = new List<string>();
        var offenders = new List<string>();
        foreach (var xaml in Directory.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories))
        {
            var codeBehind = xaml + ".cs";
            var handlers = HandlersBesideACommand(await File.ReadAllTextAsync(xaml)).ToList();
            if (handlers.Count == 0)
            {
                continue;
            }

            var code = File.Exists(codeBehind) ? await File.ReadAllTextAsync(codeBehind) : "";
            foreach (var handler in handlers)
            {
                var body = MethodBody(code, handler)
                    ?? throw new InvalidOperationException($"{Path.GetFileName(xaml)} names Click=\"{handler}\", which is not in its code-behind.");
                checkedHandlers.Add($"{Path.GetFileName(xaml)}:{handler}");
                if (HidesSynchronously(body))
                {
                    offenders.Add($"{Path.GetFileName(codeBehind)}: {handler}");
                }
            }
        }

        // Not vacuous: the log menus' Copy, Clear and Save in both log views are such handlers.
        await Assert.That(checkedHandlers.Count(h => h.EndsWith(":OnLogMenuActionClick", StringComparison.Ordinal))).IsGreaterThanOrEqualTo(6);
        await Assert.That(offenders.Distinct().ToList()).IsEmpty();
    }

    [Test]
    public async Task The_check_flags_the_288_shape_and_passes_its_fix()
    {
        const string xaml = """
            <Button Name="MenuCopy" Classes="linkRow" Command="{Binding CopyLogsCommand}"
                    Click="OnLogMenuActionClick" Content="Copy" />
            <Button Click="OnPlainClick" Content="No command" />
            """;
        await Assert.That(HandlersBesideACommand(xaml).ToList()).IsEquivalentTo(["OnLogMenuActionClick"]);

        const string broken = """
            private void OnLogMenuActionClick(object? sender, RoutedEventArgs e) => LogMenuButton.Flyout?.Hide();
            """;
        const string brokenBlock = """
            private void OnLogMenuActionClick(object? sender, RoutedEventArgs e)
            {
                Log("menu");
                LogMenuButton.Flyout?.Hide();
            }
            """;
        const string closed = """
            private void OnLogMenuActionClick(object? sender, RoutedEventArgs e) { Popup.IsOpen = false; }
            """;
        const string posted = """
            private void OnLogMenuActionClick(object? sender, RoutedEventArgs e) =>
                Dispatcher.UIThread.Post(() => LogMenuButton.Flyout?.Hide());
            """;
        await Assert.That(HidesSynchronously(MethodBody(broken, "OnLogMenuActionClick")!)).IsTrue();
        await Assert.That(HidesSynchronously(MethodBody(brokenBlock, "OnLogMenuActionClick")!)).IsTrue();
        await Assert.That(HidesSynchronously(MethodBody(closed, "OnLogMenuActionClick")!)).IsTrue();
        await Assert.That(HidesSynchronously(MethodBody(posted, "OnLogMenuActionClick")!)).IsFalse();
    }

    /// <summary>The Click handlers named by an element that also has a Command.</summary>
    private static IEnumerable<string> HandlersBesideACommand(string xaml)
    {
        // An element's opening tag, attributes across lines included. XAML attribute values in
        // this repo never contain '>'.
        foreach (Match tag in Regex.Matches(xaml, @"<[A-Za-z][\w:.]*\s[^>]*>"))
        {
            var text = tag.Value;
            if (Regex.IsMatch(text, @"\sCommand\s*=") && Regex.Match(text, @"\sClick\s*=\s*""(\w+)""") is { Success: true } click)
            {
                yield return click.Groups[1].Value;
            }
        }
    }

    /// <summary>The body of method <paramref name="name"/>: an expression body to its ';', or a block.</summary>
    private static string? MethodBody(string code, string name)
    {
        var declaration = Regex.Match(code, @"\bvoid\s+" + Regex.Escape(name) + @"\s*\(");
        if (!declaration.Success)
        {
            return null;
        }

        var at = SkipBalanced(code, declaration.Index + declaration.Length - 1, '(', ')');
        while (at < code.Length && char.IsWhiteSpace(code[at]))
        {
            at++;
        }

        if (code.AsSpan(at).StartsWith("=>"))
        {
            var end = at;
            var depth = 0;
            for (; end < code.Length; end++)
            {
                depth += code[end] switch { '(' or '{' => 1, ')' or '}' => -1, _ => 0 };
                if (depth == 0 && code[end] == ';')
                {
                    break;
                }
            }

            return code[at..end];
        }

        return code[at] == '{' ? code[at..SkipBalanced(code, at, '{', '}')] : null;
    }

    /// <summary>
    /// A flyout hidden or a popup closed in <paramref name="body"/> other than inside a posted call,
    /// which runs after the Button has run its command.
    /// </summary>
    private static bool HidesSynchronously(string body)
    {
        var text = body;
        Match post;
        while ((post = Regex.Match(text, @"\b(Post|InvokeAsync)\s*\(")).Success)
        {
            var end = SkipBalanced(text, post.Index + post.Length - 1, '(', ')');
            text = text[..post.Index] + text[end..];
        }

        return Regex.IsMatch(text, @"\.Hide\s*\(\s*\)|\bIsOpen\s*=\s*false");
    }

    /// <summary>The index just after the bracket that closes the one at <paramref name="open"/>.</summary>
    private static int SkipBalanced(string text, int open, char opening, char closing)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == opening)
            {
                depth++;
            }
            else if (text[i] == closing && --depth == 0)
            {
                return i + 1;
            }
        }

        return text.Length;
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "KubeNimbus.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"No KubeNimbus.slnx above {AppContext.BaseDirectory}.");
    }
}
