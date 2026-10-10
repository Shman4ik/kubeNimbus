using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// The commands handed to the machine's terminal (FEAT-17, FEAT-27): what kubectl is given,
/// and the check that keeps a name from meaning something to the shell or script it travels
/// through on the way.
/// </summary>
public class TerminalCommandTests
{
    [Test]
    public async Task AnExecIsKubectlExecInteractiveWithEveryPartItsOwnArgument()
    {
        var command = TerminalCommand.Exec("payments", "api-7f9c-0", "app", ["/bin/ash"]);

        await Assert.That(command.Arguments).IsEquivalentTo(
            new[] { "exec", "-it", "-n", "payments", "api-7f9c-0", "-c", "app", "--", "/bin/ash" });
        await Assert.That(command.Summary).IsEqualTo("kubectl exec -it -n payments api-7f9c-0 -c app -- /bin/ash");
    }

    [Test]
    public async Task ANodeShellIsKubectlDebugNodeWithTheImage()
    {
        var command = TerminalCommand.NodeShell("ip-10-0-1-5.ec2.internal", DebugContainers.DefaultImage);

        await Assert.That(command.Arguments).IsEquivalentTo(
            new[] { "debug", "node/ip-10-0-1-5.ec2.internal", "-it", $"--image={DebugContainers.DefaultImage}" });
    }

    /// <summary>
    /// Every name is checked against the API server's own shape before it is handed on, so one
    /// that carried a quote, a separator, a newline or a leading dash — an option to kubectl, a
    /// statement to a shell — is refused and nothing is started. The names here are the ways a
    /// value could be made to mean something on the way through.
    /// </summary>
    [Test]
    [Arguments("payments", "api;rm -rf ~", "app")]
    [Arguments("payments", "api$(id)", "app")]
    [Arguments("payments", "-n", "app")]
    [Arguments("payments", "api\n", "app")]
    [Arguments("payments", "api'x", "app")]
    [Arguments("Payments", "api", "app")]
    [Arguments("pay ments", "api", "app")]
    [Arguments("payments", "api", "a.b")]
    [Arguments("payments", "api", "")]
    [Arguments("payments", "", "app")]
    public async Task ANameThatIsNotAKubernetesNameIsRefused(string @namespace, string pod, string container)
    {
        await Assert.That(() => TerminalCommand.Exec(@namespace, pod, container, ["/bin/sh"])).Throws<ArgumentException>();
    }

    [Test]
    [Arguments("bash\"; whoami")]
    [Arguments("sh\u0007")]
    [Arguments("sh\r")]
    [Arguments("sh’")]
    [Arguments("")]
    public async Task ACommandWordOutsidePrintableAsciiOrWithADoubleQuoteIsRefused(string word)
    {
        await Assert.That(() => TerminalCommand.Exec("payments", "api", "app", [word])).Throws<ArgumentException>();
    }

    [Test]
    public async Task AnExecWithNoCommandIsRefused()
    {
        await Assert.That(() => TerminalCommand.Exec("payments", "api", "app", [])).Throws<ArgumentException>();
    }

    [Test]
    [Arguments("worker 1")]
    [Arguments("node/../x")]
    [Arguments("-it")]
    public async Task ANodeNameThatIsNotOneIsRefused(string node)
    {
        await Assert.That(() => TerminalCommand.NodeShell(node, DebugContainers.DefaultImage)).Throws<ArgumentException>();
    }

    [Test]
    [Arguments("busybox; id")]
    [Arguments("busybox latest")]
    [Arguments("-busybox")]
    public async Task AnImageThatIsNotAReferenceIsRefused(string image)
    {
        await Assert.That(() => TerminalCommand.NodeShell("worker-1", image)).Throws<ArgumentException>();
    }

    /// <summary>
    /// The shell a hand-off runs: the one already known when there is one, cmd on a Windows
    /// node (Nano Server has no PowerShell), and bash-else-sh otherwise — one command, because
    /// a terminal can be handed only one where the pane probes three.
    /// </summary>
    [Test]
    public async Task TheShellIsTheKnownOneThenCmdOnWindowsThenBashElseSh()
    {
        await Assert.That(TerminalCommand.ShellFor(PodOperatingSystem.Linux, " /bin/zsh ")).IsEquivalentTo(new[] { "/bin/zsh" });
        await Assert.That(TerminalCommand.ShellFor(PodOperatingSystem.Windows, null)).IsEquivalentTo(new[] { "cmd" });
        await Assert.That(TerminalCommand.ShellFor(PodOperatingSystem.Unknown, ""))
            .IsEquivalentTo(new[] { "/bin/sh", "-c", TerminalCommand.ShellProbe });

        // And what it builds passes the check: the default is a valid command, spaces and all.
        var exec = TerminalCommand.Exec("payments", "api", "app", TerminalCommand.ShellFor(PodOperatingSystem.Linux, null));
        await Assert.That(exec.Summary).EndsWith($"-- /bin/sh -c '{TerminalCommand.ShellProbe}'");
    }
}
