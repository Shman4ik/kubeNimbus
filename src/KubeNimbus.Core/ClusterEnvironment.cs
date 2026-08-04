namespace KubeNimbus.Core;

/// <summary>
/// Which environment a context is believed to point at. Drives the colour a
/// cluster carries throughout the shell — the one mitigation the industry has
/// converged on for "I ran that against the wrong cluster".
/// </summary>
public enum ClusterEnvironment
{
    /// <summary>Nothing in the name said either way. Neutral chrome, no claim made.</summary>
    Unknown,
    Development,
    Staging,
    Production,
}

/// <summary>How an environment was decided — a name guess, or the user saying so.</summary>
public enum ClusterEnvironmentSource
{
    /// <summary>Guessed from the context/cluster name. Can be wrong; the UI says so.</summary>
    Inferred,

    /// <summary>Set explicitly by the user and persisted. Always wins over the guess.</summary>
    UserAssigned,
}

/// <summary>
/// Name-based environment classification for kubeconfig contexts.
///
/// This is a heuristic and it is deliberately biased: <b>over-flagging production
/// is cheap (a red band on staging, one click to correct), under-flagging it is
/// the incident this whole feature exists to prevent.</b> So "prod" anywhere in
/// the name counts, and the only things rescued from that are the explicit
/// non-production prefixes (<c>preprod</c>, <c>non-prod</c>, …) which are checked
/// first. Every rule here is pinned by <c>ClusterEnvironmentTests</c>; a change
/// that makes a real production cluster read as anything else is a regression,
/// not a tuning choice.
///
/// A guess is never the last word — <c>WorkspaceSettings.EnvironmentOverrides</c>
/// carries a per-context user assignment that wins outright.
/// </summary>
public static class ClusterEnvironments
{
    // All markers are stored SQUASHED (no separators), because matching runs over
    // single tokens and adjacent token *pairs* — "non-prod-eu" tokenizes to
    // ["non","prod","eu"], so the rescue only fires if "non"+"prod" is tested as
    // one candidate. See ContainsAny.

    // Checked BEFORE the production markers, because every one of these contains
    // a production marker and means the exact opposite.
    private static readonly string[] NotProductionMarkers =
        ["preprod", "nonprod", "notprod", "prestage", "prodlike"];

    private static readonly string[] ProductionMarkers = ["prod", "prd", "production", "live"];

    private static readonly string[] StagingMarkers =
        ["staging", "stage", "stg", "stag", "uat", "qa", "canary", "preview", "integration"];

    private static readonly string[] DevelopmentMarkers =
    [
        "dev", "develop", "development", "local", "localhost", "sandbox", "sbx", "playground", "scratch",
        // "demo" covers the app's own built-in demo cluster, which must never read as
        // production — and a cluster a user has named "demo" isn't one either.
        "demo",
        // Local-cluster distributions. Nobody runs production on docker-desktop.
        "kind", "k3s", "k3d", "minikube", "microk8s", "dockerdesktop", "rancherdesktop", "colima", "orbstack",
    ];

    /// <summary>
    /// Classifies a context by name. <paramref name="clusterName"/> is considered too
    /// because managed-cluster contexts routinely carry the useful half there — an EKS
    /// ARN context named for the account with the cluster name holding "prod", say.
    /// </summary>
    public static ClusterEnvironment Classify(string? contextName, string? clusterName = null)
    {
        // Tokenized once — Classify runs per context on every kubeconfig load and
        // on every switcher keystroke's grouping pass.
        var candidates = Candidates($"{contextName} {clusterName}".ToLowerInvariant());

        // Order is the whole design: the non-production rescues run first so
        // "preprod" can't be read as "prod", then production (biased to fire),
        // then staging, then development.
        var rescued = ContainsAny(candidates, NotProductionMarkers);

        if (!rescued && ContainsAny(candidates, ProductionMarkers))
        {
            return ClusterEnvironment.Production;
        }

        if (rescued || ContainsAny(candidates, StagingMarkers))
        {
            return ClusterEnvironment.Staging;
        }

        if (ContainsAny(candidates, DevelopmentMarkers))
        {
            return ClusterEnvironment.Development;
        }

        return ClusterEnvironment.Unknown;
    }

    /// <summary>
    /// Everything a marker is allowed to match: each token, plus each pair of
    /// adjacent tokens joined. The pairs are what make the separator-spelled
    /// compounds work — "non-prod", "pre_prod" and "docker-desktop" all arrive as
    /// two tokens, and only the joined form equals the marker.
    /// </summary>
    private static List<string> Candidates(string value)
    {
        var tokens = Tokenize(value);
        var candidates = new List<string>(tokens.Count * 2);
        for (var i = 0; i < tokens.Count; i++)
        {
            candidates.Add(tokens[i]);
            if (i + 1 < tokens.Count)
            {
                candidates.Add(tokens[i] + tokens[i + 1]);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Marker match on token boundaries. A plain <c>Contains</c> over the whole
    /// name would read "product-catalog" as production and "internal-tools" as an
    /// integration environment — matching only whole <c>[a-z0-9]</c> runs (and
    /// adjacent pairs) keeps a marker from hiding inside an unrelated word, while
    /// the digit-suffix rule below still catches the real-world "prod1" /
    /// "eks-prod-01" shapes.
    /// </summary>
    private static bool ContainsAny(List<string> candidates, string[] markers)
    {
        foreach (var candidate in candidates)
        {
            foreach (var marker in markers)
            {
                if (candidate == marker)
                {
                    return true;
                }

                // "prod1", "prod01", "stg2" — a trailing ordinal is part of the
                // cluster's identity, not of the environment word.
                if (candidate.Length > marker.Length
                    && candidate.StartsWith(marker, StringComparison.Ordinal)
                    && IsAllDigits(candidate.AsSpan(marker.Length)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsAllDigits(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Splits on everything that isn't a letter or digit. Context names are
    /// separator soup in practice — <c>arn:aws:eks:eu-west-1:123:cluster/foo-prod</c>,
    /// <c>gke_project_europe-west4_prod-01</c> — and all of those separators are
    /// equivalent for our purposes.
    /// </summary>
    private static List<string> Tokenize(string value)
    {
        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i <= value.Length; i++)
        {
            var isWord = i < value.Length && char.IsAsciiLetterOrDigit(value[i]);
            if (isWord && start < 0)
            {
                start = i;
            }
            else if (!isWord && start >= 0)
            {
                tokens.Add(value[start..i]);
                start = -1;
            }
        }

        return tokens;
    }

    /// <summary>Short label for the pill/menu. Unknown has no label — it makes no claim.</summary>
    public static string? Label(this ClusterEnvironment environment) => environment switch
    {
        ClusterEnvironment.Production => "PROD",
        ClusterEnvironment.Staging => "STAGING",
        ClusterEnvironment.Development => "DEV",
        _ => null,
    };
}
