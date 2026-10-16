namespace GrayMoon.Common.Ai;

/// <summary>
/// Pure, deterministic classification and ranking of changed paths. The Worker uses it to decide which patches are
/// worth reading at all; the prompt builder uses the same scores, so both sides agree on what matters most.
/// </summary>
public static class GitEvidenceClassifier
{
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".razor", ".cshtml", ".fs", ".vb", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".py", ".go", ".rs",
        ".java", ".kt", ".swift", ".c", ".cc", ".cpp", ".h", ".hpp", ".rb", ".php", ".css", ".scss", ".less", ".html",
        ".vue", ".svelte", ".sql", ".ps1", ".psm1", ".sh", ".bash", ".xaml", ".proto", ".graphql",
    };

    private static readonly HashSet<string> ProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".sln", ".slnx",
    };

    private static readonly HashSet<string> ConfigExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".yml", ".yaml", ".xml", ".config", ".ini", ".toml", ".editorconfig", ".gitignore", ".gitattributes",
        ".env.example", ".dockerignore",
    };

    private static readonly HashSet<string> DocsExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt", ".rst", ".adoc",
    };

    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg", ".webp", ".woff", ".woff2", ".ttf", ".eot", ".otf",
        ".mp3", ".mp4", ".wav", ".pdf", ".zip", ".7z", ".gz", ".dll", ".exe", ".nupkg", ".snk",
    };

    private static readonly HashSet<string> LockFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "packages.lock.json", "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "npm-shrinkwrap.json", "Cargo.lock",
        "poetry.lock", "Pipfile.lock", "composer.lock", "Gemfile.lock", "go.sum", "paket.lock",
    };

    private static readonly HashSet<string> SensitiveFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", ".npmrc", ".pypirc", ".netrc", "credentials", "secrets.json",
        "graymoon.key",
    };

    private static readonly HashSet<string> SensitiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pem", ".key", ".pfx", ".p12", ".jks", ".keystore", ".kdbx", ".ppk", ".cer", ".crt",
    };

    public static GitEvidenceFileCategory Classify(string path)
    {
        var normalized = path.Replace('\\', '/');
        var fileName = FileName(normalized);
        var extension = Extension(fileName);
        var lower = normalized.ToLowerInvariant();

        if (LockFileNames.Contains(fileName))
            return GitEvidenceFileCategory.Lock;

        if (IsGenerated(lower, fileName))
            return GitEvidenceFileCategory.Generated;

        if (lower.StartsWith(".github/workflows/", StringComparison.Ordinal)
            || lower.StartsWith(".github/actions/", StringComparison.Ordinal)
            || fileName.Equals("azure-pipelines.yml", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".gitlab-ci.yml", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Jenkinsfile", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
            || lower.StartsWith(".circleci/", StringComparison.Ordinal))
            return GitEvidenceFileCategory.Ci;

        if (ProjectExtensions.Contains(extension)
            || fileName.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("global.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("dotnet-tools.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase))
            return GitEvidenceFileCategory.Project;

        if (AssetExtensions.Contains(extension))
            return GitEvidenceFileCategory.Asset;

        if (DocsExtensions.Contains(extension) || lower.StartsWith("docs/", StringComparison.Ordinal))
            return GitEvidenceFileCategory.Docs;

        if (IsTest(lower, fileName))
            return GitEvidenceFileCategory.Test;

        if (SourceExtensions.Contains(extension))
            return GitEvidenceFileCategory.Source;

        if (ConfigExtensions.Contains(extension) || fileName.StartsWith('.'))
            return GitEvidenceFileCategory.Config;

        return GitEvidenceFileCategory.Other;
    }

    /// <summary>Paths whose content is never read or sent: keys, certificates, credential stores, <c>.env</c> files.</summary>
    public static bool IsSensitive(string path)
    {
        var fileName = FileName(path.Replace('\\', '/'));
        if (SensitiveFileNames.Contains(fileName))
            return true;
        if (fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".example", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".sample", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".template", StringComparison.OrdinalIgnoreCase))
            return true;
        return SensitiveExtensions.Contains(Extension(fileName));
    }

    /// <summary>
    /// Relevance of a file for describing the change. Category comes first (behavior and tests over generated noise);
    /// size only adds a logarithmic bonus, so the largest patch never dominates. Huge churn is dampened because it is
    /// usually mechanical. Zero means "list it, never read its patch".
    /// </summary>
    public static double Score(GitEvidenceFile file)
    {
        if (file.IsSensitive || file.IsBinary || file.IsSubmodule || file.IsSymlink || file.Change == GitEvidenceChange.Unmerged)
            return 0;

        double weight = Weight(file.Category);
        if (weight == 0)
            return 0;

        var churn = file.Churn;
        if (churn == 0 && file.Change is GitEvidenceChange.Renamed or GitEvidenceChange.Copied)
            return 0;

        if (file.Change == GitEvidenceChange.Deleted)
            weight *= 0.4;

        var score = weight * (1 + Math.Log2(1 + Math.Min(churn, 400)));
        if (churn > 2_000)
            score *= 0.5;
        return score;
    }

    public static int Weight(GitEvidenceFileCategory category) => category switch
    {
        GitEvidenceFileCategory.Source => 10,
        GitEvidenceFileCategory.Test => 8,
        GitEvidenceFileCategory.Project => 7,
        GitEvidenceFileCategory.Ci => 6,
        GitEvidenceFileCategory.Config => 5,
        GitEvidenceFileCategory.Docs => 3,
        GitEvidenceFileCategory.Other => 3,
        GitEvidenceFileCategory.Asset => 0,
        GitEvidenceFileCategory.Lock => 0,
        GitEvidenceFileCategory.Generated => 0,
        _ => 0,
    };

    /// <summary>Highest score first, then path, so equal inputs always give the same order.</summary>
    public static IEnumerable<GitEvidenceFile> Rank(IEnumerable<GitEvidenceFile> files) =>
        files.OrderByDescending(Score)
            .ThenByDescending(f => Weight(f.Category))
            .ThenBy(f => f.Path, StringComparer.Ordinal);

    private static bool IsGenerated(string lowerPath, string fileName)
    {
        var lowerName = fileName.ToLowerInvariant();
        return lowerName.EndsWith(".g.cs", StringComparison.Ordinal)
               || lowerName.EndsWith(".g.i.cs", StringComparison.Ordinal)
               || lowerName.EndsWith(".designer.cs", StringComparison.Ordinal)
               || lowerName.EndsWith(".generated.cs", StringComparison.Ordinal)
               || lowerName.EndsWith(".min.js", StringComparison.Ordinal)
               || lowerName.EndsWith(".min.css", StringComparison.Ordinal)
               || lowerName.EndsWith(".map", StringComparison.Ordinal)
               || lowerName.EndsWith(".snap", StringComparison.Ordinal)
               || HasSegment(lowerPath, "bin") || HasSegment(lowerPath, "obj")
               || HasSegment(lowerPath, "node_modules") || HasSegment(lowerPath, "dist")
               || lowerPath.Contains("wwwroot/lib/", StringComparison.Ordinal)
               || lowerPath.Contains("/vendor/", StringComparison.Ordinal) || lowerPath.StartsWith("vendor/", StringComparison.Ordinal);
    }

    private static bool IsTest(string lowerPath, string fileName)
    {
        var lowerName = fileName.ToLowerInvariant();
        return lowerPath.Contains(".tests/", StringComparison.Ordinal)
               || lowerPath.Contains(".test/", StringComparison.Ordinal)
               || HasSegment(lowerPath, "tests") || HasSegment(lowerPath, "test") || HasSegment(lowerPath, "__tests__")
               || lowerName.EndsWith("tests.cs", StringComparison.Ordinal) || lowerName.EndsWith("test.cs", StringComparison.Ordinal)
               || lowerName.Contains(".test.", StringComparison.Ordinal) || lowerName.Contains(".spec.", StringComparison.Ordinal)
               || lowerName.StartsWith("test_", StringComparison.Ordinal);
    }

    private static bool HasSegment(string lowerPath, string segment) =>
        lowerPath.StartsWith(segment + "/", StringComparison.Ordinal)
        || lowerPath.Contains("/" + segment + "/", StringComparison.Ordinal);

    private static string FileName(string normalizedPath)
    {
        var slash = normalizedPath.LastIndexOf('/');
        return slash < 0 ? normalizedPath : normalizedPath[(slash + 1)..];
    }

    private static string Extension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot <= 0 ? (fileName.StartsWith('.') ? fileName : string.Empty) : fileName[dot..];
    }
}
