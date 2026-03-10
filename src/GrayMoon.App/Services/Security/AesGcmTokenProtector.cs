using System.Security.Cryptography;
using System.Text;

namespace GrayMoon.App.Services.Security;

/// <summary>Protects tokens using AES-256-GCM with a simple versioned format: v2:KEYID:BASE64(IV||CIPHERTEXT||TAG).</summary>
public sealed class AesGcmTokenProtector : ITokenProtector
{
    private const string Scheme = "v2";
    private const int TagSizeBytes = 16; // 128-bit GCM authentication tag
    private readonly ITokenEncryptionKeyProvider _keyProvider;

    public AesGcmTokenProtector(ITokenEncryptionKeyProvider keyProvider)
    {
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
    }

    public string Protect(string plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(plainText));

        var input = plainText.Trim();
        var bytes = Encoding.UTF8.GetBytes(input);

        var key = _keyProvider.GetCurrentKey(out var keyId);
        var iv = RandomNumberGenerator.GetBytes(12); // recommended size for GCM
        var ciphertext = new byte[bytes.Length];
        var tag = new byte[TagSizeBytes];

        using (var aes = new AesGcm(key, TagSizeBytes))
        {
            aes.Encrypt(iv, bytes, ciphertext, tag);
        }

        var combined = new byte[iv.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(iv, 0, combined, 0, iv.Length);
        Buffer.BlockCopy(ciphertext, 0, combined, iv.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, iv.Length + ciphertext.Length, tag.Length);

        var payload = Convert.ToBase64String(combined);
        return $"{Scheme}:{keyId}:{payload}";
    }

    public string Unprotect(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(protectedValue));

        var trimmed = protectedValue.Trim();

        // Legacy Level 1: Base64 (or plain text) without scheme prefix.
        if (!trimmed.StartsWith("v", StringComparison.Ordinal))
        {
            // Try Base64 first, otherwise treat as legacy plain-text token.
            try
            {
                var bytes = Convert.FromBase64String(trimmed);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (FormatException)
            {
                return trimmed;
            }
        }

        var parts = trimmed.Split(':', 3);
        if (parts.Length != 3 || !string.Equals(parts[0], Scheme, StringComparison.Ordinal))
            throw new InvalidOperationException("Unsupported token protection format.");

        var keyId = parts[1];
        var payload = parts[2];

        var combined = Convert.FromBase64String(payload);
        if (combined.Length < 12 + TagSizeBytes)
            throw new InvalidOperationException("Invalid protected token payload.");

        var iv = new byte[12];
        Buffer.BlockCopy(combined, 0, iv, 0, iv.Length);

        var tag = new byte[TagSizeBytes];
        Buffer.BlockCopy(combined, combined.Length - tag.Length, tag, 0, tag.Length);

        var ciphertextLength = combined.Length - iv.Length - tag.Length;
        var ciphertext = new byte[ciphertextLength];
        Buffer.BlockCopy(combined, iv.Length, ciphertext, 0, ciphertextLength);

        var key = _keyProvider.GetKeyById(keyId);
        var plaintext = new byte[ciphertext.Length];

        using (var aes = new AesGcm(key, TagSizeBytes))
        {
            aes.Decrypt(iv, ciphertext, tag, plaintext);
        }

        return Encoding.UTF8.GetString(plaintext);
    }
}

