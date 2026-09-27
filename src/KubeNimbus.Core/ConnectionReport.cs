using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using k8s;
using k8s.Exceptions;

namespace KubeNimbus.Core;

/// <summary>One labelled line of what a connect attempt was made with.</summary>
public sealed record ConnectionFact(string Label, string Value);

/// <summary>
/// Why a connect failed, for the content area to say in place of a blank pane: which step
/// failed, one sentence that names the cause, the exception's own text, what usually fixes
/// it, and the facts the attempt was made with.
/// </summary>
/// <param name="Step">The step that failed, in the order a connect runs them.</param>
/// <param name="Headline">The cause, as one sentence.</param>
/// <param name="Detail">The exception's own message, untouched — the thing to paste into an issue.</param>
/// <param name="Advice">What usually fixes this cause, or null when there is nothing general to say.</param>
/// <param name="Facts">Kubeconfig file, context, cluster, server, user, sign-in method, proxy.</param>
public sealed record ConnectionFailureReport(
    string Step,
    string Headline,
    string Detail,
    string? Advice,
    IReadOnlyList<ConnectionFact> Facts)
{
    /// <summary>The step as a phrase inside a sentence: "reaching the API server", acronyms kept.</summary>
    public string StepPhrase => Step.Length == 0 ? Step : char.ToLowerInvariant(Step[0]) + Step[1..];
}

/// <summary>
/// Builds <see cref="ConnectionFailureReport"/>s. Deterministic: the same exception and
/// the same kubeconfig always read the same way, and nothing here guesses beyond what the
/// exception type and the kubeconfig entry say.
/// </summary>
/// <remarks>
/// <para>
/// The facts are read from the kubeconfig again, after the failure, rather than carried
/// out of the connect: a connect can fail before it has a client (a plugin that cannot
/// start, a file that does not parse), which is precisely when the facts matter most.
/// </para>
/// <para>
/// Nothing secret is ever a fact. The sign-in method is named, never shown: "bearer token
/// in the kubeconfig", not the token; a plugin's command, not its arguments (which can
/// carry anything); a proxy URL with any <c>user:password@</c> removed.
/// </para>
/// </remarks>
public static class ConnectionReport
{
    /// <summary>Step names, in the order a connect runs them.</summary>
    public const string ReadingKubeconfig = "Reading the kubeconfig";
    public const string RunningPlugin = "Running the credential plugin";
    public const string ReachingServer = "Reaching the API server";
    public const string SettingUpTls = "Setting up TLS";
    public const string GoingThroughProxy = "Going through the proxy";
    public const string SigningIn = "Signing in";
    public const string AskingForVersion = "Asking the server for its version";

    /// <summary>
    /// The report for <paramref name="exception"/>, raised while connecting
    /// <paramref name="context"/>. Never throws: an unreadable kubeconfig becomes a fact.
    /// </summary>
    public static async Task<ConnectionFailureReport> CreateAsync(
        ClusterContext context, Exception exception, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exception);

        var (facts, server, pluginDirectories) = await DescribeAsync(context, cancellationToken).ConfigureAwait(false);
        return Explain(exception, facts, server, pluginDirectories);
    }

    /// <summary>What the connect was made with, read from the kubeconfig.</summary>
    internal static Task<(IReadOnlyList<ConnectionFact> Facts, string? Server, IReadOnlyList<string> PluginDirectories)> DescribeAsync(
        ClusterContext context, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            var facts = new List<ConnectionFact>
            {
                new("Kubeconfig", context.KubeconfigPath),
                new("Context", context.Name),
            };
            var directories = ExecPluginPath.ExtraDirectories;

            k8s.KubeConfigModels.K8SConfiguration config;
            string text;
            try
            {
                var file = new FileInfo(context.KubeconfigPath);
                config = await KubernetesClientConfiguration.LoadKubeConfigAsync(file).ConfigureAwait(false);
                text = await File.ReadAllTextAsync(file.FullName, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                facts.Add(new ConnectionFact("Could not read it", FirstLine(e.Message)));
                return ((IReadOnlyList<ConnectionFact>)facts, (string?)null, directories);
            }

            var (clusterName, user) = Kubeconfig.Entry(config, context.Name);
            var cluster = clusterName is null ? null : config.Clusters?.FirstOrDefault(c => c.Name == clusterName);
            var server = cluster?.ClusterEndpoint?.Server;

            facts.Add(new ConnectionFact("Cluster", clusterName ?? "(the context names no cluster)"));
            facts.Add(new ConnectionFact("Server", server ?? "(the cluster entry has no server)"));
            facts.Add(new ConnectionFact("User", user?.Name ?? "(the context names no user)"));
            facts.Add(new ConnectionFact("Signs in with", SignIn(user, Path.GetDirectoryName(Path.GetFullPath(context.KubeconfigPath)))));

            if (user?.UserCredentials?.ExternalExecution?.InstallHint is { Length: > 0 } hint)
            {
                facts.Add(new ConnectionFact("Plugin install hint", hint.Trim()));
            }

            if (clusterName is not null && KubeconfigProxy.Read(text, clusterName) is { } proxy)
            {
                facts.Add(new ConnectionFact("Proxy", KubeconfigProxy.Redact(proxy)));
            }

            return ((IReadOnlyList<ConnectionFact>)facts, server, directories);
        }, cancellationToken);

    /// <summary>
    /// How the user entry signs in, in words — never the credential itself. For an exec
    /// plugin, also where the command was found (or that it was not), because "not found"
    /// is the most common first-connect failure there is.
    /// </summary>
    internal static string SignIn(k8s.KubeConfigModels.User? user, string? kubeconfigDirectory)
    {
        var credentials = user?.UserCredentials;
        if (credentials?.ExternalExecution is { } exec)
        {
            var command = exec.Command ?? "";
            var resolved = ExecPluginPath.Resolve(
                command,
                kubeconfigDirectory,
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetEnvironmentVariable("PATHEXT"),
                OperatingSystem.IsWindows(),
                ExecPluginPath.ExtraDirectories);

            if (Path.IsPathFullyQualified(command))
            {
                return File.Exists(command)
                    ? $"credential plugin {command}"
                    : $"credential plugin {command} (no such file)";
            }

            return resolved is null
                ? $"credential plugin {command} (not found on PATH or in the usual install folders)"
                : $"credential plugin {command} (runs {resolved})";
        }

        if (credentials is null)
        {
            return "nothing — no user entry";
        }

        if (!string.IsNullOrEmpty(credentials.ClientCertificate) || !string.IsNullOrEmpty(credentials.ClientCertificateData))
        {
            return string.IsNullOrEmpty(credentials.ClientCertificate)
                ? "client certificate in the kubeconfig"
                : $"client certificate from {credentials.ClientCertificate}";
        }

        if (!string.IsNullOrEmpty(credentials.Token))
        {
            return "bearer token in the kubeconfig";
        }

        if (!string.IsNullOrEmpty(credentials.UserName))
        {
            return "username and password";
        }

        if (credentials.AuthProvider is { } provider)
        {
            return $"auth-provider \"{provider.Name}\"";
        }

        return "nothing — anonymous";
    }

    /// <summary>The classification itself, pure so each branch can be asserted.</summary>
    internal static ConnectionFailureReport Explain(
        Exception exception,
        IReadOnlyList<ConnectionFact> facts,
        string? server,
        IReadOnlyList<string> pluginDirectories)
    {
        var target = server is { Length: > 0 } ? server : "the server";
        var detail = Detail(exception);

        (string Step, string Headline, string? Advice) verdict = exception switch
        {
            ExecCredentialException e when e.Message.StartsWith("Could not run", StringComparison.Ordinal) => (
                RunningPlugin,
                e.Command is { Length: > 0 } command
                    ? $"The credential plugin \"{command}\" could not be started."
                    : "The kubeconfig's credential plugin could not be started.",
                pluginDirectories.Count > 0
                    ? $"Install it, or put its full path in the kubeconfig's exec command. kubeNimbus looked on this app's PATH and in {string.Join(", ", pluginDirectories)}."
                    : "Install it, or put its full path in the kubeconfig's exec command."),

            ExecCredentialException e => (
                RunningPlugin,
                e.Message,
                "Run the same command in a terminal. If it asks you to sign in, do that, then Retry — kubeNimbus runs it again on every attempt."),

            KubeconfigSetupException e => (ReadingKubeconfig, e.Message, null),

            KubeConfigException or YamlDotNet.Core.YamlException or FileNotFoundException or DirectoryNotFoundException
                or UnauthorizedAccessException => (
                ReadingKubeconfig,
                $"The kubeconfig entry could not be used: {FirstLine(exception.Message)}",
                null),

            KubernetesApiException { StatusCode: HttpStatusCode.Unauthorized } => (
                SigningIn,
                "The API server rejected the credentials (401 Unauthorized).",
                "They have most likely expired. Sign in again the way you normally do — aws sso login, az login, gcloud auth login — then Retry. kubeNimbus re-reads the kubeconfig on every attempt and keeps no copy of any credential."),

            KubernetesApiException { StatusCode: HttpStatusCode.Forbidden } => (
                SigningIn,
                "Signed in, but this user may not read the server's version (403 Forbidden).",
                "The credential is valid; the account behind it has almost no permissions on this cluster."),

            KubernetesApiException e => (AskingForVersion, FirstLine(e.Message), null),

            HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => (
                ReachingServer,
                $"{HostOf(server) ?? "The server's name"} does not resolve from this machine.",
                "A private cluster usually needs a VPN, or a proxy-url on the cluster entry in the kubeconfig."),

            HttpRequestException { HttpRequestError: HttpRequestError.ProxyTunnelError } => (
                GoingThroughProxy,
                $"The proxy refused or failed to open a tunnel to {target}.",
                "Check the cluster's proxy-url, and that the proxy (or the SSH tunnel behind it) is running."),

            HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => (
                SettingUpTls,
                $"The TLS handshake with {target} failed.",
                "The kubeconfig's certificate-authority may not be the one this server presents, or something between is intercepting TLS."),

            HttpRequestException e when Socket(e) is SocketError.ConnectionRefused => (
                ReachingServer,
                $"Nothing is accepting connections at {target}.",
                "Is the cluster running, and is this the right address? A stopped local cluster (kind, k3d, minikube, Docker Desktop) gives exactly this."),

            HttpRequestException e when Socket(e) is SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable => (
                ReachingServer,
                $"No route to {target}.",
                "A private cluster usually needs a VPN, or a proxy-url on the cluster entry in the kubeconfig."),

            HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } => (
                ReachingServer,
                $"Could not connect to {target}.",
                null),

            HttpRequestException e when e.Message.Contains("not as a Kubernetes API server", StringComparison.Ordinal) => (
                AskingForVersion,
                FirstLine(e.Message),
                null),

            TaskCanceledException or TimeoutException => (
                ReachingServer,
                $"No answer from {target} in time.",
                "A private cluster usually needs a VPN, or a proxy-url on the cluster entry in the kubeconfig."),

            AuthenticationException => (SettingUpTls, $"The TLS handshake with {target} failed.", null),

            _ => ("Connecting", FirstLine(exception.Message), null),
        };

        return new ConnectionFailureReport(verdict.Step, verdict.Headline, detail, verdict.Advice, facts);
    }

    /// <summary>The message, plus the innermost cause when it adds something ("Connection refused (127.0.0.1:1)").</summary>
    private static string Detail(Exception exception)
    {
        var message = exception.Message;
        var inner = exception.InnerException;
        while (inner?.InnerException is not null)
        {
            inner = inner.InnerException;
        }

        return inner is not null && !message.Contains(inner.Message, StringComparison.Ordinal)
            ? $"{message} ({inner.Message})"
            : message;
    }

    private static SocketError? Socket(Exception exception)
    {
        for (var e = exception.InnerException; e is not null; e = e.InnerException)
        {
            if (e is SocketException socket)
            {
                return socket.SocketErrorCode;
            }
        }

        return null;
    }

    private static string? HostOf(string? server) =>
        Uri.TryCreate(server, UriKind.Absolute, out var uri) ? uri.Host : null;

    private static string FirstLine(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message[..end];
    }
}
