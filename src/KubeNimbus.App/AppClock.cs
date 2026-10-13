namespace KubeNimbus.App;

/// <summary>
/// The one place the app reads the current instant for anything it shows: an Age, a
/// "(43m ago)", a condition's "since", a certificate's days left, "Applied at". The app reads
/// <see cref="TimeProvider.System"/>; the screenshot harness sets <see cref="Time"/> to a fixed
/// instant (<c>DemoData.Now</c>), because a fixture's ages read against the wall clock made two
/// renders of one commit differ on either side of midnight ("74d" and "75d", #350).
/// <c>BannedSymbols.Clock.txt</c> makes a direct read of <c>DateTimeOffset.UtcNow</c> (and
/// its siblings) a build error in the app and the harness, so a new age cannot bypass it; a
/// read that measures elapsed time rather than showing one says so with a pragma.
/// </summary>
public static class AppClock
{
    /// <summary>The clock. Only the harness and tests replace it.</summary>
    public static TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>The current instant, from <see cref="Time"/>.</summary>
    public static DateTimeOffset UtcNow => Time.GetUtcNow();
}
