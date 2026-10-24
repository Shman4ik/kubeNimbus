using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

// New YAML… (FEAT-12, the paste-or-type slice). Reached from the ☰ menu and the palette; no
// always-visible control (UI rule 1). Local-file open and a template library are not here.
public sealed partial class ClusterTabViewModel
{
    private int _newYamlCount;

    /// <summary>
    /// Only with an API server to apply to: not on the demo cluster (there is nothing to
    /// create on), and not before the tab has connected.
    /// </summary>
    public bool CanCreateFromYaml => Client is not null && !IsDemo;

    /// <summary>
    /// The namespace a new namespaced object without one goes to: the list's own namespace
    /// when it shows one (the first, when it shows several), else the context's, else
    /// <c>default</c> — kubectl's order.
    /// </summary>
    public string NewObjectNamespace =>
        SelectedNamespaces is [var first, ..] ? first
        : Context.Namespace is { Length: > 0 } contextNamespace ? contextNamespace
        : "default";

    /// <summary>
    /// Opens an empty editor in the dock. Always a new tab, never a preview and never one
    /// that replaces another editor (UI rule 5): each has its own key.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCreateFromYaml))]
    private void NewYaml() => OpenNewYamlEditor();

    private YamlEditorTabViewModel? OpenNewYamlEditor(Action<YamlEditorTabViewModel>? fill = null)
    {
        if (Client is not { } client)
        {
            return null;
        }

        _newYamlCount++;
        var editor = new YamlEditorTabViewModel(
            client,
            async (group, kind, cancellationToken) => (await client.GetResourceCatalogAsync(cancellationToken))
                .FirstOrDefault(d => d.Group == group && d.Kind == kind),
            NewObjectNamespace,
            _newYamlCount)
        {
            IsPreview = false,
        };

        fill?.Invoke(editor);
        AddInspectorTab(editor, replacePreview: false);
        return editor;
    }

    // ------------------------------------------------------- Open a YAML file (#301, FEAT-12)
    //
    // The same new-document editor, filled from a local file the user picked. Nothing about
    // the file is stored: not its path, not a recent-files list, not its text beyond the
    // editor's own buffer. A multi-document file opens on its first document, and the
    // editor's InfoBar says how many were left out (YamlEditorTabViewModel.LoadFromFile).

    /// <summary>
    /// The largest file the editor opens: an object the API server stores is at most about
    /// 1.5 MiB (etcd's request limit), so a file past this is not one object's manifest, and
    /// reading it whole into the editor would only stall the window.
    /// </summary>
    internal const int MaxYamlFileBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Asks for a file and reads it: its name and text, or null when the picker was dismissed.
    /// A seam for the tests, which have no window to open a picker in.
    /// </summary>
    internal Func<Task<PickedYamlFile?>> PickYamlFile { get; set; } = PickYamlFileFromDiskAsync;

    /// <summary>Opens a local YAML file into a new editor, through the same Review and server-side apply as New YAML….</summary>
    [RelayCommand(CanExecute = nameof(CanCreateFromYaml))]
    private async Task OpenYamlFileAsync()
    {
        PickedYamlFile? file;
        try
        {
            file = await PickYamlFile();
        }
        catch (YamlFileTooLargeException ex)
        {
            Status = $"{ex.FileName} was not opened: it is over {MaxYamlFileBytes / (1024 * 1024)} MiB, larger than any one object a cluster stores.";
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            Status = $"The file was not opened: {ex.Message}";
            return;
        }

        if (file is null)
        {
            return;
        }

        OpenNewYamlEditor(editor => editor.LoadFromFile(file.Name, file.Text));
    }

    private static async Task<PickedYamlFile?> PickYamlFileFromDiskAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow.StorageProvider: { } storage })
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Open a YAML file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("YAML") { Patterns = ["*.yaml", "*.yml"] },
                Avalonia.Platform.Storage.FilePickerFileTypes.All,
            ],
        });
        if (files.Count == 0)
        {
            return null;
        }

        using var picked = files[0];
        await using var stream = await picked.OpenReadAsync();
        return new PickedYamlFile(picked.Name, await ReadYamlTextAsync(picked.Name, stream));
    }

    /// <summary>
    /// Reads a picked file as UTF-8 text, refusing one past <see cref="MaxYamlFileBytes"/>
    /// before it is read whole, and refusing bytes that are not UTF-8 rather than opening them
    /// as replacement characters an apply would send.
    /// </summary>
    internal static async Task<string> ReadYamlTextAsync(string fileName, Stream stream)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > MaxYamlFileBytes)
            {
                throw new YamlFileTooLargeException(fileName);
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read));
        }

        var strict = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        // A byte-order mark is not part of the text.
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            bytes = bytes[3..];
        }

        return strict.GetString(bytes);
    }
}

/// <summary>A local file the user picked: its name (never its path, which nothing needs) and its text.</summary>
internal sealed record PickedYamlFile(string Name, string Text);

/// <summary>A picked file larger than <see cref="ClusterTabViewModel.MaxYamlFileBytes"/>.</summary>
internal sealed class YamlFileTooLargeException(string fileName) : IOException($"{fileName} is too large to open.")
{
    public string FileName { get; } = fileName;
}
