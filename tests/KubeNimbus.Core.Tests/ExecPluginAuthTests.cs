using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Exec-plugin authentication, end to end, with no cloud account: a credential plugin is
/// any program that prints an <c>ExecCredential</c>, so a script is a complete fake, and a
/// loopback stand-in for the API server records which bearer token each request carried.
/// </summary>
/// <remarks>
/// Hard rule 4 and the README had both claimed exec-plugin auth works, and no test in the
/// repository contained a kubeconfig with an <c>exec:</c> block that <em>succeeded</em>. These
/// run on Windows (a <c>.cmd</c> plugin) and on Linux and macOS (a <c>sh</c> plugin).
/// </remarks>
public class ExecPluginAuthTests
{
    private const string FarFuture = "2999-01-01T00:00:00Z";
    private const string LongAgo = "2000-01-01T00:00:00Z";

    [Test]
    public async Task A_plugin_credential_reaches_the_api_server()
    {
        using var server = new ScriptedApiServer(AcceptAnyPluginToken);
        var plugin = TokenPlugin.Write(FarFuture);
        using var client = await ClusterClient.ConnectAsync(Context(Kubeconfig(server.Url, plugin.Path)));

        var version = await client.GetServerVersionAsync();

        await Assert.That(version.GitVersion).IsEqualTo("v1.31.0");
        await Assert.That(server.Requests.Single().Authorization).StartsWith("Bearer tok-");
    }

    [Test]
    public async Task An_expired_token_is_refreshed_by_running_the_plugin_again()
    {
        // expirationTimestamp in the past: the library's token provider must treat every
        // request as needing a fresh credential and run the plugin for it.
        using var server = new ScriptedApiServer(AcceptAnyPluginToken);
        var plugin = TokenPlugin.Write(LongAgo);
        using var client = await ClusterClient.ConnectAsync(Context(Kubeconfig(server.Url, plugin.Path)));

        await client.GetServerVersionAsync();
        await client.GetServerVersionAsync();

        var tokens = server.Requests.Select(r => r.Authorization).ToList();
        await Assert.That(tokens.Count).IsEqualTo(2);
        await Assert.That(tokens[1]).IsNotEqualTo(tokens[0]);
    }

    [Test]
    public async Task An_unexpired_token_is_reused_rather_than_rerunning_the_plugin()
    {
        // The control for the test above: without it, "a new token every request" could
        // just as well be a client that runs the plugin unconditionally.
        using var server = new ScriptedApiServer(AcceptAnyPluginToken);
        var plugin = TokenPlugin.Write(FarFuture);
        using var client = await ClusterClient.ConnectAsync(Context(Kubeconfig(server.Url, plugin.Path)));

        await client.GetServerVersionAsync();
        await client.GetServerVersionAsync();

        var tokens = server.Requests.Select(r => r.Authorization).ToList();
        await Assert.That(tokens[1]).IsEqualTo(tokens[0]);
    }

    [Test]
    public async Task A_bare_plugin_command_is_found_where_a_login_shell_would_look()
    {
        // FEAT-49: `command: <name>` with the plugin in a directory that is not on this
        // process's PATH — /opt/homebrew/bin for an app launched from the Dock. The
        // override stands in for that directory.
        using var server = new ScriptedApiServer(AcceptAnyPluginToken);
        var plugin = TokenPlugin.Write(FarFuture, fileName: "kubenimbus-bare-plugin");
        using var _ = ExecPluginPath.OverrideDirectories([Path.GetDirectoryName(plugin.Path)!]);

        using var client = await ClusterClient.ConnectAsync(Context(Kubeconfig(server.Url, "kubenimbus-bare-plugin")));
        var version = await client.GetServerVersionAsync();

        await Assert.That(version.GitVersion).IsEqualTo("v1.31.0");
        await Assert.That(server.Requests.Single().Authorization).StartsWith("Bearer tok-");
    }

    [Test]
    public async Task A_bare_plugin_command_found_nowhere_is_refused_by_name_before_it_is_started()
    {
        using var _ = ExecPluginPath.OverrideDirectories([]);

        var ex = await Assert.ThrowsAsync<ExecCredentialException>(
            () => ClusterClient.ConnectAsync(Context(Kubeconfig("http://127.0.0.1:1", "kubenimbus-bare-plugin-nowhere"))));

        await Assert.That(ex!.Message).Contains("kubenimbus-bare-plugin-nowhere");
    }

    [Test]
    public async Task Refreshing_credentials_reruns_the_plugin_and_later_requests_use_the_new_token()
    {
        // What Reconnect does: re-read the kubeconfig, run the plugin again, keep nothing.
        using var server = new ScriptedApiServer(AcceptAnyPluginToken);
        var plugin = TokenPlugin.Write(FarFuture);
        using var client = await ClusterClient.ConnectAsync(Context(Kubeconfig(server.Url, plugin.Path)));
        await client.GetServerVersionAsync();
        var runsBefore = plugin.Runs;

        await client.RefreshCredentialsAsync(force: true);
        await client.GetServerVersionAsync();

        var tokens = server.Requests.Select(r => r.Authorization).ToList();
        await Assert.That(plugin.Runs).IsGreaterThan(runsBefore);
        await Assert.That(tokens[1]).IsNotEqualTo(tokens[0]);
    }

    [Test]
    public async Task A_401_during_a_watch_reruns_the_plugin_and_the_watch_recovers()
    {
        // FEAT-53: the credential is revoked under a running watch (an SSO session
        // ending). The loop used to retry with the same dead token for ever; it now
        // re-runs the plugin, says so, relists, and carries on.
        var revokedUpTo = 0;
        using var server = new ScriptedApiServer(request =>
        {
            if (TokenNumber(request.Authorization) is not { } number || number <= Volatile.Read(ref revokedUpTo))
            {
                return ScriptedApiServer.Unauthorized();
            }

            if (request.Target.StartsWith("/version", StringComparison.Ordinal))
            {
                return new ScriptedResponse(200, ScriptedApiServer.VersionBody);
            }

            return request.Target.Contains("watch=true", StringComparison.Ordinal)
                ? new ScriptedResponse(200, "", Hold: true)
                : new ScriptedResponse(200, """{"kind":"ConfigMapList","apiVersion":"v1","metadata":{"resourceVersion":"7"},"items":[]}""");
        });

        var plugin = TokenPlugin.Write(FarFuture);
        using var client = await ClusterClient.ConnectAsync(Context(Kubeconfig(server.Url, plugin.Path)));
        await client.GetServerVersionAsync();
        Volatile.Write(ref revokedUpTo, plugin.Runs);

        var lost = new List<Exception>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var synced = false;
        await foreach (var evt in client.WatchResourceAsync(
            ConfigMaps, "default", connectionLost: ex => { lock (lost) lost.Add(ex); }, cancellationToken: timeout.Token))
        {
            if (evt.Type == ResourceEventType.Synced)
            {
                synced = true;
                break;
            }
        }

        await Assert.That(synced).IsTrue();
        await Assert.That(lost.OfType<WatchConnectionException>().Any(e => e.CredentialsRejected)).IsTrue();
        await Assert.That(lost[0].Message).Contains("401");
    }

    // --------------------------------------------------------------------- helpers

    private static readonly ResourceDescriptor ConfigMaps =
        new("", "v1", "ConfigMap", "configmaps", "configmap", Namespaced: true, ShortNames: ["cm"], Categories: []);

    private static ScriptedResponse AcceptAnyPluginToken(ScriptedRequest request) =>
        request.Authorization?.StartsWith("Bearer tok-", StringComparison.Ordinal) == true
            ? new ScriptedResponse(200, ScriptedApiServer.VersionBody)
            : ScriptedApiServer.Unauthorized();

    private static int? TokenNumber(string? authorization) =>
        authorization is not null && authorization.StartsWith("Bearer tok-", StringComparison.Ordinal)
        && int.TryParse(authorization["Bearer tok-".Length..], out var number)
            ? number
            : null;

    private static ClusterContext Context(string kubeconfig) => new("stub", "stub", null, "stub", kubeconfig);

    internal static string Kubeconfig(string server, string command)
    {
        var directory = Directory.CreateTempSubdirectory("kubenimbus-exec-auth").FullName;
        var path = Path.Combine(directory, "kubeconfig.yaml");
        File.WriteAllText(path, $"""
            apiVersion: v1
            kind: Config
            clusters:
            - name: stub
              cluster:
                server: {server}
            contexts:
            - name: stub
              context:
                cluster: stub
                user: stub
            current-context: stub
            users:
            - name: stub
              user:
                exec:
                  apiVersion: client.authentication.k8s.io/v1beta1
                  command: '{command.Replace("'", "''")}'
            """);
        return path;
    }

    /// <summary>
    /// A plugin that counts its own runs in a file and prints <c>tok-&lt;run&gt;</c>, so
    /// every run produces a different token and the count says how often it ran.
    /// </summary>
    internal sealed class TokenPlugin
    {
        private readonly string _counter;

        private TokenPlugin(string path, string counter)
        {
            Path = path;
            _counter = counter;
        }

        public string Path { get; }

        public int Runs => int.Parse(File.ReadAllText(_counter).Trim(), System.Globalization.CultureInfo.InvariantCulture);

        public static TokenPlugin Write(string expirationTimestamp, string fileName = "plugin")
        {
            var directory = Directory.CreateTempSubdirectory("kubenimbus-token-plugin").FullName;
            var counter = System.IO.Path.Combine(directory, "count.txt");
            File.WriteAllText(counter, "0");
            var credential =
                "{\"apiVersion\":\"client.authentication.k8s.io/v1beta1\",\"kind\":\"ExecCredential\",\"status\":{\"token\":\"tok-%N%\",\"expirationTimestamp\":\"" + expirationTimestamp + "\"}}";

            if (OperatingSystem.IsWindows())
            {
                var path = System.IO.Path.Combine(directory, fileName + ".cmd");
                File.WriteAllText(path,
                    "@echo off\r\n"
                    + $"set /p n=<\"{counter}\"\r\n"
                    + "set /a n=n+1\r\n"
                    + $">\"{counter}\" echo %n%\r\n"
                    + "echo " + credential.Replace("%N%", "%n%") + "\r\n");
                return new TokenPlugin(path, counter);
            }

            var script = System.IO.Path.Combine(directory, fileName);
            File.WriteAllText(script,
                "#!/bin/sh\n"
                + $"n=$(cat '{counter}')\n"
                + "n=$((n+1))\n"
                + $"echo \"$n\" > '{counter}'\n"
                + "printf '%s\\n' '" + credential.Replace("%N%", "'\"$n\"'") + "'\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new TokenPlugin(script, counter);
        }
    }
}
