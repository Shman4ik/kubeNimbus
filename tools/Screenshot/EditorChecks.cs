using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Views;

namespace KubeNimbus.Screenshot;

/// <summary>
/// The AvaloniaEdit editors, read the way they are drawn. Left at its defaults AvaloniaEdit
/// draws every URL and e-mail address as a link, in pure Blue over the YAML highlighter's
/// colours, and a Ctrl+click opens it; <c>EditorDefaults</c> turns that off. What an editor
/// drew exists only in its visual lines, which only a laid-out window has, so this is a
/// <c>ux-</c> check and not a test in <c>KubeNimbus.App.Tests</c>. pgNimbus's
/// <c>JsonInspectorTests</c> reads its editors the same way.
/// </summary>
internal static class EditorChecks
{
    // An annotation link, a maintainer's address and an image reference: what a manifest
    // puts in front of AvaloniaEdit's link generators. The first two match them.
    private const string Url = "https://grafana.example.com/d/checkout";
    private const string Email = "ops@example.com";

    private const string Yaml = $"""
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: checkout
          annotations:
            link.argocd.argoproj.io/external-link: {Url}
            maintainer: {Email}
        spec:
          template:
            spec:
              containers:
                - name: checkout
                  image: registry.example.com/payments/checkout:9.0.1
        """;

    /// <summary>
    /// The YAML editor draws no links, and keeps AvaloniaEdit's room below the last line:
    /// it is the working editor, and <c>EditorDefaults.ApplyViewer</c> is only for viewers.
    /// </summary>
    internal static void YamlEditorLinks(Window window)
    {
        var view = window.GetVisualDescendants().OfType<YamlEditorView>().First();
        EveryEditorHasLinksOff(view);
        ((YamlEditorTabViewModel)view.DataContext!).YamlText = Yaml;
        Settle();

        var editor = view.FindControl<TextEditor>("Editor")!;
        NoLinks(editor, "The YAML editor");
        if (!editor.Options.AllowScrollBelowDocument)
            throw new InvalidOperationException("The YAML editor lost its room below the last line; that is for read-only viewers only.");

        Console.WriteLine("YAML editor draws URLs and e-mail addresses as text, not links.");
    }

    /// <summary>
    /// A Helm release's values and manifest draw no links, and stop scrolling at their last
    /// line. Each editor is in its own tab, so each tab is selected to lay it out.
    /// </summary>
    internal static void HelmReleaseLinks(Window window)
    {
        var view = window.GetVisualDescendants().OfType<HelmReleaseView>().First();
        EveryEditorHasLinksOff(view);
        var helm = (HelmReleaseTabViewModel)view.DataContext!;
        helm.ValuesYaml = Yaml;
        helm.Manifest = Yaml;
        var strip = view.FindControl<ListBox>("HelmTabStrip")!;
        try
        {
            foreach (var (index, name) in new[] { (0, "ValuesEditor"), (1, "ManifestEditor") })
            {
                strip.SelectedIndex = index;
                Settle();
                var editor = view.FindControl<TextEditor>(name)!;
                NoLinks(editor, $"The Helm release's {name}");
                if (editor.Options.AllowScrollBelowDocument)
                    throw new InvalidOperationException(
                        $"The Helm release's {name} scrolls past its last line, which puts a scroll bar beside text that fits.");
            }
        }
        finally
        {
            strip.SelectedIndex = 0;
            Settle();
        }

        Console.WriteLine("Helm values and manifest draw URLs and e-mail addresses as text, and stop at their last line.");
    }

    // Every editor the view declares, laid out or not (the unselected Helm tab's is not):
    // an editor added to one of these views without EditorDefaults fails here.
    private static void EveryEditorHasLinksOff(Control view)
    {
        foreach (var editor in view.GetLogicalDescendants().OfType<TextEditor>())
        {
            if (editor.Options.EnableHyperlinks || editor.Options.EnableEmailHyperlinks)
                throw new InvalidOperationException(
                    $"{view.GetType().Name}'s {editor.Name ?? "unnamed"} editor still renders links; call EditorDefaults on it.");
        }
    }

    private static void NoLinks(TextEditor editor, string what)
    {
        var textView = editor.TextArea.TextView;
        textView.EnsureVisualLines();
        var lines = new List<string>();
        var links = new List<string>();
        foreach (var line in textView.VisualLines)
        {
            var drawn = "";
            foreach (var element in line.Elements)
            {
                var text = editor.Document.GetText(line.FirstDocumentLine.Offset + element.RelativeTextOffset, element.DocumentLength);
                drawn += text;
                if (element is VisualLineLinkText)
                    links.Add(text);
            }

            lines.Add(drawn);
        }

        if (links.Count > 0)
            throw new InvalidOperationException($"{what} drew {string.Join(" and ", links)} as a link, in Blue over the highlighting.");

        // An editor with nothing laid out would pass the line above without having drawn anything.
        if (!lines.Any(l => l.Contains(Url, StringComparison.Ordinal)) || !lines.Any(l => l.Contains(Email, StringComparison.Ordinal)))
            throw new InvalidOperationException($"{what} did not draw the lines holding the URL and the address, so nothing was checked.");
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
