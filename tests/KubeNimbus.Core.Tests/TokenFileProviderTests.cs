using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// #262: while connected the file is re-read at most once a minute, as client-go's caching
/// file token source does, so a token rotated without the old one being revoked is still
/// picked up without a 401 — and without rebuilding the client, which would retire it.
/// </summary>
public class TokenFileProviderTests
{
    [Test]
    public async Task The_file_is_read_again_once_a_minute_and_a_failed_read_keeps_the_last_token()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("kubenimbus-tokenfile-provider").FullName, "token");
        await File.WriteAllTextAsync(path, "rotated-1");
        var clock = new ManualClock();
        var provider = new TokenFileProvider(path, "rotated-1", clock);

        async Task<string?> Header() => (await provider.GetAuthenticationHeaderAsync(CancellationToken.None)).Parameter;

        await File.WriteAllTextAsync(path, "rotated-2");
        clock.Advance(TimeSpan.FromSeconds(59));
        await Assert.That(await Header()).IsEqualTo("rotated-1");

        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(await Header()).IsEqualTo("rotated-2");

        // A file caught mid-rewrite (empty), or gone, keeps the token that was valid.
        await File.WriteAllTextAsync(path, "");
        clock.Advance(TokenFileProvider.Period);
        await Assert.That(await Header()).IsEqualTo("rotated-2");
        File.Delete(path);
        clock.Advance(TokenFileProvider.Period);
        await Assert.That(await Header()).IsEqualTo("rotated-2");
    }

    [Test]
    public async Task A_token_file_user_gets_the_provider_and_an_inline_token_does_not()
    {
        var directory = Directory.CreateTempSubdirectory("kubenimbus-tokenfile-provider").FullName;
        await File.WriteAllTextAsync(Path.Combine(directory, "token"), "from-file");
        var withFile = await Write(directory, "with-file.yaml", "tokenFile: token");
        var inline = await Write(directory, "inline.yaml", "token: inline");

        var fromFile = await Kubeconfig.BuildClientConfigAsync(new ClusterContext("tf", "tf", null, "tf", withFile));
        var fromInline = await Kubeconfig.BuildClientConfigAsync(new ClusterContext("tf", "tf", null, "tf", inline));

        await Assert.That(fromFile.TokenProvider is TokenFileProvider).IsTrue();
        await Assert.That(fromInline.TokenProvider).IsNull();
    }

    private static async Task<string> Write(string directory, string name, string credential)
    {
        var path = Path.Combine(directory, name);
        await File.WriteAllTextAsync(path, $"""
            apiVersion: v1
            kind: Config
            clusters:
            - name: tf
              cluster:
                server: https://127.0.0.1:1
            contexts:
            - name: tf
              context:
                cluster: tf
                user: tf
            users:
            - name: tf
              user:
                {credential}
            """);
        return path;
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
