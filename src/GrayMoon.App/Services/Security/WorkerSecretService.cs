using System.Security.Cryptography;
using System.Text;
using GrayMoon.App.Data;
using GrayMoon.App.Repositories;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Security;

public enum WorkerSecretCheck
{
    /// <summary>The request carried no Worker secret (a Worker installed before secrets existed, or some other program).</summary>
    Missing,

    /// <summary>The request carried a secret that does not match.</summary>
    Wrong,

    Valid
}

/// <summary>Persists whether some Worker has already proved it holds the secret (<see cref="AppSettingRepository.WorkerSecretSeenKey"/>).</summary>
public interface IWorkerSecretSeenStore
{
    Task<bool> GetAsync(CancellationToken cancellationToken);

    Task SetAsync(bool seen, CancellationToken cancellationToken);
}

public sealed class DbWorkerSecretSeenStore(IDbContextFactory<AppDbContext> dbFactory) : IWorkerSecretSeenStore
{
    public async Task<bool> GetAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await new AppSettingRepository(db).GetBoolAsync(AppSettingRepository.WorkerSecretSeenKey);
    }

    public async Task SetAsync(bool seen, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await new AppSettingRepository(db).SetBoolAsync(AppSettingRepository.WorkerSecretSeenKey, seen);
    }
}

/// <summary>
/// The secret only the real Worker presents to <c>/hub/agent</c> and <c>/repos/{id}/connector</c> (F2).
///
/// The secret is generated on first start and kept in <c>graymoon-worker.secret</c> next to the database (same
/// folder as the token key file; the Docker volume already persists it). It is plain text so GrayMoon.Desktop,
/// which runs as the same user, can hand it to the Worker; Unix mode 600 elsewhere. The install script
/// never contains it: a Worker gets it from Desktop or by redeeming a one-time pairing code
/// (<see cref="WorkerPairingService"/>).
///
/// If the file is missing at startup a new secret is generated and "seen" is reset, so the next Worker
/// connects with a warning instead of being locked out.
/// </summary>
public sealed class WorkerSecretService
{
    public const string SecretFileName = "graymoon-worker.secret";

    private readonly IWorkerSecretSeenStore _seenStore;
    private readonly ILogger<WorkerSecretService> _logger;
    private readonly byte[] _secretBytes;
    private readonly bool _generatedAtStartup;
    private volatile bool _seen;
    private volatile bool _unsecuredWorkerConnected;

    public WorkerSecretService(IConfiguration configuration, IWorkerSecretSeenStore seenStore, ILogger<WorkerSecretService> logger)
        : this(ResolveSecretFilePath(configuration.GetConnectionString("DefaultConnection") ?? "Data Source=db/graymoon.db"), seenStore, logger)
    {
    }

    internal WorkerSecretService(string secretFilePath, IWorkerSecretSeenStore seenStore, ILogger<WorkerSecretService> logger)
    {
        _seenStore = seenStore;
        _logger = logger;
        SecretFilePath = secretFilePath;
        Secret = LoadOrCreate(secretFilePath, logger, out _generatedAtStartup);
        _secretBytes = Encoding.UTF8.GetBytes(Secret);
    }

    public string SecretFilePath { get; }

    /// <summary>The secret itself. Only the pairing endpoint hands this out, and only for a valid one-time code.</summary>
    internal string Secret { get; }

    /// <summary>True once some Worker has presented the correct secret; a Worker without one is rejected from then on.</summary>
    public bool Seen => _seen;

    /// <summary>True while the connected Worker was accepted without a secret; drives the "Reinstall the Worker" notice.</summary>
    public bool UnsecuredWorkerConnected => _unsecuredWorkerConnected;

    /// <summary>Loads the persisted "seen" flag. Call once at startup, after the database exists.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var seen = await _seenStore.GetAsync(cancellationToken);
            if (_generatedAtStartup && seen)
            {
                seen = false;
                await _seenStore.SetAsync(false, cancellationToken);
                _logger.LogWarning("The worker secret was regenerated; the next Worker may connect without it until it is reinstalled.");
            }

            _seen = seen;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load the worker secret state; continuing as if no Worker has presented the secret yet.");
        }
    }

    public WorkerSecretCheck Check(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
            return WorkerSecretCheck.Missing;

        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(presentedBytes, _secretBytes)
            ? WorkerSecretCheck.Valid
            : WorkerSecretCheck.Wrong;
    }

    /// <summary>Records that a Worker presented the correct secret and persists it the first time.</summary>
    public async Task NoteValidSecretAsync(CancellationToken cancellationToken = default)
    {
        _unsecuredWorkerConnected = false;
        if (_seen)
            return;

        _seen = true;
        try
        {
            await _seenStore.SetAsync(true, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist that a Worker presented the worker secret.");
        }
    }

    /// <summary>Records that a Worker without a secret was accepted. Returns true only the first time per connection.</summary>
    public bool NoteUnsecuredWorker()
    {
        if (_unsecuredWorkerConnected)
            return false;

        _unsecuredWorkerConnected = true;
        return true;
    }

    public void NoteWorkerDisconnected() => _unsecuredWorkerConnected = false;

    internal static string ResolveSecretFilePath(string connectionString)
    {
        var dbPath = DatabasePathResolver.GetDatabasePath(connectionString);
        var directory = string.IsNullOrEmpty(dbPath) ? "db" : (Path.GetDirectoryName(dbPath) ?? "db");
        if (string.IsNullOrEmpty(directory))
            directory = ".";
        return Path.Combine(directory, SecretFileName);
    }

    internal static string GenerateSecret() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string LoadOrCreate(string filePath, ILogger logger, out bool generated)
    {
        generated = false;
        try
        {
            if (File.Exists(filePath))
            {
                var existing = File.ReadAllText(filePath).Trim();
                if (existing.Length > 0)
                    return existing;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not read the worker secret file at {SecretFilePath}; generating a new secret.", filePath);
        }

        var secret = GenerateSecret();
        generated = true;
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(filePath, secret);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            logger.LogInformation("Generated a new worker secret at {SecretFilePath}.", filePath);
        }
        catch (Exception ex)
        {
            // Never fail startup: the secret still works for this run; it just will not survive a restart.
            logger.LogError(ex, "Could not save the worker secret to {SecretFilePath}. It will be regenerated on the next start.", filePath);
        }

        return secret;
    }
}
