using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// FEAT-56: the access review says who the API server thinks you are, above My permissions —
/// the name and groups when it answered, and a stated sentence for each way it could not.
/// </summary>
public class RbacWhoAmITests
{
    private static RbacTabViewModel Pane(SelfSubjectIdentity? identity) =>
        new(TestObjects.OfflineClient(), "payments", load: false) { Identity = identity };

    [Test]
    public async Task The_name_and_groups_the_server_reports_are_shown()
    {
        var pane = Pane(new SelfSubjectIdentity(
            SelfSubjectReviewOutcome.Answered, "arn:aws:iam::111122223333:role/dev", ["payments-oncall", "system:authenticated"], null));

        await Assert.That(pane.IsIdentityKnown).IsTrue();
        await Assert.That(pane.IdentityName).IsEqualTo("arn:aws:iam::111122223333:role/dev");
        await Assert.That(pane.IdentityGroupsText).IsEqualTo(" · groups payments-oncall, system:authenticated");
        await Assert.That(pane.IdentityTooltip).Contains("payments-oncall\nsystem:authenticated");
    }

    [Test]
    public async Task A_user_in_no_groups_says_so()
    {
        var pane = Pane(new SelfSubjectIdentity(SelfSubjectReviewOutcome.Answered, "admin", [], null));

        await Assert.That(pane.IdentityGroupsText).IsEqualTo(" · no groups");
    }

    [Test]
    public async Task A_server_older_than_1_27_is_a_stated_state()
    {
        var pane = Pane(SelfSubjectIdentity.Unknown(SelfSubjectReviewOutcome.NotServed, null));

        await Assert.That(pane.IsIdentityKnown).IsFalse();
        await Assert.That(pane.IdentityNote).StartsWith("Not available on this server");
        await Assert.That(pane.IdentityNote).Contains("1.27");
    }

    [Test]
    public async Task A_refusal_quotes_the_server()
    {
        var pane = Pane(SelfSubjectIdentity.Unknown(SelfSubjectReviewOutcome.Refused, "selfsubjectreviews is forbidden"));

        await Assert.That(pane.IdentityNote).IsEqualTo("The server refused to say who you are: selfsubjectreviews is forbidden");
    }

    [Test]
    public async Task A_name_cannot_reorder_itself_with_a_bidi_override()
    {
        var pane = Pane(new SelfSubjectIdentity(SelfSubjectReviewOutcome.Answered, "dev‮nimda", ["ops​"], null));

        await Assert.That(pane.IdentityName).IsEqualTo("dev⟨U+202E⟩nimda");
        await Assert.That(pane.IdentityGroupsText).Contains("ops⟨U+200B⟩");
    }

    /// <summary>
    /// The identity is asked on its own: a rules review that fails (here, nothing listens) does
    /// not take the "Signed in as" line down with it, which then states its own failure.
    /// </summary>
    [Test]
    public async Task A_failed_rules_review_still_reports_the_identity()
    {
        var pane = new RbacTabViewModel(TestObjects.OfflineClient(), "payments");
        for (var i = 0; i < 200 && (pane.IsLoading || pane.Identity is null); i++)
        {
            await Task.Delay(50);
        }

        await Assert.That(pane.ErrorMessage).IsNotNull();
        await Assert.That(pane.Identity?.Outcome).IsEqualTo(SelfSubjectReviewOutcome.Failed);
        await Assert.That(pane.IdentityNote).StartsWith("Could not ask the server who you are:");
        await Assert.That(pane.IsIdentityPending).IsFalse();
    }
}
