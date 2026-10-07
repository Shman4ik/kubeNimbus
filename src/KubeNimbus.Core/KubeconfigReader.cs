using k8s.Exceptions;
using k8s.KubeConfigModels;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace KubeNimbus.Core;

/// <summary>
/// Reads a kubeconfig file into the client library's own <see cref="K8SConfiguration"/>
/// model — without the library's YAML reader. Every kubeconfig the app loads comes through
/// here, and nothing in this repository may call the library's loader instead
/// (<c>BannedSymbols.txt</c> makes that a build error).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <c>KubernetesClientConfiguration.LoadKubeConfigAsync</c>.</b> In
/// <c>KubernetesClient.Aot</c> that loader deserializes through a YamlDotNet
/// <c>StaticContext</c> which the package ships <em>precompiled</em>, generated against one
/// exact YamlDotNet version (16.3.0 for 19.0.2). YamlDotNet 18 added a member to
/// <c>ITypeInspector</c>, so with any newer YamlDotNet in the graph the precompiled
/// inspector no longer implements its interface and the first kubeconfig load throws
/// <c>TypeLoadException: Method 'HasParseMethod' … does not have an implementation</c> —
/// every connect fails, while the build, the NativeAOT publish and the plain launch check
/// all stay green. That coupled our YamlDotNet to whatever the client was last built with:
/// the bump was tried and reverted twice (#15, #77), and Dependabot had to be told to
/// ignore YamlDotNet altogether. Upstream has since moved to YamlDotNet 18 (tag v20.0.84),
/// but that release never reached NuGet, and the next one will pin some other version.
/// Reading the file here takes the client's YAML layer off the path entirely, so the two
/// packages can be upgraded independently.
/// </para>
/// <para>
/// <b>What it reads is exactly what the library's model holds</b> — the same keys, the same
/// types, unknown keys ignored — plus each cluster's <c>proxy-url</c>, which the model
/// drops (see <see cref="KubeconfigProxy"/>), and each user's impersonation
/// (see <see cref="KubeconfigImpersonation"/>). It is built on YamlDotNet's event parser, the
/// lowest and most stable layer of that package, and never on its object deserializer,
/// which is neither trim-safe nor needed for a schema this small.
/// </para>
/// <para>
/// Three places it is deliberately kinder than the loader it replaces, each matching
/// kubectl: an empty file is an empty configuration rather than a
/// <c>NullReferenceException</c>; a key given twice keeps its last value rather than failing
/// the file (YamlDotNet's representation model rejects duplicates, which is why this does
/// not use it); and an <c>as-user-extra</c> value written as a list, which is its real
/// shape in client-go but not in the library's model, is left out of the model rather than
/// failing the file. The library never sends impersonation at all; kubeNimbus does, from
/// <see cref="KubeconfigDocument.Impersonations"/>, which reads every impersonation field in
/// client-go's own shape (<c>as-uid</c> and the lists included).
/// </para>
/// </remarks>
internal static class KubeconfigReader
{
    /// <summary>
    /// Reads and parses <paramref name="path"/>, setting <see cref="K8SConfiguration.FileName"/>
    /// so relative certificate paths resolve against the file's own directory, as the
    /// library's loader did.
    /// </summary>
    internal static async Task<KubeconfigDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            // The library's own exception and wording, so a caller that reported it keeps
            // reporting the same sentence.
            throw new KubeConfigException($"kubeconfig file not found at {file.FullName}");
        }

        var text = await File.ReadAllTextAsync(file.FullName, cancellationToken).ConfigureAwait(false);
        var document = Parse(text);
        document.Configuration.FileName = file.FullName;
        return document;
    }

    /// <summary>
    /// Parses kubeconfig text. Throws <see cref="YamlException"/> for text that is not YAML
    /// or does not have a kubeconfig's shape; the message starts with the line and column.
    /// </summary>
    internal static KubeconfigDocument Parse(string text)
    {
        try
        {
            return Read(LoadFirstDocument(text));
        }
        catch (YamlException e)
        {
            // YamlDotNet 18 no longer puts the position in the message, and the message is
            // what the status line shows for a file that did not load; without a line
            // number, "did not find expected ','" leaves someone searching the whole file.
            throw new YamlException(e.Start, e.End, $"Line {e.Start.Line}, column {e.Start.Column}: {e.Message}", e);
        }
    }

    private static KubeconfigDocument Read(Node? root)
    {
        var config = new K8SConfiguration();
        var proxies = new Dictionary<string, string>(StringComparer.Ordinal);

        // Case-insensitive, first entry wins: that is how the library finds the user entry a
        // context names (FirstOrDefault, OrdinalIgnoreCase), and the impersonation sent must
        // be the one of the entry whose credentials are used.
        var impersonations = new Dictionary<string, KubeconfigImpersonation>(StringComparer.OrdinalIgnoreCase);
        if (root is null)
        {
            return new KubeconfigDocument(config, proxies, impersonations);
        }

        var map = Expect<MapNode>(root, "the top level");
        config.ApiVersion = Text(map, "apiVersion");
        config.Kind = Text(map, "kind");
        config.CurrentContext = Text(map, "current-context");
        config.Clusters = Items(map, "clusters", ReadCluster);
        config.Users = Items(map, "users", ReadUser);
        config.Contexts = Items(map, "contexts", ReadContext);

        if (map.Get("clusters") is SeqNode clusters)
        {
            foreach (var entry in clusters.Items.OfType<MapNode>())
            {
                if (Text(entry, "name") is { } name
                    && entry.Get("cluster") is MapNode cluster
                    && Text(cluster, "proxy-url") is { Length: > 0 } url)
                {
                    proxies[name] = url.Trim();
                }
            }
        }

        if (map.Get("users") is SeqNode users)
        {
            foreach (var entry in users.Items.OfType<MapNode>())
            {
                if (Text(entry, "name") is { } name
                    && entry.Get("user") is MapNode credentials
                    && !impersonations.ContainsKey(name))
                {
                    impersonations[name] = ReadImpersonation(credentials);
                }
            }
        }

        return new KubeconfigDocument(config, proxies, impersonations);
    }

    /// <summary>
    /// <c>as</c>, <c>as-uid</c>, <c>as-groups</c> and <c>as-user-extra</c> in client-go's own
    /// shapes — an extra is a list of values per key, and a single value is read as a list of
    /// one. The library's model has no <c>as-uid</c> and cannot hold a list per key, which is
    /// why this is carried beside it, as <c>proxy-url</c> is.
    /// </summary>
    private static KubeconfigImpersonation ReadImpersonation(MapNode credentials)
    {
        var groups = credentials.Get("as-groups") is { } groupsNode
            ? [.. Expect<SeqNode>(groupsNode, "as-groups").Items.Select(g => ScalarText(g, "an as-groups entry")).OfType<string>()]
            : new List<string>();

        var extra = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (credentials.Get("as-user-extra") is { } extraNode)
        {
            foreach (var (key, value) in Expect<MapNode>(extraNode, "as-user-extra").Children)
            {
                List<string> values = value switch
                {
                    ScalarNode { Text: { } text } => [text],
                    SeqNode seq => [.. seq.Items.Select(v => ScalarText(v, $"an as-user-extra {key} value")).OfType<string>()],
                    _ => [],
                };

                if (values.Count > 0)
                {
                    extra[key] = values;
                }
            }
        }

        return new KubeconfigImpersonation(Text(credentials, "as"), Text(credentials, "as-uid"), groups, extra);
    }

    private static Cluster ReadCluster(MapNode map)
    {
        var endpoint = map.Get("cluster") is { } node ? Expect<MapNode>(node, "a cluster entry's cluster") : null;
        return new Cluster
        {
            Name = Text(map, "name"),
            ClusterEndpoint = endpoint is null ? null : new ClusterEndpoint
            {
                CertificateAuthority = Text(endpoint, "certificate-authority"),
                CertificateAuthorityData = Text(endpoint, "certificate-authority-data"),
                Server = Text(endpoint, "server"),
                TlsServerName = Text(endpoint, "tls-server-name"),
                SkipTlsVerify = Bool(endpoint, "insecure-skip-tls-verify"),
            },
        };
    }

    private static User ReadUser(MapNode map)
    {
        var credentials = map.Get("user") is { } node ? Expect<MapNode>(node, "a user entry's user") : null;
        return new User
        {
            Name = Text(map, "name"),
            UserCredentials = credentials is null ? null : ReadCredentials(credentials),
        };
    }

    private static UserCredentials ReadCredentials(MapNode map)
    {
        var credentials = new UserCredentials
        {
            ClientCertificateData = Text(map, "client-certificate-data"),
            ClientCertificate = Text(map, "client-certificate"),
            ClientKeyData = Text(map, "client-key-data"),
            ClientKey = Text(map, "client-key"),
            Token = Text(map, "token"),
            Impersonate = Text(map, "as"),
            UserName = Text(map, "username"),
            Password = Text(map, "password"),
        };

        if (map.Get("as-groups") is SeqNode groups)
        {
            credentials.ImpersonateGroups = [.. groups.Items.Select(g => ScalarText(g, "an as-groups entry"))];
        }

        if (map.Get("as-user-extra") is MapNode extra)
        {
            foreach (var (key, value) in extra.Children)
            {
                if (value is ScalarNode scalar && scalar.Text is { } text)
                {
                    credentials.ImpersonateUserExtra[key] = text;
                }
            }
        }

        if (map.Get("auth-provider") is { } providerNode)
        {
            var provider = Expect<MapNode>(providerNode, "auth-provider");
            credentials.AuthProvider = new AuthProvider
            {
                Name = Text(provider, "name"),
                Config = provider.Get("config") is { } config ? StringMap(Expect<MapNode>(config, "auth-provider config")) : null,
            };
        }

        if (map.Get("exec") is { } execNode)
        {
            var exec = Expect<MapNode>(execNode, "exec");
            credentials.ExternalExecution = new ExternalExecution
            {
                ApiVersion = Text(exec, "apiVersion"),
                Command = Text(exec, "command"),
                InstallHint = Text(exec, "installHint"),
                ProvideClusterInfo = Bool(exec, "provideClusterInfo"),
                Arguments = exec.Get("args") is { } args
                    ? [.. Expect<SeqNode>(args, "exec args").Items.Select(a => ScalarText(a, "an exec argument"))]
                    : null,
                EnvironmentVariables = exec.Get("env") is { } env
                    ? [.. Expect<SeqNode>(env, "exec env").Items.Select(e => StringMap(Expect<MapNode>(e, "an exec env entry")))]
                    : null,
            };
        }

        return credentials;
    }

    private static Context ReadContext(MapNode map)
    {
        var details = map.Get("context") is { } node ? Expect<MapNode>(node, "a context entry's context") : null;
        return new Context
        {
            Name = Text(map, "name"),
            ContextDetails = details is null ? null : new ContextDetails
            {
                Cluster = Text(details, "cluster"),
                User = Text(details, "user"),
                Namespace = Text(details, "namespace"),
            },
        };
    }

    // ------------------------------------------------------------------ typed reads

    // An empty entry (a bare "-") is skipped: the old loader kept it as a null that the
    // first lookup by name then dereferenced.
    private static List<T> Items<T>(MapNode map, string key, Func<MapNode, T> read) =>
        map.Get(key) is { } node
            ? [.. Expect<SeqNode>(node, key).Items
                .Where(item => item is not ScalarNode { Text: null })
                .Select(item => read(Expect<MapNode>(item, $"an entry of {key}")))]
            : [];

    private static string? Text(MapNode map, string key) =>
        map.Get(key) is { } node ? ScalarText(node, key) : null;

    private static string? ScalarText(Node node, string what) => Expect<ScalarNode>(node, what).Text;

    private static Dictionary<string, string> StringMap(MapNode map)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in map.Children)
        {
            result[key] = ScalarText(value, key)!;
        }

        return result;
    }

    /// <summary>YAML 1.1's boolean spellings, which is what YamlDotNet accepted here before.</summary>
    private static bool Bool(MapNode map, string key)
    {
        if (map.Get(key) is not { } node || ScalarText(node, key) is not { } text)
        {
            return false;
        }

        return text.ToLowerInvariant() switch
        {
            "true" or "y" or "yes" or "on" => true,
            "false" or "n" or "no" or "off" => false,
            _ => throw new YamlException(node.Start, node.Start, $"{key} is \"{text}\", which is not true or false."),
        };
    }

    private static T Expect<T>(Node node, string what) where T : Node =>
        node as T ?? throw new YamlException(node.Start, node.Start, $"{what} should be {Describe<T>()}, not {Describe(node)}.");

    private static string Describe<T>() where T : Node =>
        typeof(T) == typeof(MapNode) ? "a mapping" : typeof(T) == typeof(SeqNode) ? "a list" : "a single value";

    private static string Describe(Node node) => node switch
    {
        MapNode => "a mapping",
        SeqNode => "a list",
        _ => "a single value",
    };

    // ------------------------------------------------------------------ the tree

    /// <summary>
    /// The first document of <paramref name="text"/> as a tree, or null for an empty stream
    /// or an empty document. Aliases resolve to the node their anchor named; a mapping key
    /// given twice keeps its last value; a key that is not a single value is skipped.
    /// </summary>
    private static Node? LoadFirstDocument(string text)
    {
        var parser = new Parser(new StringReader(text));
        parser.Consume<StreamStart>();
        if (!parser.Accept<DocumentStart>(out _))
        {
            return null;
        }

        parser.Consume<DocumentStart>();
        var root = ReadNode(parser, new Dictionary<AnchorName, Node>());
        return root is ScalarNode { Text: null } ? null : root;
    }

    private static Node ReadNode(IParser parser, Dictionary<AnchorName, Node> anchors)
    {
        if (parser.TryConsume<AnchorAlias>(out var alias))
        {
            return anchors.TryGetValue(alias.Value, out var target)
                ? target
                : throw new YamlException(alias.Start, alias.End, $"*{alias.Value} refers to no anchor.");
        }

        if (parser.TryConsume<Scalar>(out var scalar))
        {
            // A plain null spelling is YAML null; a quoted one is the text itself.
            var isNull = scalar.Style == ScalarStyle.Plain && scalar.Value is "" or "~" or "null" or "Null" or "NULL";
            return Remember(anchors, scalar.Anchor, new ScalarNode(isNull ? null : scalar.Value, scalar.Start));
        }

        if (parser.TryConsume<SequenceStart>(out var sequenceStart))
        {
            var sequence = Remember(anchors, sequenceStart.Anchor, new SeqNode([], sequenceStart.Start));
            while (!parser.TryConsume<SequenceEnd>(out _))
            {
                sequence.Items.Add(ReadNode(parser, anchors));
            }

            return sequence;
        }

        var mappingStart = parser.Consume<MappingStart>();
        var mapping = Remember(anchors, mappingStart.Anchor, new MapNode(new Dictionary<string, Node>(StringComparer.Ordinal), mappingStart.Start));
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = ReadNode(parser, anchors);
            var value = ReadNode(parser, anchors);
            if (key is ScalarNode { Text: { } name })
            {
                mapping.Children[name] = value;
            }
        }

        return mapping;
    }

    private static T Remember<T>(Dictionary<AnchorName, Node> anchors, AnchorName anchor, T node) where T : Node
    {
        if (!anchor.IsEmpty)
        {
            anchors[anchor] = node;
        }

        return node;
    }

    private abstract record Node(Mark Start);

    private sealed record ScalarNode(string? Text, Mark Start) : Node(Start);

    private sealed record SeqNode(List<Node> Items, Mark Start) : Node(Start);

    private sealed record MapNode(Dictionary<string, Node> Children, Mark Start) : Node(Start)
    {
        /// <summary>
        /// The value of <paramref name="key"/>, or null when it is absent or YAML null —
        /// kubectl itself writes <c>clusters: null</c> into a kubeconfig with no clusters.
        /// </summary>
        public Node? Get(string key) =>
            Children.TryGetValue(key, out var node) && node is not ScalarNode { Text: null } ? node : null;
    }
}

/// <summary>
/// A parsed kubeconfig: the client library's model, and what that model drops or cannot
/// hold — each cluster's <c>proxy-url</c>, by cluster name, and each user entry's
/// impersonation in client-go's shape, by user name.
/// </summary>
internal sealed record KubeconfigDocument(
    K8SConfiguration Configuration,
    IReadOnlyDictionary<string, string> ProxyUrls,
    IReadOnlyDictionary<string, KubeconfigImpersonation> Impersonations)
{
    /// <summary>The <c>proxy-url</c> of cluster <paramref name="clusterName"/>, or null.</summary>
    public string? ProxyUrl(string clusterName) => ProxyUrls.TryGetValue(clusterName, out var url) ? url : null;

    /// <summary>The impersonation of user entry <paramref name="userName"/> (possibly empty), or null when there is no such entry.</summary>
    public KubeconfigImpersonation? ImpersonationOf(string userName) =>
        Impersonations.TryGetValue(userName, out var impersonation) ? impersonation : null;
}
