using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// View/edit tab for any resource kind → server-side apply, with delete
/// (two-step confirm, no modal dialog needed) and conflict surfacing. Content
/// is a snapshot taken at open time — live watch updates never overwrite text
/// the user might be mid-edit on; <see cref="ReloadCommand"/> re-fetches explicitly.
/// For a Secret, <c>data</c> stays base64 in the editable text (matching
/// kubectl) — <see cref="IsSecretValuesRevealed"/> only shows a separate,
/// read-only decoded preview panel, masked by default, computed from whatever
/// the editor currently holds (so it reflects in-progress edits too).
/// </summary>
public sealed partial class YamlEditorTabViewModel : InspectorTabViewModelBase
{
    private const string FieldManager = "kubenimbus";

    private readonly ClusterClient _client;
    private readonly ResourceDescriptor _descriptor;
    private readonly string? _namespace;
    private readonly string _name;

    public override string Key { get; }

    [ObservableProperty]
    private string _yamlText;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isConfirmingDelete;

    [ObservableProperty]
    private string? _conflictDetails;

    [ObservableProperty]
    private bool _isDeleted;

    public bool IsSecret => _descriptor is { Kind: "Secret", Group: "" };

    [ObservableProperty]
    private bool _isSecretValuesRevealed;

    public ObservableCollection<SecretValuePreviewViewModel> DecodedSecretValues { get; } = [];

    /// <summary>Cluster this object came from in an aggregated fleet list; empty otherwise.</summary>
    public string ClusterName { get; }

    /// <summary>
    /// Tab identity, qualified by cluster when the object came from an aggregated fleet
    /// list — the same namespace/name exists on every cluster in a fleet, and this key
    /// is what decides whether an open tab gets reused.
    /// </summary>
    public static string KeyFor(string clusterName, ResourceDescriptor descriptor, string? @namespace, string name) =>
        clusterName.Length == 0
            ? $"yaml:{descriptor.ApiVersion}/{descriptor.Kind}:{@namespace}/{name}"
            : $"yaml@{clusterName}:{descriptor.ApiVersion}/{descriptor.Kind}:{@namespace}/{name}";

    public YamlEditorTabViewModel(
        ClusterClient client, ResourceDescriptor descriptor, string? @namespace, string name, string initialYaml,
        string clusterName = "")
        : base(clusterName.Length == 0 ? $"{descriptor.Kind}/{name}" : $"{descriptor.Kind}/{name} · {clusterName}")
    {
        _client = client;
        _descriptor = descriptor;
        _namespace = @namespace;
        _name = name;
        _yamlText = initialYaml;
        ClusterName = clusterName;
        Key = KeyFor(clusterName, descriptor, @namespace, name);
    }

    partial void OnYamlTextChanged(string value)
    {
        IsDirty = true;
        if (IsSecretValuesRevealed)
        {
            RefreshDecodedSecretValues();
        }
    }

    [RelayCommand]
    private void ToggleSecretValuesRevealed()
    {
        IsSecretValuesRevealed = !IsSecretValuesRevealed;
        if (IsSecretValuesRevealed)
        {
            RefreshDecodedSecretValues();
        }
        else
        {
            DecodedSecretValues.Clear();
        }
    }

    private void RefreshDecodedSecretValues()
    {
        DecodedSecretValues.Clear();
        try
        {
            if (YamlJson.ParseYamlToJson(YamlText) is not JsonObject root || root["data"] is not JsonObject data)
            {
                return;
            }

            foreach (var (key, valueNode) in data)
            {
                var base64 = valueNode?.GetValue<string>() ?? "";
                DecodedSecretValues.Add(new SecretValuePreviewViewModel(key, DecodeBase64(base64)));
            }
        }
        catch (Exception)
        {
            // Editor text may be mid-edit and not valid YAML/JSON right now — the
            // preview just stays empty until it parses again, no need to surface an error.
        }
    }

    private static string DecodeBase64(string base64)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return "<invalid base64>";
        }
    }

    [RelayCommand]
    private async Task ApplyAsync() => await ApplyCoreAsync(force: false);

    [RelayCommand]
    private async Task ForceApplyAsync() => await ApplyCoreAsync(force: true);

    private async Task ApplyCoreAsync(bool force)
    {
        IsBusy = true;
        ConflictDetails = null;
        StatusMessage = null;
        try
        {
            var applied = await _client.ApplyYamlAsync(_descriptor, _namespace, _name, YamlText, FieldManager, force);
            YamlText = applied.ToYaml();
            IsDirty = false;
            StatusMessage = $"Applied at {DateTimeOffset.Now:T}.";
        }
        catch (ServerSideApplyConflictException ex)
        {
            ConflictDetails = ex.Message;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Apply failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsBusy = true;
        StatusMessage = null;
        ConflictDetails = null;
        try
        {
            var current = await _client.ReadResourceAsync(_descriptor, _namespace, _name);
            if (current is null)
            {
                IsDeleted = true;
                StatusMessage = "This resource no longer exists on the server.";
                return;
            }

            YamlText = current.ToYaml();
            IsDirty = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Reload failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RequestDelete() => IsConfirmingDelete = true;

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        IsBusy = true;
        try
        {
            await _client.DeleteResourceAsync(_descriptor, _namespace, _name);
            IsDeleted = true;
            StatusMessage = "Deleted.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Delete failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsConfirmingDelete = false;
        }
    }
}
