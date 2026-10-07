using Avalonia.Input;
using Avalonia.Input.Platform;

namespace KubeNimbus.App;

/// <summary>
/// Copies a decoded Secret value to the clipboard so that it does not outlive the reason it
/// was copied: kept out of Windows clipboard history and cloud clipboard sync, and cleared
/// after <see cref="ClearAfter"/> if it is still what the clipboard holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> On Windows, ordinary text on the clipboard enters clipboard history (Win+V),
/// where it stays across later copies, and with cloud clipboard on it is synced to every
/// device signed in to the same account. A database password copied once from the YAML
/// editor's reveal panel used to be in both indefinitely.
/// </para>
/// <para>
/// <b>The Windows formats</b> are the ones Microsoft documents for this
/// (<c>ExcludeClipboardContentFromMonitorProcessing</c>, and <c>CanIncludeInClipboardHistory</c>
/// and <c>CanUploadToCloudClipboard</c> as a DWORD 0), set beside the text in the same
/// clipboard write through Avalonia's platform formats — no interop, nothing for the AOT
/// compiler to trim. Password managers mark their copies the same way.
/// </para>
/// <para>
/// <b>Only Secret values.</b> A ConfigMap's <c>binaryData</c>, a log or a URL is not a
/// secret, and a clipboard that empties itself under someone pasting a log is a bug.
/// </para>
/// </remarks>
internal static class SensitiveClipboard
{
    /// <summary>How long a copied Secret value stays on the clipboard.</summary>
    internal static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(60);

    /// <summary>The Windows clipboard formats that keep a copy out of history and cloud sync.</summary>
    internal static readonly IReadOnlyList<string> WindowsPrivacyFormats =
    [
        "ExcludeClipboardContentFromMonitorProcessing",
        "CanIncludeInClipboardHistory",
        "CanUploadToCloudClipboard",
    ];

    /// <summary>
    /// Bumped by every sensitive copy, so a timer started for an earlier copy of the same
    /// value does not clear a later one early.
    /// </summary>
    private static int _generation;

    /// <summary>The clipboard write: the text, plus the privacy formats on Windows.</summary>
    internal static DataTransfer CreateTransfer(string text, bool windows)
    {
        var item = new DataTransferItem();
        item.SetText(text);
        if (windows)
        {
            foreach (var format in WindowsPrivacyFormats)
            {
                // A DWORD 0: "no" for the two Can… formats; the Exclude format's content is ignored.
                item.Set(DataFormat.CreateBytesPlatformFormat(format), new byte[4]);
            }
        }

        var transfer = new DataTransfer();
        transfer.Add(item);
        return transfer;
    }

    /// <summary>
    /// Puts <paramref name="text"/> on <paramref name="clipboard"/> and returns a task that
    /// completes after <see cref="ClearAfter"/>, true when it cleared the value then.
    /// </summary>
    internal static async Task<Task<bool>> CopyAsync(IClipboard clipboard, string text)
    {
        await clipboard.SetDataAsync(CreateTransfer(text, OperatingSystem.IsWindows()));
        var generation = Interlocked.Increment(ref _generation);
        return ClearLaterAsync(clipboard, text, generation);
    }

    private static async Task<bool> ClearLaterAsync(IClipboard clipboard, string text, int generation)
    {
        await Task.Delay(ClearAfter);
        if (Volatile.Read(ref _generation) != generation)
        {
            return false;
        }

        return await ClearIfUnchangedAsync(text, clipboard.TryGetTextAsync, clipboard.ClearAsync);
    }

    /// <summary>
    /// Clears the clipboard only if it still holds exactly <paramref name="copied"/> — never
    /// something copied since, from this app or any other. A clipboard that cannot be read is
    /// left alone. True when it cleared.
    /// </summary>
    internal static async Task<bool> ClearIfUnchangedAsync(string copied, Func<Task<string?>> read, Func<Task> clear)
    {
        string? current;
        try
        {
            current = await read();
        }
        catch (Exception)
        {
            return false;
        }

        if (!string.Equals(current, copied, StringComparison.Ordinal))
        {
            return false;
        }

        await clear();
        return true;
    }
}
