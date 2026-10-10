using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// ENG-56: a user entry's <c>tokenFile</c>. The client library's model has no field for it,
/// so such a context used to connect with no credential at all. Through the real connect
/// path — a temp kubeconfig, <see cref="ClusterClient.ConnectAsync"/>, a request — against a
/// loopback stand-in that records the <c>Authorization</c> header it receives.
/// </summary>
public class KubeconfigTokenFileTests
{
    private const string InlineToken = "kubenimbus-inline-token";

    [Test]
    public async Task A_relative_token_file_is_read_from_the_kubeconfigs_folder_and_sent()
    {
        using var server = Server();
        var kubeconfig = Kubeconfig(server.Url, tokenFile: "token", inlineToken: null);
        await File.WriteAllTextAsync(Beside(kubeconfig, "token"), "file-token-1\n");

        using var client = await ClusterClient.ConnectAsync(Context(kubeconfig));
        await client.GetServerVersionAsync();

        await Assert.That(server.Requests.Single().Authorization).IsEqualTo("Bearer file-token-1");
    }

    /// <summary>
    /// Re-read on every refresh: a token rotated on disk (a projected ServiceAccount token, a
    /// file a sign-in script rewrites) reaches the next request after a reconnect, and the
    /// first one is not kept anywhere.
    /// </summary>
    [Test]
    public async Task A_refresh_reads_the_file_again()
    {
        using var server = Server();
        var kubeconfig = Kubeconfig(server.Url, tokenFile: "token", inlineToken: null);
        var tokenPath = Beside(kubeconfig, "token");
        await File.WriteAllTextAsync(tokenPath, "file-token-1");

        using var client = await ClusterClient.ConnectAsync(Context(kubeconfig));
        await client.GetServerVersionAsync();
        await File.WriteAllTextAsync(tokenPath, "file-token-2");
        await client.RefreshCredentialsAsync(force: true);
        await client.GetServerVersionAsync();

        await Assert.That(string.Join(" | ", server.Requests.Select(r => r.Authorization)))
            .IsEqualTo("Bearer file-token-1 | Bearer file-token-2");
    }

    /// <summary>client-go's precedence: the file wins when it can be read, the inline token is the fallback.</summary>
    [Test]
    public async Task The_file_wins_over_an_inline_token_which_is_the_fallback_when_the_file_is_missing()
    {
        using var server = Server();
        var withFile = Kubeconfig(server.Url, tokenFile: "token", inlineToken: InlineToken);
        await File.WriteAllTextAsync(Beside(withFile, "token"), "file-token");
        var withoutFile = Kubeconfig(server.Url, tokenFile: "missing-token", inlineToken: InlineToken);

        using (var client = await ClusterClient.ConnectAsync(Context(withFile)))
        {
            await client.GetServerVersionAsync();
        }

        using (var client = await ClusterClient.ConnectAsync(Context(withoutFile)))
        {
            await client.GetServerVersionAsync();
        }

        await Assert.That(string.Join(" | ", server.Requests.Select(r => r.Authorization)))
            .IsEqualTo($"Bearer file-token | Bearer {InlineToken}");
    }

    /// <summary>
    /// With nothing to fall back on, a missing or empty file fails the connect at "Reading
    /// the kubeconfig", naming the file — never connecting anonymously.
    /// </summary>
    [Test]
    [Arguments(null)]
    [Arguments("  \n")]
    public async Task A_missing_or_empty_file_with_no_inline_token_fails_at_reading_the_kubeconfig(string? content)
    {
        using var server = Server();
        var kubeconfig = Kubeconfig(server.Url, tokenFile: "token", inlineToken: null);
        if (content is not null)
        {
            await File.WriteAllTextAsync(Beside(kubeconfig, "token"), content);
        }

        var failure = await Assert.ThrowsAsync<KubeconfigSetupException>(() => ClusterClient.ConnectAsync(Context(kubeconfig)));

        await Assert.That(failure!.Message).Contains(Beside(kubeconfig, "token"));
        await Assert.That(server.Requests).IsEmpty();
        var report = await ConnectionReport.CreateAsync(Context(kubeconfig), failure);
        await Assert.That(report.Step).IsEqualTo(ConnectionReport.ReadingKubeconfig);
    }

    /// <summary>The failure view names the file, and never what is in it.</summary>
    [Test]
    public async Task The_report_names_the_token_file_and_never_its_content()
    {
        using var server = Server();
        var kubeconfig = Kubeconfig(server.Url, tokenFile: "token", inlineToken: null);
        await File.WriteAllTextAsync(Beside(kubeconfig, "token"), "secret-in-the-file");

        var report = await ConnectionReport.CreateAsync(Context(kubeconfig), new TimeoutException());
        var signIn = report.Facts.Single(f => f.Label == "Signs in with").Value;

        await Assert.That(signIn).IsEqualTo($"bearer token from the file {Beside(kubeconfig, "token")}");
        await Assert.That(report.Facts.Any(f => f.Value.Contains("secret-in-the-file", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task The_reader_keeps_the_path_by_user_entry()
    {
        var document = KubeconfigReader.Parse("""
            apiVersion: v1
            kind: Config
            users:
            - name: a
              user:
                tokenFile: /var/run/token
            - name: b
              user:
                token: inline
            """);

        await Assert.That(document.TokenFileOf("a")).IsEqualTo("/var/run/token");
        await Assert.That(document.TokenFileOf("b")).IsNull();
    }

    // --------------------------------------------------------------------- helpers

    private static ScriptedApiServer Server() => new(_ => new ScriptedResponse(200, ScriptedApiServer.VersionBody));

    private static ClusterContext Context(string kubeconfig) => new("tf", "tf", null, "tf", kubeconfig);

    private static string Beside(string kubeconfig, string name) => Path.Combine(Path.GetDirectoryName(kubeconfig)!, name);

    private static string Kubeconfig(string server, string tokenFile, string? inlineToken)
    {
        var directory = Directory.CreateTempSubdirectory("kubenimbus-tokenfile").FullName;
        var path = Path.Combine(directory, "kubeconfig.yaml");
        var inline = inlineToken is null ? "" : $"\n    token: {inlineToken}";
        File.WriteAllText(path, $"""
            apiVersion: v1
            kind: Config
            clusters:
            - name: tf
              cluster:
                server: {server}
            contexts:
            - name: tf
              context:
                cluster: tf
                user: tf
            current-context: tf
            users:
            - name: tf
              user:
                tokenFile: {tokenFile}{inline}
            """);
        return path;
    }
}
