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

    /// <summary>Owner-only: read, write and search for the user, nothing for anyone else (0700).</summary>
    internal const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Owner read and write only (0600), what kubectl keeps a kubeconfig at.</summary>
    internal const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Creates <paramref name="directory"/> (and any missing parents) and, on Linux and macOS,
    /// makes it owner-only (0700) — including when it already existed with a wider mode, which
    /// is what every directory created before this was under a umask of 022.
    /// </summary>
    /// <remarks>
    /// The files in these directories carry no credential (hard rule 4), but they are not
    /// nothing: context names (an EKS context is an ARN with the account ID in it), kubeconfig
    /// paths, namespaces and the clusters' resource catalogs. kubectl keeps its kubeconfig
    /// 0600; a world-readable copy of the map of someone's clusters is the gap this closes.
    /// On Windows the per-user AppData folders are already private to their user.
    /// </remarks>
    public static void CreatePrivate(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }

        Directory.CreateDirectory(directory, PrivateDirectoryMode);
        File.SetUnixFileMode(directory, PrivateDirectoryMode);
    }

    /// <summary>
    /// Tightens the app's own two directories to owner-only when they exist, so a profile
    /// created by an older build is fixed on the next launch rather than only for files
    /// written from now on. Best effort: a failure here must not stop the app starting.
    /// </summary>
    public static void SecureExisting()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var directory in new[] { Roaming, Local })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    File.SetUnixFileMode(directory, PrivateDirectoryMode);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Options that create a new file, with <paramref name="unixMode"/> on Linux and macOS.</summary>
    internal static FileStreamOptions CreateNewOptions(UnixFileMode unixMode = PrivateFileMode)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = unixMode;
        }

        return options;
    }

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> so that a reader — or a
    /// crash, or a second instance writing at the same moment — sees the old file or the new
    /// one and never half of either: the text goes to a temporary file beside it, which then
    /// replaces it in one rename. The directory is created owner-only (<see cref="CreatePrivate"/>),
    /// and on Linux and macOS the file is created with <paramref name="unixMode"/> (0600 unless
    /// said otherwise). Throws what the file system throws; callers that are best effort catch.
    /// </summary>
    public static void WriteAllTextAtomically(string path, string contents, UnixFileMode unixMode = PrivateFileMode)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        CreatePrivate(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, CreateNewOptions(unixMode)))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
