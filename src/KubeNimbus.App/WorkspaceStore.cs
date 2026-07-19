using System.Text.Json;
using System.Text.Json.Serialization;

namespace KubeNimbus.App;

/// <summary>One remembered cluster tab. Only a context name + kubeconfig path — re-resolved
/// through the kubeconfig chain on restore, never a credential (CLAUDE.md rule #4).</summary>
public sealed record TabSnapshot(string ContextName, string KubeconfigPath);

public sealed record WorkspaceSettings(string? Theme, List<TabSnapshot> Tabs);

[JsonSerializable(typeof(WorkspaceSettings))]
internal sealed partial class WorkspaceJsonContext : JsonSerializerContext;

/// <summary>
/// Persists the theme choice and the open cluster tabs so a restart restores
/// the workspace. Source-generated JSON (<see cref="WorkspaceJsonContext"/>)
/// keeps this NativeAOT/trim-safe — no reflection-based serialization.
/// </summary>
public static class WorkspaceStore
{
    private static readonly WorkspaceSettings Empty = new(null, []);

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "kubeNimbus", "workspace.json");

    public static WorkspaceSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return Empty;
            }

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize(json, WorkspaceJsonContext.Default.WorkspaceSettings) ?? Empty;
        }
        catch (Exception)
        {
            return Empty;
        }
    }

    public static void Save(WorkspaceSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, WorkspaceJsonContext.Default.WorkspaceSettings);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception)
        {
            // Best-effort persistence — a failed save shouldn't crash the app.
        }
    }
}
