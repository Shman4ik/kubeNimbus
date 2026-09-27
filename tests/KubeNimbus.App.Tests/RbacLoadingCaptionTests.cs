using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The access review's "Loading…" caption belongs to the review's own load (My permissions,
/// Bindings). On "Who can…" it read as that query still running, which has its own progress
/// line — the README's RBAC screenshot showed exactly that.
/// </summary>
public class RbacLoadingCaptionTests
{
    [Test]
    public async Task The_review_load_is_not_announced_on_the_who_can_tab()
    {
        var rbac = new RbacTabViewModel(TestObjects.OfflineClient(), "payments");

        // The constructor starts the review's own load against an address nothing listens
        // on; let it fail first, so it cannot clear IsLoading between the assertions below.
        for (var i = 0; i < 100 && rbac.IsLoading; i++)
        {
            await Task.Delay(50);
        }

        await Assert.That(rbac.IsLoading).IsFalse();
        rbac.IsLoading = true;

        rbac.SelectedTabIndex = 0;
        await Assert.That(rbac.ShowsReviewLoading).IsTrue();

        rbac.SelectedTabIndex = RbacTabViewModel.WhoCanTabIndex;
        await Assert.That(rbac.ShowsReviewLoading).IsFalse();

        rbac.SelectedTabIndex = 0;
        rbac.IsLoading = false;
        await Assert.That(rbac.ShowsReviewLoading).IsFalse();
    }
}
