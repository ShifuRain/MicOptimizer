namespace MicOptimizer.Core.PathSetup;

/// <summary>
/// Pure text operations on a PATH-style value (folders separated by semicolons). Nothing here touches the registry,
/// so it can be tested exhaustively. Entries other than the one being changed are kept exactly as they were.
/// </summary>
public static class PathEditor
{
    public static bool Contains(string? path, string folder) =>
        Split(path).Any(entry => Same(entry, folder));

    /// <summary>Appends <paramref name="folder"/> unless it is already listed (comparing case-insensitively, ignoring quotes and a trailing slash).</summary>
    public static string Add(string? path, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("A folder is required.", nameof(folder));
        if (Contains(path, folder)) return path ?? "";

        var entries = Split(path).ToList();
        entries.Add(folder.TrimEnd('\\', '/'));
        return string.Join(';', entries);
    }

    /// <summary>Removes every entry that is <paramref name="folder"/>. Folders that merely start with the same text are left alone.</summary>
    public static string Remove(string? path, string folder) =>
        string.Join(';', Split(path).Where(entry => !Same(entry, folder)));

    private static IEnumerable<string> Split(string? path) =>
        (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Environment variables such as %USERPROFILE% are expanded for the comparison only.</summary>
    private static bool Same(string entry, string folder) =>
        string.Equals(Normalize(entry), Normalize(folder), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) =>
        Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')).TrimEnd('\\', '/');
}
