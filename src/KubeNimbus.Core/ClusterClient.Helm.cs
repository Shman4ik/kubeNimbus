using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>
/// Read-only Helm 3 release browsing, straight off the cluster — no Helm binary,
/// no Tiller, nothing shelled out.
/// </summary>
/// <remarks>
/// Helm 3 keeps each release revision in a Secret of type
/// <c>helm.sh/release.v1</c> named <c>sh.helm.release.v1.&lt;release&gt;.v&lt;revision&gt;</c>,
/// in the release's own namespace. Its <c>release</c> data value is
/// base64(gzip(JSON)) — and Kubernetes base64s Secret data on top of that, so
/// reading one means undoing two base64 layers and a gzip. That's the entire
/// integration: releases are ordinary cluster objects, so listing them reuses
/// the generic list path with a field selector.
/// </remarks>
public sealed partial class ClusterClient
{
    private const string HelmReleaseSecretType = "helm.sh/release.v1";

    /// <summary>
    /// Latest revision of every Helm release in a namespace (or all namespaces
    /// when null), newest update first.
    /// </summary>
    public async Task<IReadOnlyList<HelmRelease>> ListHelmReleasesAsync(
        string? @namespace = null, CancellationToken cancellationToken = default)
    {
        var revisions = await ListReleaseRevisionsAsync(@namespace, cancellationToken).ConfigureAwait(false);

        return [.. revisions
            .GroupBy(r => $"{r.Namespace}/{r.Name}", StringComparer.Ordinal)
            .Select(g => g.MaxBy(r => r.Revision)!)
            .OrderByDescending(r => r.Updated ?? DateTimeOffset.MinValue)];
    }

    /// <summary>
    /// Cheap "does this cluster use Helm at all?" probe — one page of one item,
    /// rather than decoding every release just to decide whether to show the
    /// Helm sidebar section at connect time.
    /// </summary>
    public async Task<bool> HasHelmReleasesAsync(CancellationToken cancellationToken = default)
    {
        var query = $"?limit=1&fieldSelector={Uri.EscapeDataString($"type={HelmReleaseSecretType}")}";
        using var doc = await GetJsonDocumentAsync(
            ResourceDescriptor.Secrets.CollectionPath(null) + query, cancellationToken).ConfigureAwait(false);

        return doc.RootElement.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Array
            && items.GetArrayLength() > 0;
    }

    /// <summary>Every stored revision of one release, newest first (Helm keeps 10 by default).</summary>
    public async Task<IReadOnlyList<HelmRelease>> GetHelmReleaseHistoryAsync(
        string @namespace, string name, CancellationToken cancellationToken = default)
    {
        var revisions = await ListReleaseRevisionsAsync(@namespace, cancellationToken).ConfigureAwait(false);

        return [.. revisions
            .Where(r => string.Equals(r.Name, name, StringComparison.Ordinal))
            .OrderByDescending(r => r.Revision)];
    }

    /// <summary>
    /// Full detail (rendered manifest, user-supplied values, notes) for one
    /// release revision — the latest when <paramref name="revision"/> is null.
    /// Null when the release isn't found.
    /// </summary>
    public async Task<HelmReleaseDetail?> GetHelmReleaseAsync(
        string @namespace, string name, int? revision = null, CancellationToken cancellationToken = default)
    {
        var secrets = await ListReleaseSecretsAsync(@namespace, cancellationToken).ConfigureAwait(false);

        JsonDocument? best = null;
        HelmRelease? bestRelease = null;
        try
        {
            foreach (var secret in secrets)
            {
                var document = TryReadReleaseRecord(secret);
                if (document is null)
                {
                    continue;
                }

                var release = ReadRelease(document.RootElement, secret.Namespace);
                if (!string.Equals(release.Name, name, StringComparison.Ordinal)
                    || (revision is { } wanted && release.Revision != wanted))
                {
                    document.Dispose();
                    continue;
                }

                if (bestRelease is null || release.Revision > bestRelease.Revision)
                {
                    best?.Dispose();
                    best = document;
                    bestRelease = release;
                }
                else
                {
                    document.Dispose();
                }
            }

            if (best is null || bestRelease is null)
            {
                return null;
            }

            var root = best.RootElement;
            return new HelmReleaseDetail(
                bestRelease,
                Manifest: ReadString(root, "manifest") ?? "",
                Notes: root.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                    ? ReadString(info, "notes") ?? ""
                    : "",
                ValuesYaml: root.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object
                    ? YamlJson.ToYamlString(config)
                    : "");
        }
        finally
        {
            best?.Dispose();
        }
    }

    private async Task<IReadOnlyList<HelmRelease>> ListReleaseRevisionsAsync(string? @namespace, CancellationToken ct)
    {
        var secrets = await ListReleaseSecretsAsync(@namespace, ct).ConfigureAwait(false);
        var result = new List<HelmRelease>();
        foreach (var secret in secrets)
        {
            using var document = TryReadReleaseRecord(secret);
            if (document is not null)
            {
                result.Add(ReadRelease(document.RootElement, secret.Namespace));
            }
        }

        return result;
    }

    private Task<IReadOnlyList<DynamicResource>> ListReleaseSecretsAsync(string? @namespace, CancellationToken ct) =>
        ListResourceOnceAsync(
            ResourceDescriptor.Secrets, @namespace, $"type={HelmReleaseSecretType}", ct);

    /// <summary>
    /// Unwraps a release Secret: Kubernetes base64 → Helm's own base64 → gzip →
    /// JSON. Returns null for anything that doesn't unwrap cleanly (a foreign
    /// object wearing the type, or a future storage format) — one bad release
    /// must not take out the whole list.
    /// </summary>
    internal static JsonDocument? TryReadReleaseRecord(DynamicResource secret)
    {
        if (!secret.Raw.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("release", out var releaseEl)
            || releaseEl.GetString() is not { } encoded)
        {
            return null;
        }

        try
        {
            // Kubernetes base64 of Helm's base64 text.
            var helmEncoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var payload = Convert.FromBase64String(helmEncoded);

            // Helm gzips by default; the magic bytes make the check cheap and
            // keep support for the rare uncompressed record.
            if (payload.Length > 2 && payload[0] == 0x1f && payload[1] == 0x8b)
            {
                using var compressed = new MemoryStream(payload);
                using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                using var plain = new MemoryStream();
                gzip.CopyTo(plain);
                payload = plain.ToArray();
            }

            return JsonDocument.Parse(payload);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or JsonException or DecoderFallbackException)
        {
            return null;
        }
    }

    internal static HelmRelease ReadRelease(JsonElement root, string? secretNamespace)
    {
        var info = root.TryGetProperty("info", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
        var metadata = root.TryGetProperty("chart", out var chart) && chart.ValueKind == JsonValueKind.Object
            && chart.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object
                ? m
                : default;

        return new HelmRelease(
            Name: ReadString(root, "name") ?? "",
            Namespace: ReadString(root, "namespace") ?? secretNamespace ?? "",
            Revision: root.TryGetProperty("version", out var v) && v.TryGetInt32(out var revision) ? revision : 0,
            Status: info.ValueKind == JsonValueKind.Object ? ReadString(info, "status") ?? "unknown" : "unknown",
            ChartName: metadata.ValueKind == JsonValueKind.Object ? ReadString(metadata, "name") ?? "" : "",
            ChartVersion: metadata.ValueKind == JsonValueKind.Object ? ReadString(metadata, "version") ?? "" : "",
            AppVersion: metadata.ValueKind == JsonValueKind.Object ? ReadString(metadata, "appVersion") ?? "" : "",
            Updated: info.ValueKind == JsonValueKind.Object ? ReadTimestamp(info, "last_deployed") : null,
            Description: info.ValueKind == JsonValueKind.Object ? ReadString(info, "description") ?? "" : "");
    }

    private static string? ReadString(JsonElement owner, string property) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(property, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static DateTimeOffset? ReadTimestamp(JsonElement owner, string property) =>
        ReadString(owner, property) is { } text && DateTimeOffset.TryParse(text, out var value) ? value : null;
}

/// <summary>One stored Helm release revision, as recorded in its release Secret.</summary>
public sealed record HelmRelease(
    string Name,
    string Namespace,
    int Revision,
    string Status,
    string ChartName,
    string ChartVersion,
    string AppVersion,
    DateTimeOffset? Updated,
    string Description)
{
    /// <summary>"nginx-1.2.3" — chart and chart version the way Helm's own output reads.</summary>
    public string Chart => string.IsNullOrEmpty(ChartVersion) ? ChartName : $"{ChartName}-{ChartVersion}";
}

/// <summary>A release revision plus its heavy payload: rendered manifest, user values and notes.</summary>
public sealed record HelmReleaseDetail(HelmRelease Release, string Manifest, string Notes, string ValuesYaml);
