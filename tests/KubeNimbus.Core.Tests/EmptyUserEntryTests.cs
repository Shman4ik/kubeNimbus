using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// #273: a context whose user entry carries no credential (<c>user: {}</c>, or no
/// <c>user:</c> key) connects anonymously, as kubectl does — behind <c>kubectl proxy</c> or an
/// authenticating proxy that is the normal shape. The library refused it with "does not have
/// appropriate auth credentials in kubeconfig".
/// </summary>
public class EmptyUserEntryTests
{
    [Test]
    [Arguments("  user: {}")]
    [Arguments("")]
    public async Task An_empty_user_entry_connects_with_no_credential(string userLine)
    {
        using var server = new ScriptedApiServer(_ => new ScriptedResponse(200, ScriptedApiServer.VersionBody));
        var kubeconfig = Write(server.Url, userLine);

        using var client = await ClusterClient.ConnectAsync(Context(kubeconfig));
        var version = await client.GetServerVersionAsync();

        await Assert.That(version.GitVersion).IsEqualTo("v1.31.0");
        await Assert.That(string.IsNullOrEmpty(server.Requests.Single().Authorization)).IsTrue();
    }

    [Test]
    [Arguments("  user: {}")]
    [Arguments("")]
    public async Task The_failure_view_says_the_user_entry_is_empty(string userLine)
    {
        var kubeconfig = Write("https://127.0.0.1:1", userLine);

        var report = await ConnectionReport.CreateAsync(Context(kubeconfig), new TimeoutException());

        await Assert.That(report.Facts.Single(f => f.Label == "Signs in with").Value)
            .IsEqualTo("no credential (the user entry is empty)");
    }

    private static ClusterContext Context(string kubeconfig) => new("anon", "anon", null, "none", kubeconfig);

    private static string Write(string server, string userLine)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("kubenimbus-empty-user").FullName, "kubeconfig.yaml");
        File.WriteAllText(path, $"""
            apiVersion: v1
            kind: Config
            clusters:
            - name: anon
              cluster:
                server: {server}
            contexts:
            - name: anon
              context:
                cluster: anon
                user: none
            users:
            - name: none
            {userLine}
            """);
        return path;
    }
}
