using System.Text;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// VER-21: the exec transport against a real container. The pane was rebuilt around a VT
/// emulator whose own columns and rows now drive <see cref="ExecSession.ResizeAsync"/>, and
/// not one byte had crossed a WebSocket through it. These drive the session the pane
/// drives — the same <see cref="ClusterClient.ExecAsync"/>, the same resize message — and
/// read what the container saw. The emulator and the keyboard stay the GUI half of the row.
/// </summary>
public class ExecLiveTests
{
    private static async Task<string> ShellPodAsync(ClusterClient client, CancellationToken ct)
    {
        var name = LiveCluster.Named("shell");
        await LiveCluster.ApplyAsync(client, LiveCluster.Deployments, name,
            LiveCluster.DeploymentYaml(name, 1, "trap 'exit 0' TERM; sleep 3600 & wait $!"), ct);
        await LiveCluster.WaitForReadyPodsAsync(client, name, 1, ct);
        return (await LiveCluster.PodsOfAsync(client, name, ct)).Single().Name;
    }

    private static readonly SemaphoreSlim PodGate = new(1, 1);
    private static string? _pod;

    /// <summary>One shell pod for the class; each test opens its own session in it.</summary>
    private static async Task<string> PodAsync(ClusterClient client, CancellationToken ct)
    {
        await PodGate.WaitAsync(ct);
        try
        {
            return _pod ??= await ShellPodAsync(client, ct);
        }
        finally
        {
            PodGate.Release();
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task A_command_without_a_tty_returns_its_stdout_stderr_and_a_clean_status(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var pod = await PodAsync(client, ct);

        using var session = await client.ExecAsync(
            LiveCluster.Namespace, pod, "app", ["sh", "-c", "echo to-stdout; echo to-stderr >&2"], tty: false, cancellationToken: ct);
        var status = session.ReadTerminalStatusAsync(ct);
        var stdout = ReadAllAsync(session.StdOut, ct);
        var stderr = ReadAllAsync(session.StdErr!, ct);

        await Assert.That(await status.WaitAsync(TimeSpan.FromSeconds(30), ct)).IsNull();
        await Assert.That((await stdout.WaitAsync(TimeSpan.FromSeconds(10), ct)).Trim()).IsEqualTo("to-stdout");
        await Assert.That((await stderr.WaitAsync(TimeSpan.FromSeconds(10), ct)).Trim()).IsEqualTo("to-stderr");
    }

    /// <summary>
    /// A failed command arrives on the error channel, not on stdout — this is the text the
    /// pane prints instead of sitting on a blank terminal.
    /// </summary>
    [Test]
    [Arguments("exit-code")]
    [Arguments("no-binary")]
    [Timeout(120_000)]
    public async Task A_failed_command_reports_the_servers_reason(string failure, CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var pod = await PodAsync(client, ct);
        IReadOnlyList<string> command = failure == "exit-code" ? ["sh", "-c", "exit 3"] : ["/no/such/shell"];

        string? reason;
        try
        {
            using var session = await client.ExecAsync(LiveCluster.Namespace, pod, "app", command, tty: false, cancellationToken: ct);
            reason = await session.ReadTerminalStatusAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A runtime may refuse the upgrade itself rather than report on channel 3;
            // either way the reason has to be the server's.
            reason = ex.Message;
        }

        await Assert.That(reason).IsNotNull();
        if (failure == "exit-code")
        {
            await Assert.That(reason!).Contains("exit code 3");
        }
        else
        {
            await Assert.That(reason!).Contains("no such file or directory");
        }
    }

    /// <summary>
    /// The coupling the rebuild made new: a resize the pane sends is what <c>stty size</c>
    /// inside the container reports, and a second resize follows. Then Ctrl+C (the byte
    /// 0x03, which the pane passes through as the terminal's own) interrupts a real
    /// <c>tail -f</c> and the shell prompt comes back.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task A_resize_reaches_stty_size_and_ctrl_c_interrupts_a_running_command(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var pod = await PodAsync(client, ct);

        using var session = await client.ExecAsync(LiveCluster.Namespace, pod, "app", ["sh"], tty: true, cancellationToken: ct);
        var screen = new StringBuilder();
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            try
            {
                int read;
                while ((read = await session.StdOut.ReadAsync(buffer, readCts.Token)) > 0)
                {
                    lock (screen)
                    {
                        screen.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // ended by the test
            }
        }, readCts.Token);

        async Task SendAsync(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await session.StdIn.WriteAsync(bytes, ct);
            await session.StdIn.FlushAsync(ct);
        }

        // Output that the typed command line cannot contain: the shell computes it.
        Task Expect(string marker) => LiveCluster.WaitUntilAsync(
            () => { lock (screen) { return Task.FromResult(screen.ToString().Contains(marker, StringComparison.Ordinal)); } },
            TimeSpan.FromSeconds(20), $"\"{marker}\" on the terminal", ct);

        await session.ResizeAsync(123, 45, ct);
        await SendAsync("echo SIZE=$(stty size | tr ' ' x)=\n");
        await Expect("SIZE=45x123=");

        await session.ResizeAsync(80, 24, ct);
        await SendAsync("echo SIZE=$(stty size | tr ' ' x)=\n");
        await Expect("SIZE=24x80=");

        await SendAsync("tail -f /dev/null\n");
        await Task.Delay(500, ct);
        await SendAsync("\u0003");
        await SendAsync("echo AFTER=$((40+2))\n");
        await Expect("AFTER=42");

        await SendAsync("exit\n");
        await Assert.That(await session.ReadTerminalStatusAsync(ct).WaitAsync(TimeSpan.FromSeconds(20), ct)).IsNull();
        await readCts.CancelAsync();
        await pump;
    }

    private static async Task<string> ReadAllAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }
}
