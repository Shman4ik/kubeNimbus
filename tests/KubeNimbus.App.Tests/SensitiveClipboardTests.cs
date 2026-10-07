namespace KubeNimbus.App.Tests;

/// <summary>
/// S1-5: a decoded Secret value copied from the YAML editor is marked to stay out of Windows
/// clipboard history and cloud sync, and is cleared a minute later — only if the clipboard
/// still holds exactly that value.
/// </summary>
public class SensitiveClipboardTests
{
    [Test]
    public async Task The_value_is_cleared_when_the_clipboard_still_holds_it()
    {
        var cleared = 0;
        var result = await SensitiveClipboard.ClearIfUnchangedAsync(
            "hunter2", () => Task.FromResult<string?>("hunter2"), () => { cleared++; return Task.CompletedTask; });

        await Assert.That(result).IsTrue();
        await Assert.That(cleared).IsEqualTo(1);
    }

    [Test]
    public async Task Something_copied_since_is_never_cleared()
    {
        var cleared = 0;
        var result = await SensitiveClipboard.ClearIfUnchangedAsync(
            "hunter2", () => Task.FromResult<string?>("a log line someone copied after"), () => { cleared++; return Task.CompletedTask; });

        await Assert.That(result).IsFalse();
        await Assert.That(cleared).IsEqualTo(0);
    }

    [Test]
    public async Task A_clipboard_that_cannot_be_read_is_left_alone()
    {
        var cleared = 0;
        var result = await SensitiveClipboard.ClearIfUnchangedAsync(
            "hunter2", () => throw new InvalidOperationException("clipboard busy"), () => { cleared++; return Task.CompletedTask; });

        await Assert.That(result).IsFalse();
        await Assert.That(cleared).IsEqualTo(0);
    }

    [Test]
    public async Task On_windows_the_copy_carries_the_formats_that_keep_it_out_of_history_and_sync()
    {
        var windows = SensitiveClipboard.CreateTransfer("hunter2", windows: true);
        var identifiers = windows.Formats.Select(f => f.Identifier).ToList();

        await Assert.That(identifiers).Contains("ExcludeClipboardContentFromMonitorProcessing");
        await Assert.That(identifiers).Contains("CanIncludeInClipboardHistory");
        await Assert.That(identifiers).Contains("CanUploadToCloudClipboard");
        await Assert.That(windows.Formats).Contains(Avalonia.Input.DataFormat.Text);

        var elsewhere = SensitiveClipboard.CreateTransfer("hunter2", windows: false);
        await Assert.That(elsewhere.Formats.Count).IsEqualTo(1);
    }

    [Test]
    public async Task It_waits_a_minute()
    {
        await Assert.That(SensitiveClipboard.ClearAfter).IsEqualTo(TimeSpan.FromSeconds(60));
    }
}
