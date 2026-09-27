using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// A failed connect, stated in the content area where the list would have been (UI rule 9
/// names "disconnected" in its own list of states, and a failed connect used to leave the
/// pane blank with the whole message in a status bar that does not wrap). What it shows is
/// <see cref="ConnectionFailureReport"/>: the step that failed, the cause in one sentence,
/// the exception's own text, what usually fixes it, and the facts the attempt was made
/// with — the kubeconfig file, context, cluster, server, user and sign-in method, which the
/// app always had and never showed.
/// </summary>
/// <remarks>
/// One view for both modes: <c>ConnectionFailureView</c> is hosted by the Resources list
/// and by the Applications page, so the two cannot say different things about the same
/// failure. Its two actions are the tab's own commands — Retry is
/// <see cref="ClusterTabViewModel.ReconnectCommand"/>, which re-reads the kubeconfig and
/// re-runs any credential plugin, and the terminal is the tab's "open a terminal on this
/// cluster", for running <c>aws sso login</c> against the right kubeconfig.
/// </remarks>
public sealed partial class ConnectionFailureViewModel : ObservableObject
{
    private readonly ClusterTabViewModel _tab;

    public ConnectionFailureViewModel(ConnectionFailureReport report, ClusterTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(tab);

        Report = report;
        _tab = tab;
        _tab.PropertyChanged += OnTabPropertyChanged;
    }

    public ConnectionFailureReport Report { get; }

    public string Title => $"Could not connect to {_tab.Header}";

    public string Step => $"Failed while: {Report.StepPhrase}";

    public string Headline => Report.Headline;

    public string Detail => Report.Detail;

    public string? Advice => Report.Advice;

    public bool HasAdvice => Report.Advice is { Length: > 0 };

    public IReadOnlyList<ConnectionFact> Facts => Report.Facts;

    public IAsyncRelayCommand RetryCommand => _tab.ReconnectCommand;

    public IAsyncRelayCommand OpenTerminalCommand => _tab.OpenInTerminalCommand;

    /// <summary>What happened when the terminal was asked for — the tab's own notice, shown here too.</summary>
    public string? TerminalNotice => _tab.TerminalNotice;

    public bool HasTerminalNotice => _tab.TerminalNotice is not null;

    /// <summary>Stops following the tab; called when the tab drops this failure.</summary>
    internal void Detach() => _tab.PropertyChanged -= OnTabPropertyChanged;

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClusterTabViewModel.TerminalNotice))
        {
            OnPropertyChanged(nameof(TerminalNotice));
            OnPropertyChanged(nameof(HasTerminalNotice));
        }
    }
}
