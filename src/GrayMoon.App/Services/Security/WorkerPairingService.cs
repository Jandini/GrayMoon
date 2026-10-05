using System.Security.Cryptography;
using System.Text;

namespace GrayMoon.App.Services.Security;

public sealed record WorkerPairingCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>
/// One-time pairing code a person copies from the Worker page into the install script (F2). The script
/// exchanges it at <c>POST /api/worker/pair</c> for the Worker secret, so the secret itself never appears in
/// the (unauthenticated) install script. A code is valid for 10 minutes, works once, and is thrown away
/// after a few wrong guesses.
/// </summary>
public sealed class WorkerPairingService(WorkerSecretService secrets, ILogger<WorkerPairingService> logger, TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    internal const int MaxFailedAttempts = 5;
    private const int CodeDigits = 8;

    private readonly object _gate = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private string? _code;
    private DateTimeOffset _expiresAt;
    private int _failedAttempts;

    /// <summary>Issues a new code, replacing any code still pending.</summary>
    public WorkerPairingCode CreateCode()
    {
        var code = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D" + CodeDigits);
        lock (_gate)
        {
            _code = code;
            _expiresAt = _clock.GetUtcNow() + CodeLifetime;
            _failedAttempts = 0;
            return new WorkerPairingCode(code, _expiresAt);
        }
    }

    /// <summary>Returns the Worker secret when <paramref name="code"/> is the pending, unexpired code; the code is then spent.</summary>
    public string? TryRedeem(string? code)
    {
        var normalized = Normalize(code);

        lock (_gate)
        {
            if (_code is null)
                return null;

            if (_clock.GetUtcNow() >= _expiresAt)
            {
                _code = null;
                return null;
            }

            var matches = normalized.Length == CodeDigits &&
                CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(normalized), Encoding.ASCII.GetBytes(_code));
            if (matches)
            {
                _code = null;
                return secrets.Secret;
            }

            _failedAttempts++;
            if (_failedAttempts >= MaxFailedAttempts)
            {
                _code = null;
                logger.LogWarning("The worker pairing code was discarded after {Attempts} wrong attempts.", _failedAttempts);
            }

            return null;
        }
    }

    private static string Normalize(string? code) =>
        code is null ? string.Empty : new string(code.Where(char.IsAsciiDigit).ToArray());
}
