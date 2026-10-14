namespace GrayMoon.Worker.Services;

/// <summary>
/// Decides whether a file is safe to read (and rewrite) as text before any line-based parsing touches it.
/// Uses Git's own heuristic - a NUL byte in the first 8000 bytes means binary - plus a size cap, so a binary or
/// huge file configured as a version file is refused instead of being loaded whole or rewritten as text.
/// </summary>
public static class TextFileGuard
{
    /// <summary>Largest file treated as text. Version files are small; this only stops pathological reads.</summary>
    public const long MaxTextFileBytes = 16 * 1024 * 1024;

    /// <summary>Same sniff length Git uses (<c>buffer_is_binary</c>).</summary>
    private const int SniffBytes = 8000;

    /// <summary>Returns null when <paramref name="fullPath"/> can be treated as text, otherwise why it cannot.</summary>
    public static string? GetRejectionReason(string fullPath)
    {
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxTextFileBytes)
                return $"File is too large to read as text ({stream.Length} bytes).";

            var buffer = new byte[SniffBytes];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return LooksBinary(buffer.AsSpan(0, read)) ? "File is binary." : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Cannot read file: {ex.Message}";
        }
    }

    /// <summary>True when <paramref name="head"/> (the start of a file) holds a NUL byte, unless it opens with a UTF-16/UTF-32 LE or UTF-16 BE byte order mark.</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return false;
        return head.IndexOf((byte)0) >= 0;
    }
}
