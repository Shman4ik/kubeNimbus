using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KubeNimbus.Core;

/// <summary>Best-effort discovery metadata only. Credentials and object contents never enter this store.</summary>
internal sealed class DiscoveryCache(string? directory = null)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);
    private readonly string _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kubeNimbus", "discovery");
    private string PathFor(string identity) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json");

    internal async Task<IReadOnlyList<ResourceDescriptor>?> ReadAsync(string identity, string version, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(PathFor(identity));
            var entry = await JsonSerializer.DeserializeAsync(stream, DiscoveryCacheJson.Default.DiscoveryCacheEntry, ct);
            if (entry is null || entry.Format != 1 || entry.ServerVersion != version
                || entry.WrittenAt > DateTimeOffset.UtcNow || DateTimeOffset.UtcNow - entry.WrittenAt > Lifetime
                || entry.Resources is null || entry.Resources.Any(r => r is null || string.IsNullOrEmpty(r.Version)
                    || string.IsNullOrEmpty(r.Kind) || string.IsNullOrEmpty(r.Plural) || r.Group is null
                    || r.Verbs is null || r.Subresources is null || r.ShortNames is null || r.Categories is null)) return null;
            return entry.Resources;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal void Invalidate(string identity)
    {
        try { File.Delete(PathFor(identity)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal async Task WriteAsync(string identity, string version, IReadOnlyList<ResourceDescriptor> resources, CancellationToken ct)
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(_directory);
            var path = PathFor(identity);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, new DiscoveryCacheEntry(1, version, DateTimeOffset.UtcNow, resources.ToArray()),
                    DiscoveryCacheJson.Default.DiscoveryCacheEntry, ct);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

internal sealed record DiscoveryCacheEntry(int Format, string ServerVersion, DateTimeOffset WrittenAt, ResourceDescriptor[] Resources);
[JsonSerializable(typeof(DiscoveryCacheEntry))]
internal sealed partial class DiscoveryCacheJson : JsonSerializerContext;
