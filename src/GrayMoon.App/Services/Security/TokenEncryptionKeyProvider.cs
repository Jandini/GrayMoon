using System.Security.Cryptography;
using System.Text;

namespace GrayMoon.App.Services.Security;

/// <summary>Provides symmetric keys for token encryption. Uses TokenKey/TokenKeyId configuration with a built-in default key when none is supplied.</summary>
public sealed class TokenEncryptionKeyProvider : ITokenEncryptionKeyProvider
{
    private readonly byte[] _currentKey;
    private readonly string _currentKeyId;

    public TokenEncryptionKeyProvider(IConfiguration configuration, ILogger<TokenEncryptionKeyProvider> logger)
    {
        var keyString = configuration["TokenKey"];
        var keyId = configuration["TokenKeyId"];

        if (string.IsNullOrWhiteSpace(keyString))
        {
            // Fallback: deterministic dev key so containers start even without explicit configuration.
            // This should not be used in production.
            logger.LogWarning("TokenKey configuration value is missing. Using built-in default key; configure TokenKey for production.");
            keyString = CreateDefaultKeyString();
            keyId ??= "default";
        }

        // When TokenKey is present, first try Base64; if that fails, treat it as a plain-text password and hash to 32 bytes.
        byte[] keyBytes;
        var trimmed = keyString.Trim();
        try
        {
            keyBytes = Convert.FromBase64String(trimmed);
        }
        catch (FormatException)
        {
            // Not valid Base64: interpret as a passphrase for convenience.
            keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(trimmed));
        }

        if (keyBytes.Length != 32)
        {
            logger.LogWarning("TokenKey decoded length is {Length} bytes; AES-256 requires 32 bytes. Deriving a 32-byte key from the provided value.", keyBytes.Length);
            keyBytes = SHA256.HashData(keyBytes);
        }

        _currentKey = keyBytes;
        _currentKeyId = string.IsNullOrWhiteSpace(keyId) ? "default" : keyId.Trim();
    }

    public byte[] GetCurrentKey(out string keyId)
    {
        keyId = _currentKeyId;
        return _currentKey;
    }

    public byte[] GetKeyById(string keyId)
    {
        // For now we only support a single key. In the future this can look up keys by ID.
        return _currentKey;
    }

    private static string CreateDefaultKeyString()
    {
        // Deterministic default key derived from a fixed string.
        var seed = Encoding.UTF8.GetBytes("GrayMoon-Default-Token-Encryption-Key");
        var bytes = SHA256.HashData(seed);
        // Truncate to 32 bytes (already 32) and return Base64.
        return Convert.ToBase64String(bytes);
    }
}

