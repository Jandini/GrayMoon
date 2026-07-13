namespace GrayMoon.Common.Git;

/// <summary>Maps a file extension to the Monaco editor language id used for diff syntax highlighting.</summary>
public static class MonacoLanguageMapper
{
    private static readonly Dictionary<string, string> ExtensionToLanguageId = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".razor"] = "razor",
        [".cshtml"] = "razor",
        [".json"] = "json",
        [".xml"] = "xml",
        [".csproj"] = "xml",
        [".props"] = "xml",
        [".targets"] = "xml",
        [".nuspec"] = "xml",
        [".yml"] = "yaml",
        [".yaml"] = "yaml",
        [".js"] = "javascript",
        [".mjs"] = "javascript",
        [".cjs"] = "javascript",
        [".jsx"] = "javascript",
        [".ts"] = "typescript",
        [".tsx"] = "typescript",
        [".css"] = "css",
        [".scss"] = "scss",
        [".less"] = "less",
        [".html"] = "html",
        [".htm"] = "html",
        [".sql"] = "sql",
        [".ps1"] = "powershell",
        [".psm1"] = "powershell",
        [".psd1"] = "powershell",
        [".md"] = "markdown",
        [".markdown"] = "markdown",
    };

    /// <summary>
    /// Returns the intended Monaco language id for the given path's extension, or "plaintext" if unknown.
    /// The Monaco wrapper (Stage 6) is responsible for falling back safely if a given vendored Monaco
    /// build does not actually register the returned id (e.g. "razor" has no first-class grammar).
    /// </summary>
    public static string GetLanguageId(string path)
    {
        var extension = Path.GetExtension(path);
        if (!string.IsNullOrEmpty(extension) && ExtensionToLanguageId.TryGetValue(extension, out var languageId))
        {
            return languageId;
        }

        return "plaintext";
    }
}
