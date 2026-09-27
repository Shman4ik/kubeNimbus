using Avalonia.Media;
using Avalonia.Styling;
using KubeNimbus.App.Editing;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The YAML editor has a palette per theme. The dark one used to be drawn on the light
/// theme too, where numbers, anchors and keys were pale on white; these pin that every
/// role the grammar colours has a light colour, and that it reads on a white page.
/// </summary>
public class YamlHighlightingTests
{
    [Test]
    public async Task Each_theme_gets_its_own_palette()
    {
        await Assert.That(YamlSyntaxHighlighting.For(ThemeVariant.Light)).IsSameReferenceAs(YamlSyntaxHighlighting.Light);
        await Assert.That(YamlSyntaxHighlighting.For(ThemeVariant.Dark)).IsSameReferenceAs(YamlSyntaxHighlighting.Dark);
        await Assert.That(YamlSyntaxHighlighting.For(null)).IsSameReferenceAs(YamlSyntaxHighlighting.Dark);
    }

    /// <summary>
    /// WCAG AA for body text, 4.5:1 against white. A colour added to the .xshd without a
    /// light counterpart keeps its dark value and fails here rather than on someone's screen.
    /// </summary>
    [Test]
    public async Task Every_light_colour_reads_on_white()
    {
        foreach (var named in YamlSyntaxHighlighting.Dark.NamedHighlightingColors)
        {
            var light = YamlSyntaxHighlighting.Light.GetNamedColor(named.Name);
            var colour = light.Foreground!.GetColor(null!)!.Value;
            await Assert.That(Contrast(colour, Colors.White)).IsGreaterThanOrEqualTo(4.5)
                .Because($"{named.Name} is {colour} on the light theme");
        }
    }

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
