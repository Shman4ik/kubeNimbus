using System.Text.Json;
using System.Text.Json.Serialization;

namespace KubeNimbus.Core.Settings;

/// <summary>
/// Persists <see cref="AppSettings"/> to a single JSON file under the app data root
/// (<c>%APPDATA%/kubeNimbus/settings.json</c> and the platform equivalents).
///
/// <para>
/// Source-generated JSON (<see cref="AppSettingsJsonContext"/>) keeps this
/// NativeAOT/trim-safe — no reflection-based serialization anywhere in this repo.
/// </para>
/// </summary>
public sealed class AppSettingsStore
{
    private readonly string _filePath;

    /// <summary>
    /// Overrides the directory settings are read from and written to. Set by the
    /// screenshot harness for the same reason <c>WorkspaceStore.DirectoryOverride</c>
    /// exists: scenarios construct real view-models, which read settings on
    /// construction and write them as soon as a fixture flips a preference — without
    /// the redirect, rendering fixtures would silently rewrite the developer's own
    /// theme and kubeconfig list.
    /// </summary>
    public static string? DirectoryOverride { get; set; }

    /// <summary>The default file location, honouring <see cref="DirectoryOverride"/>.</summary>
    public static string DefaultPath => Path.Combine(
        DirectoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "kubeNimbus"),
        "settings.json");

    public AppSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? DefaultPath;
    }

    /// <summary>
    /// Reads the saved settings, or returns defaults when there is no file yet. A
    /// missing, unreadable or corrupt file must never block startup — the app is a
    /// cluster client, and refusing to launch because a preferences file has a stray
    /// comma in it would be absurd — so any failure here falls back to defaults rather
    /// than throwing. The result is always <see cref="AppSettings.Normalized"/>, so
    /// callers never see an out-of-range poll interval or a null list.
    /// </summary>
    public AppSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
            return (settings ?? new AppSettings()).Normalized();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Writes the settings, normalizing first so a value the UI never validated cannot
    /// be persisted out of range. Best-effort, like the workspace: a failed save must
    /// not crash the app — the user loses a preference, not their session.
    /// </summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings.Normalized(), AppSettingsJsonContext.Default.AppSettings);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Whether a settings file exists yet. Used by the one-time migration off the workspace.</summary>
    public bool Exists() => File.Exists(_filePath);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
