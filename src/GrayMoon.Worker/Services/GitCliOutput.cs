namespace GrayMoon.Worker.Services;

/// <summary>Small text helpers shared by the git CLI based services.</summary>
internal static class GitCliOutput
{
    public static string? CombineOutput(string? stdout, string? stderr)
    {
        var outStr = (stdout ?? "").Trim();
        var errStr = (stderr ?? "").Trim();
        if (string.IsNullOrWhiteSpace(outStr) && string.IsNullOrWhiteSpace(errStr))
            return null;
        return string.IsNullOrWhiteSpace(outStr) ? errStr
             : string.IsNullOrWhiteSpace(errStr) ? outStr
             : $"{outStr}\n{errStr}";
    }

    public static string BuildProcessError(string? stderr, string? stdout, string fallback)
        => (!string.IsNullOrWhiteSpace(stderr) ? stderr : stdout)?.Trim() ?? fallback;
}
