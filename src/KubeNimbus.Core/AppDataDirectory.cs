namespace KubeNimbus.Core;

/// <summary>
/// The two directories kubeNimbus writes its own files under — preferences and session
/// state in <see cref="Roaming"/>, the discovery cache in <see cref="Local"/> — resolved
/// so that the answer is always an absolute path.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> returns an
/// <b>empty string</b> when the folder does not exist, which on Linux is the ordinary
/// state of a fresh <c>HOME</c>: <c>~/.local/share</c> and <c>~/.config</c> are created by
/// whichever program first needs them. <c>Path.Combine("", "kubeNimbus", …)</c> is then a
/// relative path, and every store that built its location that way wrote into the
/// <em>current directory</em> — launched from the repository root, the discovery cache
/// appeared as <c>./kubeNimbus/discovery/&lt;hash&gt;.json</c>. The settings and workspace
/// stores had the same pattern.
/// </para>
/// <para>
/// The resolution order is: the platform's own answer, asked to create the folder
/// (<see cref="Environment.SpecialFolderOption.Create"/>); then the XDG variable for it
/// when that is absolute (the XDG spec says a relative value must be ignored); then the
/// XDG default under the home directory; and last, the temp directory. The last one loses
/// preferences across a reboot, which is a real cost — and the right one next to writing
/// files into whatever directory the app happened to be started from.
/// </para>
/// </remarks>
public static class AppDataDirectory
{
    /// <summary>The app's own name as a directory, shared by every store.</summary>
    public const string AppFolderName = "kubeNimbus";

    /// <summary>Preferences, the workspace and the terminal overlays: <c>%APPDATA%</c>, <c>~/.config</c>.</summary>
    public static string Roaming => Path.Combine(
        Resolve(Environment.SpecialFolder.ApplicationData, "XDG_CONFIG_HOME", ".config"), AppFolderName);

    /// <summary>Caches: <c>%LOCALAPPDATA%</c>, <c>~/.local/share</c>.</summary>
    public static string Local => Path.Combine(
        Resolve(Environment.SpecialFolder.LocalApplicationData, "XDG_DATA_HOME", Path.Combine(".local", "share")), AppFolderName);

    private static string Resolve(Environment.SpecialFolder folder, string xdgVariable, string xdgDefault)
    {
        string special;
        try
        {
            special = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.Create);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            special = "";
        }

        return Choose(
            special,
            Environment.GetEnvironmentVariable(xdgVariable),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            xdgDefault,
            Path.GetTempPath());
    }

    /// <summary>
    /// The decision, with every input passed in so it can be asserted on any platform.
    /// Returns the first candidate that is a fully qualified path, and the temp
    /// directory's <c>kubeNimbus-fallback</c> child when none is.
    /// </summary>
    internal static string Choose(string? special, string? xdgValue, string? home, string xdgDefault, string tempRoot)
    {
        if (IsAbsolute(special))
        {
            return special!;
        }

        if (IsAbsolute(xdgValue))
        {
            return xdgValue!;
        }

        if (IsAbsolute(home))
        {
            return Path.Combine(home!, xdgDefault);
        }

        return Path.Combine(tempRoot, "kubeNimbus-fallback");
    }

    private static bool IsAbsolute(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);
}
