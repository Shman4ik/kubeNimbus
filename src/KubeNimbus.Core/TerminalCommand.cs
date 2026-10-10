using System.Text.RegularExpressions;

namespace KubeNimbus.Core;

/// <summary>
/// A <c>kubectl</c> command for the machine's own terminal to run (FEAT-17, FEAT-27): an
/// exec session handed out of the app's pane, or a node shell. The arguments are everything
/// after <c>kubectl</c>; <see cref="TerminalLauncher"/> puts the absolute path of the kubectl
/// it found in front of them and passes the whole list to the terminal in that platform's own
/// shape.
///
/// <para>
/// <b>Every name is checked before it reaches a command line</b>, even though each one has
/// already been validated by the API server. They travel through a PowerShell script on
/// Windows, a <c>/bin/sh</c> script on macOS and an emulator's argument list on Linux, and a
/// name that carried a quote, a semicolon or a leading dash would mean something to one of
/// those. So a pod, node, namespace or container name is accepted only in the API server's own
/// shape (RFC 1123: lowercase letters, digits, <c>-</c> and, for pods and nodes, <c>.</c>,
/// starting with a letter or digit), an image only in the characters an image reference uses,
/// and a command word only as printable ASCII without a double quote. Anything else is refused
/// with an <see cref="ArgumentException"/> that says which value, and nothing is started. Each
/// platform quotes what it is given as well (see <see cref="TerminalLauncher"/>); the check
/// here is the half that does not depend on getting a quoting rule right.
/// </para>
/// </summary>
public sealed partial class TerminalCommand
{
    private TerminalCommand(IReadOnlyList<string> arguments)
    {
        Arguments = arguments;
        Summary = "kubectl " + string.Join(' ', arguments.Select(Display));
    }

    /// <summary>The arguments after <c>kubectl</c>, one entry per argument.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>The command as a person would type it, for the notice that says what was opened.</summary>
    public string Summary { get; }

    /// <summary>
    /// What an exec into a pod whose shell is not known runs: bash where the image has it, and
    /// <c>/bin/sh</c> otherwise. One command rather than the pane's probe of three, because a
    /// terminal can be handed only one; kubectl prints the runtime's own sentence in that
    /// terminal when neither exists.
    /// </summary>
    public const string ShellProbe = "if [ -x /bin/bash ]; then exec /bin/bash; else exec /bin/sh; fi";

    /// <summary>
    /// <c>kubectl exec -it -n &lt;namespace&gt; &lt;pod&gt; -c &lt;container&gt; -- &lt;command…&gt;</c>.
    /// </summary>
    public static TerminalCommand Exec(string @namespace, string pod, string container, IReadOnlyList<string> command)
    {
        RequireLabel(@namespace, "namespace");
        RequireSubdomain(pod, "pod");
        RequireLabel(container, "container");
        ArgumentNullException.ThrowIfNull(command);
        if (command.Count == 0)
        {
            throw new ArgumentException("There is no command to run in the container.", nameof(command));
        }

        foreach (var word in command)
        {
            RequireCommandWord(word);
        }

        return new TerminalCommand(["exec", "-it", "-n", @namespace, pod, "-c", container, "--", .. command]);
    }

    /// <summary>
    /// <c>kubectl debug node/&lt;node&gt; -it --image=&lt;image&gt;</c>: kubectl's own node shell, a pod
    /// on that node in its host namespaces with the node's filesystem at <c>/host</c>. Created by
    /// kubectl in the user's terminal, never by this app (the owner's call on FEAT-27, see
    /// docs/engineering/node-operations.md), so a Pod Security or RBAC refusal is kubectl's
    /// sentence in that terminal.
    /// </summary>
    public static TerminalCommand NodeShell(string node, string image)
    {
        RequireSubdomain(node, "node");
        if (string.IsNullOrEmpty(image) || image.Length > 512 || !ImageReference().IsMatch(image))
        {
            throw new ArgumentException(
                $"'{image}' is not an image reference kubeNimbus will hand to a terminal.", nameof(image));
        }

        return new TerminalCommand(["debug", $"node/{node}", "-it", $"--image={image}"]);
    }

    /// <summary>
    /// The command an exec hand-off runs: the shell the pane is already using or the one typed
    /// into its box when there is one, <c>cmd</c> on a Windows node (Nano Server has no
    /// PowerShell, and every Windows image has <c>cmd</c>), and <see cref="ShellProbe"/> otherwise.
    /// </summary>
    public static IReadOnlyList<string> ShellFor(PodOperatingSystem os, string? shell) =>
        !string.IsNullOrWhiteSpace(shell) ? [shell.Trim()]
        : os == PodOperatingSystem.Windows ? ["cmd"]
        : ["/bin/sh", "-c", ShellProbe];

    private static void RequireLabel(string? value, string what)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 63 || !DnsLabel().IsMatch(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid {what} name, so kubeNimbus will not hand it to a terminal.", what);
        }
    }

    private static void RequireSubdomain(string? value, string what)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 253 || !DnsSubdomain().IsMatch(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid {what} name, so kubeNimbus will not hand it to a terminal.", what);
        }
    }

    private static void RequireCommandWord(string? word)
    {
        if (string.IsNullOrEmpty(word) || word.Length > 1024 || word.Any(c => c is < ' ' or > '~' or '"'))
        {
            throw new ArgumentException(
                $"'{word}' cannot be handed to a terminal: a command word is printable ASCII without a double quote.",
                nameof(word));
        }
    }

    /// <summary>How <see cref="Summary"/> shows one argument: as it is, or single-quoted when it has a space.</summary>
    private static string Display(string argument) =>
        argument.Contains(' ', StringComparison.Ordinal) ? $"'{argument}'" : argument;

    [GeneratedRegex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DnsLabel();

    [GeneratedRegex("^[a-z0-9]([-a-z0-9.]*[a-z0-9])?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DnsSubdomain();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/:@-]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ImageReference();
}
