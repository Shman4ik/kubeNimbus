using System.Net.Http.Headers;
using System.Text;
using k8s;
using k8s.Authentication;
using k8s.Exceptions;
using k8s.KubeConfigModels;

namespace KubeNimbus.Core;

/// <summary>
/// Runs a kubeconfig user's exec credential plugin once per client build and hands what it
/// printed to the client's first request, instead of letting the library run it twice
/// (#283).
/// </summary>
/// <remarks>
/// <para>
/// <c>BuildConfigFromConfigObject</c> runs the plugin to build the configuration and then
/// installs an <see cref="ExecTokenProvider"/> that starts empty, so the first request ran it
/// again: two process starts per connect, often two network round trips, and for an
/// interactive plugin two prompts. Here the plugin runs before the configuration is built,
/// its token (or client certificate) goes into the in-memory model as if the kubeconfig had
/// carried it, the exec entry is taken out of that model so the library does not run it a
/// second time, and the configuration's token provider is replaced by
/// <see cref="SeededExecTokenProvider"/>, which starts from this run's credential.
/// </para>
/// <para>
/// Hard rule 4 holds: the credential lives in the in-memory model for the length of the
/// build and in the provider inside the client afterwards, exactly where the library's own
/// provider kept it. Nothing is written anywhere, and every connect, reconnect and 401
/// refresh rebuilds the client and runs the plugin again.
/// </para>
/// </remarks>
internal static class ExecCredentialProvider
{
    /// <summary>
    /// Runs <paramref name="credentials"/>' plugin, writes its credential into the model and
    /// removes the exec entry from it. Returns what the configuration's token provider must
    /// be built from, or null when the plugin returned no token (a certificate-only plugin,
    /// which the library never gave a provider either).
    /// </summary>
    /// <remarks>Blocks while the plugin runs; called on the pool, inside
    /// <see cref="ExecCredentialCapture.RunAsync{T}"/>, like the library's own run was.</remarks>
    public static SeededExecTokenProvider? RunInto(UserCredentials credentials)
    {
        var exec = credentials.ExternalExecution!;

        // The library's own check, which it made before running the plugin.
        if (string.IsNullOrWhiteSpace(exec.ApiVersion))
        {
            throw new KubeConfigException("External command execution missing ApiVersion key");
        }

        var response = KubernetesClientConfiguration.ExecuteExternalCommand(exec);
        var status = response.Status;

        // As the library did: what the plugin returns replaces the entry's own token, and a
        // certificate is base64 of the PEM text, the encoding the model carries from a file.
        credentials.ExternalExecution = null;
        credentials.Token = status?.Token;
        if (!string.IsNullOrEmpty(status?.ClientCertificateData))
        {
            credentials.ClientCertificateData = Convert.ToBase64String(Encoding.ASCII.GetBytes(status.ClientCertificateData));
            credentials.ClientCertificate = null;
        }

        if (!string.IsNullOrEmpty(status?.ClientKeyData))
        {
            credentials.ClientKeyData = Convert.ToBase64String(Encoding.ASCII.GetBytes(status.ClientKeyData));
            credentials.ClientKey = null;
        }

        return string.IsNullOrEmpty(status?.Token) ? null : new SeededExecTokenProvider(exec, response);
    }
}

/// <summary>
/// The library's <see cref="ExecTokenProvider"/>, except that it starts from the credential
/// the client build already obtained. It runs the plugin again when that credential is
/// within <see cref="ExpirySkew"/> of its <c>expirationTimestamp</c>, as the library's did;
/// a credential with no expiry is kept until a 401, which rebuilds the client
/// (<see cref="ClusterClient.RefreshCredentialsAsync"/>).
/// </summary>
internal sealed class SeededExecTokenProvider(ExternalExecution exec, ExecCredentialResponse first) : ITokenProvider
{
    /// <summary>How long before its expiry a credential is replaced.</summary>
    internal static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ExecCredentialResponse _response = first;

    public async Task<AuthenticationHeaderValue> GetAuthenticationHeaderAsync(CancellationToken cancellationToken)
    {
        var response = Volatile.Read(ref _response);
        if (NeedsRefresh(response, DateTime.UtcNow))
        {
            // One run for requests that find it expired together; each later one re-checks.
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                response = _response;
                if (NeedsRefresh(response, DateTime.UtcNow))
                {
                    response = await Task.Run(() => KubernetesClientConfiguration.ExecuteExternalCommand(exec), cancellationToken)
                        .ConfigureAwait(false);
                    Volatile.Write(ref _response, response);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        return new AuthenticationHeaderValue("Bearer", response.Status?.Token);
    }

    internal static bool NeedsRefresh(ExecCredentialResponse response, DateTime utcNow)
    {
        if (response.Status is not { } status || string.IsNullOrEmpty(status.Token))
        {
            return true;
        }

        if (status.ExpirationTimestamp is not { } expiry)
        {
            return false;
        }

        var expiryUtc = expiry.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(expiry, DateTimeKind.Utc)
            : expiry.ToUniversalTime();
        return utcNow + ExpirySkew >= expiryUtc;
    }
}
