namespace D2Vpn.Core.Hosting;

/// <summary>
/// Where the config file lives, so every front end agrees.
/// </summary>
/// <remarks>
/// <para>
/// It did not, and the symptom was binds that looked like they had not saved: the window wrote to
/// <see cref="AppContext.BaseDirectory"/> - its own <c>bin</c> folder - while the CLI read
/// <c>d2vpn.json</c> from the working directory. Two files, each front end convinced it was the
/// only one.
/// </para>
/// <para>
/// Neither was the right place anyway. A build directory is wiped by a clean, and once this is
/// installed that same path is under Program Files, which is a per-machine location for something
/// that is per-user. Settings belong in <c>%APPDATA%</c>, where they survive a reinstall and belong
/// to the person who set them.
/// </para>
/// </remarks>
public static class ConfigLocation
{
    /// <summary>The folder holding the config.</summary>
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "d2vpn");

    /// <summary>The config file used when no path is given.</summary>
    public static string Default => Path.Combine(Folder, "d2vpn.json");

    /// <summary>The path to use, given what the caller asked for.</summary>
    public static string Resolve(string? requested) =>
        string.IsNullOrWhiteSpace(requested) ? Default : Path.GetFullPath(requested);

    /// <summary>
    /// Makes sure a config exists at <paramref name="path"/>, creating one if it does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A config from one of the places this used to look is adopted rather than ignored, so
    /// somebody who already had one does not silently start again from the template with their
    /// binds gone. Both are checked because the two front ends used different ones: the window
    /// wrote beside its executable, the CLI wrote in the working directory.
    /// </para>
    /// <para>
    /// The working directory is the weaker of the two - it is wherever the process happened to be
    /// started, which for a window launched from Explorer is not meaningful - so the executable's
    /// own folder is tried first.
    /// </para>
    /// </remarks>
    /// <returns>True if a file was created, so a caller can say where it went.</returns>
    public static bool EnsureExists(string path)
    {
        if (File.Exists(path))
        {
            return false;
        }

        var folder = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        foreach (var legacy in LegacyPaths())
        {
            if (File.Exists(legacy) && !PathsMatch(legacy, path))
            {
                File.Copy(legacy, path);
                return true;
            }
        }

        File.WriteAllText(path, ConfigTemplate.Text);
        return true;
    }

    /// <summary>Where a config may have been left before this type existed.</summary>
    public static IEnumerable<string> LegacyPaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "d2vpn.json");
        yield return Path.Combine(Environment.CurrentDirectory, "d2vpn.json");
    }

    private static bool PathsMatch(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
