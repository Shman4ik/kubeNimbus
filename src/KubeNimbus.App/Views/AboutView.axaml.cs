using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace KubeNimbus.App.Views;

/// <summary>
/// The About box: app name, release version, license. The version comes from the
/// <c>InformationalVersion</c> the release pipeline embeds, stripped of its
/// "+&lt;git-sha&gt;" build metadata — the same single source
/// (<c>Directory.Build.props</c>'s <c>VersionPrefix</c>, overridden by the tag) that
/// the release workflow uses, so this box cannot name a version the binary is not.
/// <para>
/// Hosted in the shell's About <c>OverlayPanel</c> rather than in a window of its own:
/// an About box is read once and dismissed, which is exactly what an overlay is for,
/// and a second Alt+Tab entry for four lines of text was never earning its place.
/// </para>
/// </summary>
public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();

        var assembly = Assembly.GetEntryAssembly();
        var version = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";
        VersionText.Text = $"Version {version}";

        var copyright = assembly?.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;
        CopyrightText.Text = string.IsNullOrEmpty(copyright) ? "MIT License" : $"{copyright} · MIT License";
    }

    private void OnGitHubClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/Shman4ik/kubeNimbus") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser to hand off to is not worth crashing the About box.
        }
    }
}
