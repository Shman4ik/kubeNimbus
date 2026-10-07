using System.Text.Json;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// The bidi controls and zero-width characters are format characters, not controls, so the
/// log pane's escape stripping let them through and a right-to-left override in a log line,
/// an Event or an Argo message reordered what was drawn.
/// </summary>
public class InvisibleCharactersTests
{
    [Test]
    public async Task Bidi_and_zero_width_characters_become_visible_markers()
    {
        await Assert.That(InvisibleCharacters.Reveal("deny\u202Ewolla")).IsEqualTo("deny⟨U+202E⟩wolla");
        await Assert.That(InvisibleCharacters.Reveal("ad\u200Bmin \u2066x\u2069 \uFEFF\u061C"))
            .IsEqualTo("ad⟨U+200B⟩min ⟨U+2066⟩x⟨U+2069⟩ ⟨U+FEFF⟩⟨U+061C⟩");

        foreach (var c in "\u061C\u200B\u200E\u200F\u202A\u202B\u202C\u202D\u202E\u2060\u2066\u2067\u2068\u2069\uFEFF")
        {
            await Assert.That(char.IsControl(c)).IsFalse();
            await Assert.That(InvisibleCharacters.Reveal(c.ToString())).IsEqualTo(InvisibleCharacters.Marker(c));
        }
    }

    [Test]
    public async Task Ordinary_text_is_returned_as_the_same_instance_and_joiners_are_left_alone()
    {
        const string plain = "GET /healthz 200 — ✓ ready";
        await Assert.That(ReferenceEquals(InvisibleCharacters.Reveal(plain), plain)).IsTrue();

        // Persian shaping and emoji sequences use the joiners; they reorder nothing.
        const string joined = "می\u200Cخواهم 👨\u200D👩\u200D👧";
        await Assert.That(InvisibleCharacters.Reveal(joined)).IsEqualTo(joined);
    }

    [Test]
    public async Task Argo_messages_are_read_with_the_markers_in_place()
    {
        using var document = JsonDocument.Parse("""
            {"apiVersion":"argoproj.io/v1alpha1","kind":"Application","metadata":{"name":"x","namespace":"argocd"},"spec":{},
             "status":{"health":{"status":"Degraded","message":"ok \u202Eliaf"},
                       "operationState":{"phase":"Failed","message":"\u2067sync\u2069"},
                       "conditions":[{"type":"ComparisonError","message":"a\u200Bb"}]}}
            """);
        var app = ArgoCd.ReadApplication(new DynamicResource(document.RootElement.Clone()));

        await Assert.That(app.HealthMessage).IsEqualTo("ok ⟨U+202E⟩liaf");
        await Assert.That(app.OperationMessage).IsEqualTo("⟨U+2067⟩sync⟨U+2069⟩");
        await Assert.That(app.Conditions[0].Message).IsEqualTo("a⟨U+200B⟩b");
    }
}
