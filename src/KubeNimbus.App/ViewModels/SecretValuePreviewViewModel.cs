namespace KubeNimbus.App.ViewModels;

/// <summary>One decoded key/value pair shown by the YAML editor's "reveal secret values" toggle.</summary>
public sealed class SecretValuePreviewViewModel(string key, string value)
{
    public string Key { get; } = key;

    public string Value { get; } = value;
}
