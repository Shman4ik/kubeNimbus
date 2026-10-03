using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Editing;
using KubeNimbus.App.ViewModels;
using KubeNimbus.App.Views;
using Nimbus.Ui.Fonts;
using SkiaSharp;
using SvcSystems.UI.Terminal;

namespace KubeNimbus.Screenshot;

/// <summary>
/// DESIGN.md rule 22: text is drawn in one of three faces, each one resource, and the faces
/// are settings. A face written at a use site still compiles, renders and passes every other
/// check, which is how about 80 copies of <c>Cascadia Mono,Consolas,monospace</c> built up,
/// so the rule is read off the windows as drawn. The view-model tests start no Avalonia
/// application, which is why this lives here rather than in <c>KubeNimbus.App.Tests</c>.
/// </summary>
internal static class FontChecks
{
    private static readonly List<string> Failures = [];
    private static int _monoProbed;
    private static int _textProbed;

    /// <summary>
    /// Every element in the <c>mono</c> class is drawn in <c>MonoFont</c> at spacing 0, and
    /// every text-drawing element is in one of the three faces. A <c>mono</c> element that
    /// fails is one the class does not reach (a local <c>FontFamily</c>, an app style loaded
    /// after the shared one, a control type the class's selector does not match); any other
    /// failure is a face written at a use site.
    /// </summary>
    internal static void Walk(Window window, string name)
    {
        var mono = Resource("MonoFont");
        var allowed = new[] { window.GetValue(TextElement.FontFamilyProperty), mono, Resource("KeyCapFont") };

        foreach (var visual in window.GetVisualDescendants().OfType<Control>())
        {
            // The exec terminal is a Grid with a FontFamily of its own, not the inherited
            // TextElement one, so it names the token directly and is read through its own
            // property: drawn in the interface face, a shell's columns stop lining up.
            if (visual is TerminalControl terminal)
            {
                _monoProbed++;
                if (!Equals(terminal.GetValue(TerminalControl.FontFamilyProperty), mono))
                {
                    Failures.Add($"{name}: the exec terminal draws in \"{terminal.GetValue(TerminalControl.FontFamilyProperty)}\", not MonoFont");
                }

                continue;
            }

            var family = visual.GetValue(TextElement.FontFamilyProperty);
            if (visual.Classes.Contains("mono"))
            {
                _monoProbed++;
                var spacing = visual.GetValue(TextElement.LetterSpacingProperty);
                if (!Equals(family, mono) || spacing != 0)
                {
                    Failures.Add($"{name}: {Describe(visual)} has class mono but draws in \"{family}\" at spacing {spacing}");
                }

                continue;
            }

            if (visual is not (TextBlock or TextArea) || !visual.IsEffectivelyVisible)
            {
                continue;
            }

            // The code font list draws each row in the face it offers: the one place a
            // family is chosen by data rather than by role.
            if (visual.DataContext is CodeFontOption)
            {
                continue;
            }

            _textProbed++;
            if (!allowed.Any(face => Equals(face, family)))
            {
                Failures.Add($"{name}: {Describe(visual)} draws in \"{family}\", which is none of the three faces");
            }
        }
    }

    /// <summary>
    /// The settings reach text that is already on screen: the preferences page changes the
    /// interface face and the code face of the open window, persists both, and goes back.
    /// Also that the bundled face is registered under the name the tokens use and is the
    /// fixed-pitch cut, and that the installed list read through Skia tells a monospace face
    /// from a proportional one.
    /// </summary>
    internal static void SettingsReachOpenText(Window window)
    {
        if (!FontManager.Current.TryGetGlyphTypeface(new Typeface(NimbusFonts.BundledMono), out var bundled)
            || bundled.FamilyName != NimbusFonts.BundledMonoName)
        {
            throw new InvalidOperationException(
                $"The bundled face did not resolve as \"{NimbusFonts.BundledMonoName}\"; is WithNimbusFonts() in the app builder?");
        }

        using (var regular = Skia("avares://Nimbus.Ui/Fonts/JetBrainsMono/JetBrainsMonoNL-Regular.ttf"))
        using (var inter = Skia("avares://Avalonia.Fonts.Inter/Assets/Inter-Regular.ttf"))
        {
            if (!MonospaceFonts.IsMonospace(regular) || MonospaceFonts.IsMonospace(inter))
            {
                throw new InvalidOperationException("MonospaceFonts.IsMonospace cannot tell JetBrains Mono from Inter.");
            }
        }

        var page = window.GetVisualDescendants().OfType<PreferencesView>().First().DataContext as PreferencesViewModel
            ?? throw new InvalidOperationException("The preferences page has no view model.");
        var code = window.GetVisualDescendants().OfType<Control>()
            .First(c => c.Classes.Contains("mono") && c.IsEffectivelyVisible && c is not PreferencesView);

        // A fresh settings file is "auto", which is the system face, and the page says so.
        if (page.InterfaceFontIndex != 0 || page.SelectedCodeFont != CodeFontOption.Bundled)
        {
            throw new InvalidOperationException(
                $"A fresh page opened on interface {page.InterfaceFontIndex} and code font {page.SelectedCodeFont?.Label}, not System and the bundled face.");
        }

        page.InterfaceFontIndex = 1;
        Settle();
        Expect(window.GetValue(TextElement.FontFamilyProperty), NimbusFonts.Interface(InterfaceFont.Inter), "the window after choosing Inter");
        Expect(KubeNimbus.App.App.LoadSettings().InterfaceFont, "inter", "the stored interface font");

        // The installed list arrives from the thread pool; the box offers the bundled face
        // until then. Wait for it here so a face other than the bundled one can be chosen.
        page.LoadInstalledCodeFontsAsync().GetAwaiter().GetResult();
        Settle();
        var installed = page.CodeFonts.FirstOrDefault(option => option.Name is not null);
        if (installed is not null)
        {
            page.SelectedCodeFont = installed;
            Settle();
            Expect(code.GetValue(TextElement.FontFamilyProperty), NimbusFonts.Mono(installed.Name), $"{Describe(code)} after choosing {installed.Name}");
            Expect(KubeNimbus.App.App.LoadSettings().CodeFont, installed.Name, "the stored code font");
        }

        page.InterfaceFontIndex = 0;
        page.SelectedCodeFont = CodeFontOption.Bundled;
        Settle();
        Expect(window.GetValue(TextElement.FontFamilyProperty), NimbusFonts.Interface(InterfaceFont.System), "the window after choosing System again");
        Expect(code.GetValue(TextElement.FontFamilyProperty), NimbusFonts.Mono(null), $"{Describe(code)} after choosing the bundled face again");
        Expect(KubeNimbus.App.App.LoadSettings().CodeFont, null, "the stored code font after choosing the bundled face");

        Console.WriteLine(
            $"Font settings reach open text ({page.CodeFonts.Count - 1} installed monospace families listed{(installed is null ? "" : $", tried {installed.Name}")}).");
    }

    /// <summary>Called once after every scenario has rendered.</summary>
    internal static void ThrowIfAnyFailed(bool filtered)
    {
        Console.WriteLine($"Font roles checked: {_monoProbed} mono, {_textProbed} other text, failures: {Failures.Count}.");
        if (Failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Text outside the three faces (DESIGN.md rule 22):" + Environment.NewLine
                + string.Join(Environment.NewLine, Failures.Distinct()));
        }

        // Not vacuous: a full run has to find the code it is checking.
        if (!filtered && _monoProbed < 200)
        {
            throw new InvalidOperationException($"Only {_monoProbed} mono elements were checked; the walk is not finding them.");
        }
    }

    private static FontFamily Resource(string key) =>
        Application.Current!.TryGetResource(key, null, out var value) && value is FontFamily family
            ? family
            : throw new InvalidOperationException($"No {key} resource.");

    private static SKTypeface Skia(string uri)
    {
        using var stream = AssetLoader.Open(new Uri(uri));
        return SKTypeface.FromStream(stream) ?? throw new InvalidOperationException($"Skia cannot read {uri}.");
    }

    private static void Expect<T>(T actual, T expected, string what)
    {
        if (!Equals(actual, expected))
        {
            throw new InvalidOperationException($"Font settings: {what} is \"{actual}\", expected \"{expected}\".");
        }
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static string Describe(Control element) =>
        $"{element.GetType().Name}{(element.Name is { } n ? "#" + n : "")}"
        + (element is TextBlock { Text: { } text } ? $" \"{(text.Length > 40 ? text[..40] + "…" : text)}\"" : "");
}
