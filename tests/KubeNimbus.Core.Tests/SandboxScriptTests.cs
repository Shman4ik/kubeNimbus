using System.Diagnostics;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// #383: the sandbox scripts' default kubeconfig follows the container name, with one rule in
/// all four of them. <c>sandbox-up.ps1 -Name kubenimbus-argo</c> without <c>-Kubeconfig</c>
/// wrote the Argo cluster's kubeconfig over the main sandbox's <c>.sandbox/kubeconfig.yaml</c>,
/// so the live tests and the QA seed talked to the wrong cluster; #379 was the same hazard on the
/// teardown side. Each script is run with its print-the-path dry run, which resolves the path and
/// exits before it touches Docker. The PowerShell pair runs wherever <c>pwsh</c> is on PATH; the
/// shell pair only off Windows, where <c>bash</c> on PATH can be WSL's rather than a POSIX shell
/// that sees this checkout.
/// </summary>
public class SandboxScriptTests
{
    [Test]
    [Arguments("sandbox-up.ps1", "kubenimbus-sandbox", "kubeconfig.yaml")]
    [Arguments("sandbox-up.ps1", "kubenimbus-argo", "argo-kubeconfig.yaml")]
    [Arguments("sandbox-up.ps1", "kubenimbus-sandbox-b", "sandbox-b-kubeconfig.yaml")]
    [Arguments("sandbox-up.ps1", "scratch", "scratch-kubeconfig.yaml")]
    [Arguments("sandbox-down.ps1", "kubenimbus-sandbox", "kubeconfig.yaml")]
    [Arguments("sandbox-down.ps1", "kubenimbus-argo", "argo-kubeconfig.yaml")]
    [Arguments("sandbox-down.ps1", "kubenimbus-sandbox-b", "sandbox-b-kubeconfig.yaml")]
    [Timeout(60_000)]
    public async Task The_PowerShell_scripts_default_kubeconfig_follows_the_name(
        string script, string name, string file, CancellationToken ct)
    {
        var printed = await RunAsync("pwsh", ct, "-NoProfile", "-NonInteractive", "-File", ScriptPath(script),
            "-Name", name, "-PrintKubeconfigPath");

        await Assert.That(Normalise(printed)).IsEqualTo(Normalise(Path.Combine(RepositoryRoot(), ".sandbox", file)));
    }

    [Test]
    [Arguments("sandbox-up.sh", "kubenimbus-sandbox", "kubeconfig.yaml")]
    [Arguments("sandbox-up.sh", "kubenimbus-argo", "argo-kubeconfig.yaml")]
    [Arguments("sandbox-up.sh", "kubenimbus-sandbox-b", "sandbox-b-kubeconfig.yaml")]
    [Arguments("sandbox-down.sh", "kubenimbus-sandbox", "kubeconfig.yaml")]
    [Arguments("sandbox-down.sh", "kubenimbus-argo", "argo-kubeconfig.yaml")]
    [Arguments("sandbox-down.sh", "kubenimbus-sandbox-b", "sandbox-b-kubeconfig.yaml")]
    [Timeout(60_000)]
    public async Task The_shell_scripts_default_kubeconfig_follows_the_name(
        string script, string name, string file, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test("bash on a Windows PATH can be WSL's; the shell scripts are checked on Linux and macOS.");
        }

        var printed = await RunAsync("bash", ct, ScriptPath(script), "--name", name, "--print-kubeconfig-path");

        await Assert.That(Normalise(printed)).IsEqualTo(Normalise(Path.Combine(RepositoryRoot(), ".sandbox", file)));
    }

    [Test]
    [Timeout(60_000)]
    public async Task An_explicit_kubeconfig_is_kept_whatever_the_name(CancellationToken ct)
    {
        var mine = Path.Combine(Path.GetTempPath(), "kn-sandbox-script-test.yaml");
        var printed = await RunAsync("pwsh", ct, "-NoProfile", "-NonInteractive", "-File", ScriptPath("sandbox-up.ps1"),
            "-Name", "kubenimbus-argo", "-Kubeconfig", mine, "-PrintKubeconfigPath");

        await Assert.That(Normalise(printed)).IsEqualTo(Normalise(mine));
    }

    private static async Task<string> RunAsync(string program, CancellationToken ct, params string[] arguments)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"{program} did not start.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Skip.Test($"{program} is not on PATH.");
            throw;
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{program} exited {process.ExitCode}: {await error}");
            }

            return (await output).Trim();
        }
    }

    private static string Normalise(string path) => Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));

    private static string ScriptPath(string script) => Path.Combine(RepositoryRoot(), "scripts", script);

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "KubeNimbus.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"No KubeNimbus.slnx above {AppContext.BaseDirectory}.");
    }
}
