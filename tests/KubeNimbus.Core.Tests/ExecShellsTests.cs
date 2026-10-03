using System.Text.Json;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Which shells the exec pane tries, and when it says "this image has no shell" rather
/// than passing on whatever the last attempt said. A Windows node used to get
/// <c>/bin/bash</c>, <c>/bin/sh</c> and <c>/bin/ash</c> — three certain failures — and a
/// 403 was reported as a missing <c>/bin/ash</c> because ash happened to be tried last.
/// </summary>
public class ExecShellsTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Test]
    public async Task A_linux_pod_tries_bash_sh_then_ash()
    {
        await Assert.That(ExecShells.Candidates(PodOperatingSystem.Linux))
            .IsEquivalentTo(new[] { "/bin/bash", "/bin/sh", "/bin/ash" });
    }

    [Test]
    public async Task A_windows_pod_tries_powershell_then_cmd()
    {
        await Assert.That(ExecShells.Candidates(PodOperatingSystem.Windows))
            .IsEquivalentTo(new[] { "powershell", "cmd" });
    }

    [Test]
    public async Task An_unknown_os_tries_linux_shells_first_then_windows_ones()
    {
        var candidates = ExecShells.Candidates(PodOperatingSystem.Unknown);
        await Assert.That(candidates).IsEquivalentTo(new[] { "/bin/bash", "/bin/sh", "/bin/ash", "powershell", "cmd" });
        await Assert.That(candidates[0]).IsEqualTo("/bin/bash");
    }

    [Test]
    public async Task The_pods_own_os_field_wins_over_its_node_selector()
    {
        var pod = Json("""{"spec":{"os":{"name":"windows"},"nodeSelector":{"kubernetes.io/os":"linux"}}}""");
        await Assert.That(ExecShells.FromPod(pod)).IsEqualTo(PodOperatingSystem.Windows);
    }

    [Test]
    public async Task The_node_selector_says_windows_when_the_os_field_is_absent()
    {
        var pod = Json("""{"spec":{"nodeSelector":{"kubernetes.io/os":"Windows"}}}""");
        await Assert.That(ExecShells.FromPod(pod)).IsEqualTo(PodOperatingSystem.Windows);
    }

    [Test]
    public async Task A_pod_that_says_nothing_is_unknown()
    {
        await Assert.That(ExecShells.FromPod(Json("""{"spec":{"containers":[]}}"""))).IsEqualTo(PodOperatingSystem.Unknown);
        await Assert.That(ExecShells.FromPod(Json("""{"metadata":{}}"""))).IsEqualTo(PodOperatingSystem.Unknown);
        await Assert.That(ExecShells.FromPod(Json("""{"spec":{"os":{"name":"plan9"}}}"""))).IsEqualTo(PodOperatingSystem.Unknown);
    }

    [Test]
    public async Task The_node_label_names_the_os()
    {
        var node = Json("""{"metadata":{"name":"win-1","labels":{"kubernetes.io/os":"windows"}}}""");
        await Assert.That(ExecShells.FromNode(node)).IsEqualTo(PodOperatingSystem.Windows);
        await Assert.That(ExecShells.FromNode(Json("""{"metadata":{"name":"n"}}"""))).IsEqualTo(PodOperatingSystem.Unknown);
    }

    /// <summary>The runtimes' own sentences, verbatim from runc/containerd and hcsshim.</summary>
    [Test]
    [Arguments("Internal error occurred: error executing command in container: failed to exec in container: failed to start exec \"0d6a\": OCI runtime exec failed: exec failed: unable to start container process: exec: \"/bin/ash\": stat /bin/ash: no such file or directory: unknown")]
    [Arguments("OCI runtime exec failed: exec failed: unable to start container process: exec: \"sh\": executable file not found in $PATH: unknown")]
    [Arguments("failed to exec in container: hcs::System::CreateProcess powershell: The system cannot find the file specified.")]
    public async Task A_missing_executable_is_recognized(string message)
    {
        await Assert.That(ExecShells.IsMissingExecutable(message)).IsTrue();
    }

    [Test]
    [Arguments("pods \"web-0\" is forbidden: User \"dev\" cannot create resource \"pods/exec\" in API group \"\" in the namespace \"shop\"")]
    [Arguments("container not found (\"web\")")]
    [Arguments("unable to upgrade connection: container web not found in pod web-0_shop")]
    [Arguments("command terminated with exit code 137")]
    [Arguments(null)]
    public async Task Any_other_failure_is_not_a_missing_shell(string? message)
    {
        await Assert.That(ExecShells.IsMissingExecutable(message)).IsFalse();
    }
}
