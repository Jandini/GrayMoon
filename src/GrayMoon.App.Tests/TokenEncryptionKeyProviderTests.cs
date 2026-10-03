using System.Security.Cryptography;
using GrayMoon.App.Services.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public class TokenEncryptionKeyProviderTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gm-tokenkey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static IConfiguration BuildConfig(string dbPath, string? tokenKey = null, string? tokenKeyId = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = $"Data Source={dbPath}"
        };
        if (tokenKey is not null) values["TokenKey"] = tokenKey;
        if (tokenKeyId is not null) values["TokenKeyId"] = tokenKeyId;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void New_install_generates_a_key_file_and_reuses_the_same_key_on_the_next_start()
    {
        var dir = NewTempDir();
        try
        {
            var dbPath = Path.Combine(dir, "graymoon.db");
            var config = BuildConfig(dbPath);
            var logger = NullLogger<TokenEncryptionKeyProvider>.Instance;

            var provider1 = new TokenEncryptionKeyProvider(config, logger);
            var key1 = provider1.GetCurrentKey(out var keyId1);
            Assert.Equal(TokenEncryptionKeyProvider.InstallKeyId, keyId1);

            var keyFilePath = Path.Combine(dir, "graymoon.key");
            Assert.True(File.Exists(keyFilePath));

            var provider2 = new TokenEncryptionKeyProvider(config, logger);
            var key2 = provider2.GetCurrentKey(out var keyId2);

            Assert.Equal(keyId1, keyId2);
            Assert.Equal(key1, key2);

            // A token encrypted under the first provider's key decrypts under the second (same key file).
            var protector1 = new AesGcmTokenProtector(provider1);
            var protector2 = new AesGcmTokenProtector(provider2);
            var protected1 = protector1.Protect("ghp_example_token");
            Assert.Equal("ghp_example_token", protector2.Unprotect(protected1));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_configured_TokenKey_always_wins_and_no_key_file_is_written()
    {
        var dir = NewTempDir();
        try
        {
            var dbPath = Path.Combine(dir, "graymoon.db");
            var keyBytes = RandomNumberGenerator.GetBytes(32);
            var config = BuildConfig(dbPath, tokenKey: Convert.ToBase64String(keyBytes), tokenKeyId: "my-key");
            var logger = NullLogger<TokenEncryptionKeyProvider>.Instance;

            var provider = new TokenEncryptionKeyProvider(config, logger);
            var key = provider.GetCurrentKey(out var keyId);

            Assert.Equal("my-key", keyId);
            Assert.Equal(keyBytes, key);
            Assert.False(File.Exists(Path.Combine(dir, "graymoon.key")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_token_encrypted_with_the_legacy_key_is_readable_and_reports_the_legacy_key_id()
    {
        var dir = NewTempDir();
        try
        {
            var dbPath = Path.Combine(dir, "graymoon.db");
            var config = BuildConfig(dbPath);
            var logger = NullLogger<TokenEncryptionKeyProvider>.Instance;

            // Simulate an old install: a protector whose "current" key is the legacy key.
            var legacyOnlyProvider = new LegacyOnlyKeyProvider();
            var legacyProtector = new AesGcmTokenProtector(legacyOnlyProvider);
            var legacyToken = legacyProtector.Protect("legacy-token-value");

            Assert.Equal(TokenEncryptionKeyProvider.LegacyKeyId, legacyProtector.TryGetKeyId(legacyToken));

            // The current (per-install) provider still decrypts it via GetKeyById(legacy).
            var provider = new TokenEncryptionKeyProvider(config, logger);
            var protector = new AesGcmTokenProtector(provider);
            Assert.Equal("legacy-token-value", protector.Unprotect(legacyToken));
            Assert.True(provider.IsLegacyKeyId(TokenEncryptionKeyProvider.LegacyKeyId));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Tampered_ciphertext_fails_to_decrypt()
    {
        var dir = NewTempDir();
        try
        {
            var dbPath = Path.Combine(dir, "graymoon.db");
            var config = BuildConfig(dbPath);
            var provider = new TokenEncryptionKeyProvider(config, NullLogger<TokenEncryptionKeyProvider>.Instance);
            var protector = new AesGcmTokenProtector(provider);

            var protectedValue = protector.Protect("a-secret-token");
            var parts = protectedValue.Split(':', 3);
            var payloadBytes = Convert.FromBase64String(parts[2]);
            payloadBytes[^1] ^= 0xFF; // flip a bit in the GCM tag
            var tampered = $"{parts[0]}:{parts[1]}:{Convert.ToBase64String(payloadBytes)}";

            Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(tampered));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Key_file_is_mode_600_on_non_Windows()
    {
        if (OperatingSystem.IsWindows())
            return; // DPAPI path covered by the round-trip test above; file mode is POSIX-only (R-F1).

        var dir = NewTempDir();
        try
        {
            var dbPath = Path.Combine(dir, "graymoon.db");
            _ = new TokenEncryptionKeyProvider(BuildConfig(dbPath), NullLogger<TokenEncryptionKeyProvider>.Instance);

            var mode = File.GetUnixFileMode(Path.Combine(dir, "graymoon.key"));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryGetKeyId_returns_null_for_legacy_plain_text_or_base64_values()
    {
        var dir = NewTempDir();
        var dbPath = Path.Combine(dir, "graymoon.db");
        try
        {
            var provider = new TokenEncryptionKeyProvider(BuildConfig(dbPath), NullLogger<TokenEncryptionKeyProvider>.Instance);
            var protector = new AesGcmTokenProtector(provider);

            Assert.Null(protector.TryGetKeyId("plain-text-token"));
            Assert.Null(protector.TryGetKeyId(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("plain-text-token"))));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Stands in for an old install that has never had TokenKey or a key file: current key id is
    /// the legacy id, matching production behaviour before this unit existed.</summary>
    private sealed class LegacyOnlyKeyProvider : ITokenEncryptionKeyProvider
    {
        private readonly byte[] _legacyKey = TokenEncryptionKeyProvider.DeriveLegacyKey();

        public byte[] GetCurrentKey(out string keyId)
        {
            keyId = TokenEncryptionKeyProvider.LegacyKeyId;
            return _legacyKey;
        }

        public byte[] GetKeyById(string keyId) => _legacyKey;

        public bool IsLegacyKeyId(string keyId) => string.Equals(keyId, TokenEncryptionKeyProvider.LegacyKeyId, StringComparison.Ordinal);
    }
}
