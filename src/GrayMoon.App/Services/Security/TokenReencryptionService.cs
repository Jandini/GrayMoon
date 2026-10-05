using GrayMoon.App.Data;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Security;

/// <summary>
/// Re-encrypts connector tokens that are still protected under the legacy, source-derived key
/// (<see cref="TokenEncryptionKeyProvider.LegacyKeyId"/>) onto the current per-install key. Runs once at
/// startup, after the U0-2 migration backup; never fails startup (R-F1a #5).
/// </summary>
public static class TokenReencryptionService
{
    public static async Task<int> ReencryptLegacyTokensAsync(
        AppDbContext dbContext,
        ITokenProtector tokenProtector,
        ITokenEncryptionKeyProvider keyProvider,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var reencrypted = 0;
        try
        {
            keyProvider.GetCurrentKey(out var currentKeyId);
            if (keyProvider.IsLegacyKeyId(currentKeyId))
            {
                // The current key is itself the legacy key (the per-install key file failed to load this
                // run); nothing to upgrade to yet. Try again on the next successful start.
                return 0;
            }

            var connectors = await dbContext.Connectors
                .Where(c => c.UserToken != null)
                .ToListAsync(cancellationToken);

            foreach (var connector in connectors)
            {
                var stored = connector.UserToken;
                if (string.IsNullOrEmpty(stored))
                    continue;

                string? keyId;
                try
                {
                    keyId = tokenProtector.TryGetKeyId(stored);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not inspect the stored token for connector {ConnectorId} during startup re-encryption; leaving it unchanged.", connector.ConnectorId);
                    continue;
                }

                if (keyId is null || !keyProvider.IsLegacyKeyId(keyId))
                    continue; // already current, or not in the versioned scheme at all (nothing to migrate).

                try
                {
                    // R-F1a #3: only replace the stored value once the round trip (decrypt old key, encrypt
                    // new key) succeeds. A failure here leaves the value exactly as it was.
                    var plainText = tokenProtector.Unprotect(stored);
                    connector.UserToken = tokenProtector.Protect(plainText);
                    reencrypted++;
                }
                catch (Exception ex)
                {
                    // Leave it under the legacy key; TokenHealthBackgroundService reports it as
                    // undecryptable ("token needs to be re-entered") instead of this step throwing.
                    logger.LogWarning(ex, "Could not re-encrypt the stored token for connector {ConnectorId}; it will be reported by the token health check instead.", connector.ConnectorId);
                }
            }

            if (reencrypted > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Re-encrypted {Count} connector token(s) from the legacy key to the current per-install key.", reencrypted);
            }
        }
        catch (Exception ex)
        {
            // R-F1a #5: never fail startup.
            logger.LogError(ex, "Startup token re-encryption failed; continuing startup. Existing tokens are unaffected.");
        }

        return reencrypted;
    }
}
