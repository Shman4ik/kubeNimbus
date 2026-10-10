using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using k8s;
using k8s.KubeConfigModels;

namespace KubeNimbus.Core;

/// <summary>
/// Kubeconfig discovery and loading. Kubeconfig is the single source of truth:
/// $KUBECONFIG (path-separator list) plus the default ~/.kube/config.
/// </summary>
public static partial class Kubeconfig
{
    /// <summary>
    /// The source label <see cref="CandidatePaths"/> stamps on a path the user
    /// chose through the app's file picker, so the empty state's "Searched:" list
    /// distinguishes it from the two locations the app looks in on its own.
    /// </summary>
    public const string PickedSource = "picked";

    /// <summary>The source label of a picked <em>folder</em> — the entry for the folder itself.</summary>
    public const string PickedFolderSource = "picked folder";

    /// <summary>The source label of a kubeconfig found inside a picked folder.</summary>
    public const string InPickedFolderSource = "in picked folder";

    /// <summary>
    /// Files larger than this in a picked folder are not considered kubeconfigs. A real
    /// one with hundreds of contexts is well under it; a folder that also holds a log or
    /// an archive should not cost a read of the whole thing on every rescan.
    /// </summary>
    private const long MaxFolderFileBytes = 1024 * 1024;

    /// <summary>
    /// When set, replaces the machine's own search — <c>$KUBECONFIG</c> and
    /// <c>~/.kube/config</c> — with these paths. For test harnesses only, and for a
    /// stronger reason than the stores' <c>DirectoryOverride</c>: without it a view-model
    /// test that builds the shell reads the developer's real kubeconfig and opens a tab
    /// on its current context, which connects to their real cluster and can run its
    /// credential plugin. User-picked paths are still searched first.
    /// </summary>
    public static IReadOnlyList<string>? EnvironmentSearchOverride { get; set; }

    /// <summary>
    /// Kubeconfig file paths in precedence order: any user-picked file, then every
    /// entry of $KUBECONFIG, then ~/.kube/config. Only existing files are returned —
    /// which is what makes a picked file that has since been moved or deleted
    /// degrade to "no contexts" rather than to an exception.
    /// </summary>
    /// <param name="extraPaths">
    /// Files the user pointed the app at explicitly (the "Open kubeconfig file…"
    /// picker). Only the <em>path</em> is ever kept — the file is re-read through
    /// this chain at connect time like any other, so no credential is copied
    /// anywhere (CLAUDE.md rule #4).
    /// </param>
    public static IReadOnlyList<string> DiscoverPaths(IEnumerable<string>? extraPaths = null) =>
        [.. CandidatePaths(extraPaths).Where(c => c.Exists && !c.IsFolder).Select(c => c.Path).Distinct()];

    /// <summary>
    /// The same search, including paths that don't exist — so a UI with no
    /// contexts can say *where* it looked instead of just "none found". A picked
    /// path that has gone away is reported here as missing, which is the whole
    /// reason it is listed rather than silently dropped.
    /// </summary>
    public static IReadOnlyList<KubeconfigCandidate> CandidatePaths(IEnumerable<string>? extraPaths = null)
    {
        var candidates = new List<KubeconfigCandidate>();

        // First, so a file the user explicitly chose wins on duplicate context
        // names — an explicit choice outranks whatever the environment happened
        // to be carrying.
        foreach (var path in extraPaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path)
                || candidates.Any(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // A picked folder is expanded here, on every search, which is what makes a
            // file dropped into it later appear on the next rescan without being picked
            // itself. The folder is still only a path in settings (rule 4).
            if (Directory.Exists(path))
            {
                candidates.Add(new KubeconfigCandidate(path, true, PickedFolderSource, IsFolder: true));
                foreach (var file in FolderKubeconfigs(path))
                {
                    if (!candidates.Any(c => string.Equals(c.Path, file, StringComparison.OrdinalIgnoreCase)))
                    {
                        candidates.Add(new KubeconfigCandidate(file, true, InPickedFolderSource));
                    }
                }

                continue;
            }

            candidates.Add(new KubeconfigCandidate(path, File.Exists(path), PickedSource));
        }

        if (EnvironmentSearchOverride is { } seeded)
        {
            foreach (var path in seeded)
            {
                candidates.Add(new KubeconfigCandidate(path, File.Exists(path), "override"));
            }

            return candidates;
        }

        var env = Environment.GetEnvironmentVariable("KUBECONFIG");
        if (!string.IsNullOrWhiteSpace(env))
        {
            foreach (var path in env.Split(
                Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!candidates.Any(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    candidates.Add(new KubeconfigCandidate(path, File.Exists(path), "$KUBECONFIG"));
                }
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultPath = Path.Combine(home, ".kube", "config");
        if (!candidates.Any(c => string.Equals(c.Path, defaultPath, StringComparison.OrdinalIgnoreCase)))
        {
            candidates.Add(new KubeconfigCandidate(defaultPath, File.Exists(defaultPath), "default location"));
        }

        return candidates;
    }

    /// <summary>
    /// The kubeconfig files directly inside <paramref name="directory"/>, in ordinal name
    /// order so the first-file-wins rule for duplicate context names is the same on every
    /// machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Top level only. The folder people point this at is most often <c>~/.kube</c>, whose
    /// <c>cache/</c> holds thousands of discovery and HTTP cache files; walking that on
    /// every rescan would be the cost of a feature nobody asked for.
    /// </para>
    /// <para>
    /// A file is taken when it says it is a kubeconfig — a <c>kind: Config</c> line, in YAML
    /// or JSON spelling. <c>~/.kube</c> also holds <c>kubectx</c>'s one-line state file, lock
    /// files and editor backups; offering those to the parser would turn each into a
    /// "could not read" line on every rescan. A file that does say <c>kind: Config</c> and
    /// then fails to parse is a real kubeconfig that is broken, and is reported as one.
    /// Hidden files are skipped for the same reason as the rest.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> FolderKubeconfigs(string directory)
    {
        try
        {
            return [.. Directory.EnumerateFiles(directory)
                .Where(f => !Path.GetFileName(f).StartsWith('.') && LooksLikeKubeconfig(f))
                .Order(StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool LooksLikeKubeconfig(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return info.Length is > 0 and <= MaxFolderFileBytes && KindConfigLine().IsMatch(File.ReadAllText(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [GeneratedRegex("""^\s*["']?kind["']?\s*:\s*["']?Config["']?\s*,?\s*$""", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex KindConfigLine();

    /// <summary>
    /// A cheap summary of everything the kubeconfig search would read — each candidate
    /// file's size and write time, and every file directly inside a picked folder — so a
    /// caller can tell whether a rescan would find anything new without parsing a file.
    /// Reads metadata only.
    /// </summary>
    /// <remarks>
    /// This is what lets the shell rescan when the window regains focus: that is the
    /// moment someone returns from <c>aws eks update-kubeconfig</c> in a terminal, or from
    /// dropping a file into a synced folder, and a <c>FileSystemWatcher</c> on
    /// <c>~/.kube</c> would instead fire on every write every tool makes there.
    /// </remarks>
    public static string ChainFingerprint(IEnumerable<string>? extraPaths = null)
    {
        var builder = new StringBuilder();
        foreach (var candidate in CandidatePathsShallow(extraPaths))
        {
            if (Directory.Exists(candidate))
            {
                builder.Append("D|").Append(candidate).Append('\n');
                try
                {
                    foreach (var file in Directory.EnumerateFiles(candidate).Order(StringComparer.Ordinal))
                    {
                        Stamp(builder, file);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    builder.Append("!\n");
                }
            }
            else
            {
                Stamp(builder, candidate);
            }
        }

        return builder.ToString();

        static void Stamp(StringBuilder builder, string file)
        {
            var info = new FileInfo(file);
            builder.Append(file).Append('|');
            if (info.Exists)
            {
                builder.Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);
            }

            builder.Append('\n');
        }
    }

    /// <summary>The search's own entries, without expanding folders or checking anything exists.</summary>
    private static IEnumerable<string> CandidatePathsShallow(IEnumerable<string>? extraPaths)
    {
        foreach (var path in extraPaths ?? [])
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return path;
            }
        }

        if (EnvironmentSearchOverride is { } seeded)
        {
            foreach (var path in seeded)
            {
                yield return path;
            }

            yield break;
        }

        var env = Environment.GetEnvironmentVariable("KUBECONFIG");
        if (!string.IsNullOrWhiteSpace(env))
        {
            foreach (var path in env.Split(
                Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return path;
            }
        }

        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kube", "config");
    }

    /// <summary>
    /// All contexts across the discovered kubeconfig files (first file wins on
    /// duplicate context names, matching kubectl merge semantics).
    /// </summary>
    /// <param name="kubeconfigPaths">An explicit file list, bypassing discovery entirely.</param>
    /// <param name="extraPaths">
    /// User-picked files to search <em>in addition to</em> the usual chain — ignored
    /// when <paramref name="kubeconfigPaths"/> is supplied, since that is already an
    /// explicit list. A picked file that no longer exists is dropped by
    /// <see cref="DiscoverPaths"/> before it gets here, so a stale pick costs a
    /// missing row in the empty state's search list, not an exception.
    /// </param>
    /// <param name="failures">
    /// When supplied, a file that exists but cannot be read or parsed is recorded here
    /// and skipped, and the rest of the chain still loads — one hand-edited file in a
    /// five-entry <c>$KUBECONFIG</c> used to cost every context in the other four.
    /// When null, the first such file throws, which is what a caller holding one
    /// explicit file wants.
    /// </param>
    public static async Task<IReadOnlyList<ClusterContext>> LoadContextsAsync(
        IEnumerable<string>? kubeconfigPaths = null,
        IEnumerable<string>? extraPaths = null,
        ICollection<KubeconfigReadFailure>? failures = null,
        CancellationToken cancellationToken = default) =>
        (await LoadChainAsync(kubeconfigPaths, extraPaths, failures, cancellationToken).ConfigureAwait(false)).Contexts;

    /// <summary>
    /// <see cref="LoadContextsAsync"/>, plus where the chain's <c>current-context</c> came
    /// from — which a caller that opens a tab on its own needs (B4-4, see
    /// <see cref="KubeconfigChain.AutomaticFirstContext"/>).
    /// </summary>
    public static async Task<KubeconfigChain> LoadChainAsync(
        IEnumerable<string>? kubeconfigPaths = null,
        IEnumerable<string>? extraPaths = null,
        ICollection<KubeconfigReadFailure>? failures = null,
        CancellationToken cancellationToken = default)
    {
        var result = new List<ClusterContext>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? currentContext = null;
        var currentFromFolderScan = false;

        // An explicit list is the caller naming every file, so nothing in it came from a scan.
        var folderOnly = kubeconfigPaths is null ? FolderOnlyPaths(extraPaths) : [];

        foreach (var path in kubeconfigPaths ?? DiscoverPaths(extraPaths))
        {
            var fromFolderScan = folderOnly.Contains(path);
            cancellationToken.ThrowIfCancellationRequested();
            K8SConfiguration config;
            try
            {
                config = (await KubeconfigReader.LoadAsync(path, cancellationToken).ConfigureAwait(false)).Configuration;
            }
            catch (Exception ex) when (failures is not null && ex is not OperationCanceledException)
            {
                failures.Add(new KubeconfigReadFailure(path, FirstLine(ex.Message)));
                continue;
            }

            // kubectl's merge rule: the first file in the chain that sets current-context wins.
            if (currentContext is null && !string.IsNullOrEmpty(config.CurrentContext))
            {
                currentContext = config.CurrentContext;
                currentFromFolderScan = fromFolderScan;
            }

            foreach (var ctx in config.Contexts ?? [])
            {
                if (ctx.Name is null || !seen.Add(ctx.Name))
                {
                    continue;
                }

                result.Add(new ClusterContext(
                    Name: ctx.Name,
                    ClusterName: ctx.ContextDetails?.Cluster ?? "",
                    Namespace: ctx.ContextDetails?.Namespace,
                    UserName: ctx.ContextDetails?.User,
                    KubeconfigPath: path)
                {
                    FromFolderScan = fromFolderScan,
                });
            }
        }

        IReadOnlyList<ClusterContext> contexts = currentContext is null
            ? result
            : [.. result.Select(c => c.Name == currentContext ? c with { IsCurrentContext = true } : c)];
        return new KubeconfigChain(contexts, currentContext, currentFromFolderScan);
    }

    /// <summary>
    /// The files the search reaches only by scanning a picked folder: found in one, and not
    /// also named on their own — as a picked file, a <c>$KUBECONFIG</c> entry or the default
    /// <c>~/.kube/config</c>. The last matters because the folder people pick is most often
    /// <c>~/.kube</c>, and its <c>config</c> is no less the user's own file for being in it.
    /// </summary>
    internal static HashSet<string> FolderOnlyPaths(IEnumerable<string>? extraPaths)
    {
        var picked = extraPaths?.ToArray() ?? [];
        var named = new HashSet<string>(
            CandidatePathsShallow(picked).Where(p => !Directory.Exists(p)).Select(Normalize),
            StringComparer.OrdinalIgnoreCase);

        return new HashSet<string>(
            CandidatePaths(picked)
                .Where(c => c.Source == InPickedFolderSource && !named.Contains(Normalize(c.Path)))
                .Select(c => c.Path),
            StringComparer.OrdinalIgnoreCase);

        static string Normalize(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return path;
            }
        }
    }

    /// <summary>
    /// Builds a client configuration for one context, re-resolving the file on
    /// every call (exec plugins, rotated certs and tokens are picked up fresh).
    /// </summary>
    /// <remarks>
    /// Blocks the calling thread: it waits on <see cref="BuildClientSetupAsync"/>'s work, and
    /// an exec credential plugin runs inside that as a blocking <c>WaitForExit</c>. Tests and
    /// tooling only — the app goes through <see cref="BuildClientConfigAsync"/>, and why
    /// is written there.
    /// </remarks>
    public static KubernetesClientConfiguration BuildClientConfig(ClusterContext context) =>
        BuildClientSetup(context).Configuration;

    /// <summary>The synchronous <see cref="BuildClientSetupAsync"/> — tests and tooling only, see <see cref="BuildClientConfig"/>.</summary>
    internal static ClientSetup BuildClientSetup(ClusterContext context) =>
        Task.Run(() => BuildClientSetupCoreAsync(context)).GetAwaiter().GetResult();

    /// <summary>
    /// <see cref="BuildClientConfig"/> for a caller that must not block — the UI thread,
    /// which is every caller in the app.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The synchronous overload hung the NativeAOT build at startup. Called on Avalonia's
    /// UI thread (an STA), its <c>GetResult()</c> parks in the runtime's reentrant COM
    /// wait; the kubeconfig read it waits for finished on a pool thread within a
    /// millisecond, and the UI thread still never woke — measured on win-x64 with the
    /// wait instrumented, 4 of 6 launches. The JIT runtime implements that wait
    /// differently and was never seen to hang, so a Debug run could not show it.
    /// Whatever the runtime's share of that, blocking the UI thread on I/O was the
    /// part that was ours.
    /// </para>
    /// <para>
    /// <see cref="Task.Run(Func{Task})"/> rather than awaiting the library's async
    /// overload directly: that overload only leaves the calling thread at its first
    /// await that does not complete synchronously, and everything after the file read —
    /// certificate parsing, and an exec credential plugin's blocking <c>WaitForExit</c>
    /// (bounded only by <c>ExecTimeout</c>, two minutes) — would otherwise still run on
    /// the UI thread whenever the read happened to complete inline.
    /// </para>
    /// <para>
    /// An exec plugin runs inside this call, and a failing one surfaces as an
    /// <see cref="ExecCredentialException"/> carrying what the plugin said, never the
    /// library's JSON parser error — see there.
    /// </para>
    /// </remarks>
    public static async Task<KubernetesClientConfiguration> BuildClientConfigAsync(
        ClusterContext context,
        CancellationToken cancellationToken = default) =>
        (await BuildClientSetupAsync(context, cancellationToken).ConfigureAwait(false)).Configuration;

    /// <summary>
    /// <see cref="BuildClientConfigAsync"/> plus the proxy the configuration was built with,
    /// which the caller needs a second time for the WebSocket transport (see
    /// <see cref="KubeconfigProxy"/>). Everything that happens to the kubeconfig entry
    /// between the file and the client is here, in one place:
    /// <list type="number">
    /// <item>the file is read once, by <see cref="KubeconfigReader"/> rather than the
    /// library's loader (relative certificate paths resolve against the file, as before);</item>
    /// <item>an exec plugin's command is resolved the way a login shell would
    /// (<see cref="ExecPluginPath"/>) — on the parsed object, in memory;</item>
    /// <item>the cluster's <c>proxy-url</c>, which the library's model drops, is taken from
    /// the same parse and validated before any plugin runs, so a typo in it does not cost an
    /// SSO prompt;</item>
    /// <item>a user entry's <c>tokenFile</c>, which the model has no field for either, is read
    /// into the in-memory model (<see cref="KubeconfigTokenFile"/>);</item>
    /// <item>the configuration is built, which is where the plugin runs; then a
    /// <c>certificate-authority(-data)</c> bundle is read whole (<see cref="ClusterCertificateAuthority"/>).</item>
    /// </list>
    /// Nothing read here is kept after the call beyond the configuration itself, and the
    /// whole thing re-runs on every connect and every credential refresh (hard rule 4).
    /// </summary>
    internal static Task<ClientSetup> BuildClientSetupAsync(
        ClusterContext context,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => BuildClientSetupCoreAsync(context), cancellationToken);

    private static Task<ClientSetup> BuildClientSetupCoreAsync(ClusterContext context) =>
        ExecCredentialCapture.RunAsync(async () =>
        {
            var file = new FileInfo(context.KubeconfigPath);
            var document = await KubeconfigReader.LoadAsync(file.FullName).ConfigureAwait(false);
            var config = document.Configuration;
            var (clusterName, user) = Entry(config, context.Name);

            if (user?.UserCredentials?.ExternalExecution is { } exec)
            {
                ExecPluginPath.Apply(exec, file.DirectoryName);
            }

            var proxy = clusterName is null ? null : KubeconfigProxy.Create(document.ProxyUrl(clusterName));

            // Checked before any plugin runs, like the proxy: an entry that cannot be honoured
            // exactly must not cost an SSO prompt first.
            var userEntry = ContextUser(config, context.Name);
            var impersonation = userEntry is null ? null : document.ImpersonationOf(userEntry);
            impersonation?.Validate(userEntry!);
            if (impersonation is { IsEmpty: true })
            {
                impersonation = null;
            }

            // tokenFile, which the library's model has no field for: read now, on every build,
            // into the in-memory model only (KubeconfigTokenFile).
            string? tokenFilePath = null;
            if (userEntry is not null
                && document.TokenFileOf(userEntry) is { } tokenFile
                && user?.UserCredentials is { } credentials)
            {
                tokenFilePath = KubeconfigTokenFile.Apply(credentials, tokenFile, file.DirectoryName);
            }

            // The plugin runs here, once, and its credential seeds the client's token
            // provider; the library would run it again on the first request (#283).
            var execProvider = user?.UserCredentials is { ExternalExecution: not null } execCredentials
                ? ExecCredentialProvider.RunInto(execCredentials)
                : null;

            // An entry with no credential in it (`user: {}`) connects anonymously, as kubectl
            // does; the library refuses it as a user with no credentials (#273).
            if (user is not null && HasNoCredential(user.UserCredentials)
                && config.Contexts?.FirstOrDefault(c => c.Name == context.Name)?.ContextDetails is { } details)
            {
                details.User = null;
            }

            var configuration = KubernetesClientConfiguration.BuildConfigFromConfigObject(config, context.Name);
            if (execProvider is not null)
            {
                configuration.TokenProvider = execProvider;
            }
            else if (tokenFilePath is not null && user?.UserCredentials?.Token is { Length: > 0 } fileToken)
            {
                // Re-read about once a minute while connected, as client-go does (#262).
                configuration.TokenProvider = new TokenFileProvider(tokenFilePath, fileToken);
            }

            // The library keeps only the first certificate of a CA bundle; kubectl trusts them all.
            if (!configuration.SkipTlsVerify
                && config.Clusters?.FirstOrDefault(c => c.Name == clusterName)?.ClusterEndpoint is { } endpoint
                && ClusterCertificateAuthority.Read(endpoint, file.DirectoryName) is { } authorities)
            {
                configuration.SslCaCerts = authorities;
            }
            if (proxy is not null)
            {
                configuration.FirstMessageHandlerSetup = handler =>
                {
                    handler.Proxy = proxy;
                    handler.UseProxy = true;
                };
            }

            return new ClientSetup(configuration, proxy, impersonation);
        });

    /// <summary>
    /// A path written in a kubeconfig, as client-go resolves it: as is when rooted, otherwise
    /// against the folder of the kubeconfig that wrote it.
    /// </summary>
    internal static string ResolveAgainstFile(string path, string? kubeconfigDirectory) =>
        Path.IsPathRooted(path) || string.IsNullOrEmpty(kubeconfigDirectory)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(kubeconfigDirectory, path));

    /// <summary>
    /// Whether a user entry carries nothing to sign in with: no token, certificate, username,
    /// auth-provider or exec plugin (<c>user: {}</c>, or a <c>user:</c> key missing). kubectl
    /// sends such requests with no credential; the library refuses the entry. Impersonation
    /// fields are not a credential — they say who to act as, and are sent either way.
    /// </summary>
    internal static bool HasNoCredential(UserCredentials? credentials) =>
        credentials is null
        || (string.IsNullOrEmpty(credentials.Token)
            && string.IsNullOrEmpty(credentials.ClientCertificateData)
            && string.IsNullOrEmpty(credentials.ClientCertificate)
            && string.IsNullOrEmpty(credentials.ClientKeyData)
            && string.IsNullOrEmpty(credentials.ClientKey)
            && string.IsNullOrEmpty(credentials.UserName)
            && string.IsNullOrEmpty(credentials.Password)
            && credentials.AuthProvider is null
            && credentials.ExternalExecution is null);

    /// <summary>The user entry name a context names, or null.</summary>
    internal static string? ContextUser(K8SConfiguration config, string contextName) =>
        config.Contexts?.FirstOrDefault(c => c.Name == contextName)?.ContextDetails?.User;

    /// <summary>The cluster name and user entry a context points at, when the file has them.</summary>
    internal static (string? ClusterName, User? User) Entry(K8SConfiguration config, string contextName)
    {
        var details = config.Contexts?.FirstOrDefault(c => c.Name == contextName)?.ContextDetails;
        var user = details?.User is { } userName ? config.Users?.FirstOrDefault(u => u.Name == userName) : null;
        return (details?.Cluster, user);
    }

    // The YAML parser's messages can run to several lines; the first names the problem.
    private static string FirstLine(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message[..end];
    }
}

/// <summary>
/// What a load of the kubeconfig chain found: its contexts, the chain's
/// <c>current-context</c> (kubectl's rule: the first file that sets one), and whether the
/// file that set it was reached only by scanning a picked folder.
/// </summary>
public sealed record KubeconfigChain(
    IReadOnlyList<ClusterContext> Contexts,
    string? CurrentContext,
    bool CurrentContextFromFolderScan)
{
    /// <summary>
    /// The context the app may open a tab on by itself — on a first launch with no
    /// workspace, after a rescan or after adding a folder — or null when it should open
    /// nothing and leave the choice to the switcher or the empty state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never a context from a folder scan, and never one a folder scan chose (B4-4).</b>
    /// A picked folder trusts every kubeconfig placed in it, the way a directory on PATH
    /// trusts every program in it; that is its point, and a click on such a context is the
    /// user's choice. But connecting runs the context's exec plugin, and picked paths are
    /// searched first, so a file dropped into a shared or synced folder used to set the
    /// chain's current-context and have its own command run on the next launch without a
    /// click. kubectl only ever reads files it was named.
    /// </para>
    /// <para>
    /// So: when the chain's current-context was set by such a file, nothing; when it names
    /// a context defined first in such a file (a folder file shadowing one of the user's
    /// own names), nothing; when it names one from a file the user named, that one; and
    /// with no current-context at all, the first context from a named file.
    /// </para>
    /// </remarks>
    public ClusterContext? AutomaticFirstContext()
    {
        if (CurrentContextFromFolderScan)
        {
            return null;
        }

        if (Contexts.FirstOrDefault(c => c.IsCurrentContext) is { } current)
        {
            return current.FromFolderScan ? null : current;
        }

        return Contexts.FirstOrDefault(c => !c.FromFolderScan);
    }
}

/// <summary>A kubeconfig file that exists and could not be read, and what the parser said.</summary>
public sealed record KubeconfigReadFailure(string Path, string Message);

/// <summary>One place the kubeconfig search looked, and whether anything was there.</summary>
/// <param name="IsFolder">A picked folder; its kubeconfigs follow it as their own candidates.</param>
public sealed record KubeconfigCandidate(string Path, bool Exists, string Source, bool IsFolder = false);

/// <summary>
/// A client configuration, the proxy it was built with (null when direct), and the user
/// entry's impersonation (null when it asks for none) — the two things the library's
/// configuration cannot carry.
/// </summary>
internal sealed record ClientSetup(
    KubernetesClientConfiguration Configuration, IWebProxy? Proxy, KubeconfigImpersonation? Impersonation = null);
