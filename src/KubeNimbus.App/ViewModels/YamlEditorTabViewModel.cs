using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// View/edit tab for any resource kind → server-side apply, with delete
/// (two-step confirm, no modal dialog needed) and conflict surfacing. Content
/// is a snapshot taken at open time — live watch updates never overwrite text
/// the user might be mid-edit on; <see cref="ReloadCommand"/> re-fetches explicitly.
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

    public YamlEditorTabViewModel(ClusterClient client, ResourceDescriptor descriptor, string? @namespace, string name, string initialYaml)
        : base($"{descriptor.Kind}/{name}")
    {
        _client = client;
        _descriptor = descriptor;
        _namespace = @namespace;
        _name = name;
        _yamlText = initialYaml;
        Key = $"yaml:{descriptor.ApiVersion}/{descriptor.Kind}:{@namespace}/{name}";
    }

    partial void OnYamlTextChanged(string value) => IsDirty = true;

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
