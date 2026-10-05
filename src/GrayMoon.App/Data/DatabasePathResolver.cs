namespace GrayMoon.App.Data;

/// <summary>Parses the SQLite file path out of a "Data Source=..." connection string, shared by startup
/// (database directory, Data Protection key ring) and token-key file placement (next to the database).</summary>
public static class DatabasePathResolver
{
    public static string? GetDatabasePath(string connectionString)
    {
        const string prefix = "Data Source=";
        var idx = connectionString.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var path = connectionString[(idx + prefix.Length)..].Trim();
        return string.IsNullOrEmpty(path) ? null : path;
    }
}
