namespace KubeNimbus.App.Tests;

/// <summary>
/// The launch check's verdict once the lifetime has shut down. On macOS it is computed in the
/// lifetime's <c>Exit</c> event (<c>Platform.MacShutdown</c> ends the process there, to stay
/// out of the <c>exit()</c> teardown that aborts), so the rule that a clean exit without a
/// rendered window is a failure has to hold there too, not only after the message loop.
/// </summary>
public class SmokeVerdictTests
{
    [Test]
    public async Task A_clean_exit_with_no_rendered_window_is_still_a_failure()
    {
        // Nothing in this process ever rendered a smoke-test window.
        await Assert.That(SmokeTest.Verdict(0)).IsEqualTo(65);
    }

    [Test]
    public async Task A_failing_code_passes_through()
    {
        await Assert.That(SmokeTest.Verdict(69)).IsEqualTo(69);
    }
}
