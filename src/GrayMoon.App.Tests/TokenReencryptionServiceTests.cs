using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

/// <summary>
/// F1: on startup, connector tokens still protected under the legacy, source-derived key are re-encrypted
/// onto the current per-install key. A token already on the current key, or not in the versioned scheme at
/// all, is left untouched; a round-trip failure leaves the stored value exactly as it was (R-F1a #3).
/// </summary>
public class TokenReencryptionServiceTests
{
    private static async Task<AppDbContext> CreateDbAsync(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

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

    private static (ITokenEncryptionKeyProvider KeyProvider, AesGcmTokenProtector LegacyProtector) MakeInstallKeyProvider(string dir)
    {
        var dbPath = Path.Combine(dir, "graymoon.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = $"Data Source={dbPath}"
            })
            .Build();
        var keyProvider = new TokenEncryptionKeyProvider(config, NullLogger<TokenEncryptionKeyProvider>.Instance);
        var legacyProtector = new AesGcmTokenProtector(new LegacyOnlyKeyProvider());
        return (keyProvider, legacyProtector);
    }

    [Fact]
    public async Task Legacy_keyed_token_is_decrypted_and_reencrypted_onto_the_current_key()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gm-reencrypt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var (keyProvider, legacyProtector) = MakeInstallKeyProvider(dir);
            var protector = new AesGcmTokenProtector(keyProvider);

            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = await CreateDbAsync(connection);

            var legacyToken = legacyProtector.Protect("ghp_legacy_value");
            db.Connectors.Add(new Connector { ConnectorName = "GH", UserToken = legacyToken });
            await db.SaveChangesAsync();

            var count = await TokenReencryptionService.ReencryptLegacyTokensAsync(db, protector, keyProvider, NullLogger.Instance);

            Assert.Equal(1, count);
            var stored = (await db.Connectors.SingleAsync()).UserToken!;
            Assert.NotEqual(legacyToken, stored);
            Assert.NotEqual(TokenEncryptionKeyProvider.LegacyKeyId, protector.TryGetKeyId(stored));
            Assert.Equal("ghp_legacy_value", protector.Unprotect(stored));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Token_already_on_the_current_key_is_left_untouched()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gm-reencrypt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var (keyProvider, _) = MakeInstallKeyProvider(dir);
            var protector = new AesGcmTokenProtector(keyProvider);

            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = await CreateDbAsync(connection);

            var currentToken = protector.Protect("ghp_current_value");
            db.Connectors.Add(new Connector { ConnectorName = "GH", UserToken = currentToken });
            await db.SaveChangesAsync();

            var count = await TokenReencryptionService.ReencryptLegacyTokensAsync(db, protector, keyProvider, NullLogger.Instance);

            Assert.Equal(0, count);
            Assert.Equal(currentToken, (await db.Connectors.SingleAsync()).UserToken);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Null_token_and_non_versioned_token_are_left_untouched()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gm-reencrypt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var (keyProvider, _) = MakeInstallKeyProvider(dir);
            var protector = new AesGcmTokenProtector(keyProvider);

            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = await CreateDbAsync(connection);

            db.Connectors.Add(new Connector { ConnectorName = "NoToken", UserToken = null });
            db.Connectors.Add(new Connector { ConnectorName = "PlainLegacy", UserToken = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("legacy-plain")) });
            await db.SaveChangesAsync();

            var count = await TokenReencryptionService.ReencryptLegacyTokensAsync(db, protector, keyProvider, NullLogger.Instance);

            Assert.Equal(0, count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task When_current_key_is_itself_the_legacy_key_nothing_is_reencrypted()
    {
        // Simulates a failed file-key load this run (R-F1a #5): current key id equals the legacy id, so
        // there is nothing to upgrade to yet.
        var legacyOnly = new LegacyOnlyKeyProvider();
        var protector = new AesGcmTokenProtector(legacyOnly);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);

        var token = protector.Protect("ghp_value");
        db.Connectors.Add(new Connector { ConnectorName = "GH", UserToken = token });
        await db.SaveChangesAsync();

        var count = await TokenReencryptionService.ReencryptLegacyTokensAsync(db, protector, legacyOnly, NullLogger.Instance);

        Assert.Equal(0, count);
        Assert.Equal(token, (await db.Connectors.SingleAsync()).UserToken);
    }
}
