using System.Security.Cryptography;
using System.Text;
using GrayMoon.App.Data;

namespace GrayMoon.App.Services.Security;

/// <summary>
/// Provides symmetric keys for token encryption.
///
/// Precedence (R-F1a #1 - a configured key always wins and is never replaced):
/// 1. <c>TokenKey</c> configuration (settings or environment).
/// 2. A per-install key file (<c>graymoon.key</c>) next to the database, generated on first start.
///    DPAPI-protected (CurrentUser) on Windows; stored as raw bytes with file mode 600 elsewhere.
///
/// The old hard-coded, source-derived key (<see cref="LegacyKeyId"/>) is kept available forever, but only
/// for decrypting tokens that were encrypted before a per-install key existed. It is never used to encrypt
/// new values.
/// </summary>
public sealed class TokenEncryptionKeyProvider : ITokenEncryptionKeyProvider
{
    /// <summary>Key id used by every install before this unit; read-only, decrypt-only forever.</summary>
    public const string LegacyKeyId = "default";

    /// <summary>Key id for the per-install generated key file.</summary>
    public const string InstallKeyId = "install";

    /// <summary>Key id for an explicitly configured <c>TokenKey</c> with no <c>TokenKeyId</c> given.</summary>
    public const string ConfiguredKeyId = "configured";

    private const string KeyFileName = "graymoon.key";

    private readonly byte[] _legacyKey = DeriveLegacyKey();
    private readonly byte[] _currentKey;
    private readonly string _currentKeyId;

    public TokenEncryptionKeyProvider(IConfiguration configuration, ILogger<TokenEncryptionKeyProvider> logger)
    {
        var keyString = configuration["TokenKey"];

        if (!string.IsNullOrWhiteSpace(keyString))
        {
            var keyId = configuration["TokenKeyId"];
            _currentKey = ParseConfiguredKey(keyString, logger);
            _currentKeyId = string.IsNullOrWhiteSpace(keyId) ? ConfiguredKeyId : keyId.Trim();
            return;
        }

        var connectionString = configuration.GetConnectionString("DefaultConnection") ?? "Data Source=db/graymoon.db";
        var keyFilePath = ResolveKeyFilePath(connectionString);

        try
        {
            _currentKey = LoadOrCreateFileKey(keyFilePath, logger);
            _currentKeyId = InstallKeyId;
        }
        catch (Exception ex)
        {
            // R-F1a #5: never fail startup. Fall back to the legacy key so tokens stay readable;
            // the next successful start will try the file key again.
            logger.LogError(ex, "Could not load or create the per-install token key file at {KeyFilePath}. " +
                "Using the legacy built-in key until this is fixed; configure TokenKey to avoid this message.", keyFilePath);
            _currentKey = _legacyKey;
            _currentKeyId = LegacyKeyId;
        }
    }

    public byte[] GetCurrentKey(out string keyId)
    {
        keyId = _currentKeyId;
        return _currentKey;
    }

    public byte[] GetKeyById(string keyId)
    {
        if (string.Equals(keyId, _currentKeyId, StringComparison.Ordinal))
            return _currentKey;

        if (IsLegacyKeyId(keyId))
            return _legacyKey;

        // Unknown key id (for example a key rotated away in a future version): best effort, matches the
        // single-key behaviour this provider always had before per-install keys existed.
        return _currentKey;
    }

    public bool IsLegacyKeyId(string keyId) => string.Equals(keyId, LegacyKeyId, StringComparison.Ordinal);

    private static byte[] ParseConfiguredKey(string keyString, ILogger logger)
    {
        var trimmed = keyString.Trim();
        byte[] keyBytes;
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

        return keyBytes;
    }

    private static string ResolveKeyFilePath(string connectionString)
    {
        var dbPath = DatabasePathResolver.GetDatabasePath(connectionString);
        var directory = string.IsNullOrEmpty(dbPath) ? "db" : (Path.GetDirectoryName(dbPath) ?? "db");
        if (string.IsNullOrEmpty(directory))
            directory = ".";
        return Path.Combine(directory, KeyFileName);
    }

    /// <summary>Loads the per-install key, generating and persisting one on first use. Internal and static so
    /// the file format (DPAPI on Windows, raw + mode 600 elsewhere) can be unit tested without a real IConfiguration.</summary>
    internal static byte[] LoadOrCreateFileKey(string keyFilePath, ILogger logger)
    {
        if (File.Exists(keyFilePath))
        {
            var stored = File.ReadAllBytes(keyFilePath);
            var keyBytes = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(stored, optionalEntropy: null, DataProtectionScope.CurrentUser)
                : stored;

            if (keyBytes.Length != 32)
                throw new InvalidOperationException($"Token key file at '{keyFilePath}' does not contain a 32-byte key.");

            return keyBytes;
        }

        var directory = Path.GetDirectoryName(keyFilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var newKey = RandomNumberGenerator.GetBytes(32);
        var toWrite = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(newKey, optionalEntropy: null, DataProtectionScope.CurrentUser)
            : newKey;

        File.WriteAllBytes(keyFilePath, toWrite);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(keyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        logger.LogInformation("Generated a new per-install token encryption key at {KeyFilePath}.", keyFilePath);
        return newKey;
    }

    internal static byte[] DeriveLegacyKey()
    {
        // Deterministic legacy key derived from a fixed string - every pre-F1 install used exactly this key.
        // Kept only to decrypt tokens that still carry LegacyKeyId; never used to encrypt new values.
        var seed = Encoding.UTF8.GetBytes("GrayMoon-Default-Token-Encryption-Key");
        return SHA256.HashData(seed);
    }
}
