namespace KubeNimbus.Core;

/// <summary>
/// How much of a cluster's streams the client reads at once: the size caps on a watch frame
/// and on a log line, and what a cut log line looks like. The cluster writes both streams and
/// a container writes its own stdout, so neither is trusted to end.
/// </summary>
public sealed partial class ClusterClient
{
    /// <summary>
    /// The longest watch frame read. A frame is one object, and the API server stores
    /// nothing near this (etcd's default request limit is 1.5 MiB); a line longer than this
    /// is a stream to stop reading, not an object to hold in memory.
    /// </summary>
    internal const int MaxWatchFrameBytes = 32 * 1024 * 1024;

    /// <summary>
    /// The longest log line kept whole. A container writes its own stdout, and a line with no
    /// newline would otherwise be read into memory for as long as it kept coming. Past this,
    /// the head is kept with <see cref="TruncatedLogLineMarker"/> and the rest of the line is
    /// read and dropped.
    /// </summary>
    internal const int MaxLogLineBytes = 1024 * 1024;

    /// <summary>What a log line cut at <see cref="MaxLogLineBytes"/> ends with.</summary>
    public const string TruncatedLogLineMarker = " … [line cut at 1 MiB by kubeNimbus]";

    /// <summary>
    /// The head of a log line that ran past <see cref="MaxLogLineBytes"/>, ended on a whole
    /// character (the cut can land inside a multi-byte one) and marked as cut, so the pane
    /// never shows a line as complete when it is not.
    /// </summary>
    internal static string TruncatedLogLine(ReadOnlySpan<byte> head)
    {
        // Back up over a character the cut split: find the last lead byte in the final
        // four, and drop it with its continuation bytes if fewer arrived than it declares.
        for (var i = head.Length - 1; i >= Math.Max(0, head.Length - 4); i--)
        {
            var b = head[i];
            if ((b & 0xC0) == 0x80)
            {
                continue; // a continuation byte
            }

            var declared = b < 0x80 ? 1 : b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
            if (head.Length - i < declared)
            {
                head = head[..i];
            }

            break;
        }

        return System.Text.Encoding.UTF8.GetString(head) + TruncatedLogLineMarker;
    }
}
