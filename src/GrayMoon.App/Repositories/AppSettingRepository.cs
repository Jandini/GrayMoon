using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public sealed class AppSettingRepository(AppDbContext db)
{
    public const string WorkspaceRootPathKey = "WorkspaceRootPath";

    /// <summary>
    /// Root directory for GrayMoon-managed Feature worktrees on the Worker host
    /// (e.g. C:\Users\name\.graymoon). Per-workspace paths are {root}\{WorkspaceName}\features.
    /// </summary>
    public const string FeatureStorageRootPathKey = "FeatureStorageRootPath";

    public const string TerminalShowByDefaultKey = "Terminal.ShowByDefault";
    public const string TerminalTransparentBackdropKey = "Terminal.TransparentBackdrop";
    public const string TerminalColorSchemeKey = "Terminal.ColorScheme";
    public const string SidebarCollapsedKey = "Sidebar.Collapsed";
    public const string TopBarShowKey = "TopBar.Show";

    /// <summary>When true, GrayMoon Desktop starts the Worker update at startup without asking.</summary>
    public const string WorkerAutoUpdateKey = "Worker.AutoUpdate";

    /// <summary>True once some Worker has presented the correct Worker secret (F2); from then on a Worker without a secret is rejected.</summary>
    public const string WorkerSecretSeenKey = "Security:WorkerSecretSeen";

    public async Task<string?> GetValueAsync(string key)
    {
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key);
        return setting?.Value;
    }

    /// <remarks>
    /// Two callers can set a key for the first time together (two tabs, two requests): both find no row and add
    /// one, and the unique index on the key rejects the later save. A caller can also find a row that another
    /// one clears before the save. Neither is a failure of this call: it reads again and stores its value, so the
    /// key ends with what this call asked for, as when nobody raced it.
    /// <para>
    /// The context is the scope's (one per circuit or request) and other services track rows in it, so the repeat
    /// must not clear the tracker as <see cref="DbWriteConflict.RetryAsync(Func{Task}, CancellationToken)"/> expects.
    /// Only the setting that collided is detached; everything else stays tracked and is saved with the repeat. A
    /// conflict on any other row of that save is not this method's and is rethrown.
    /// </para>
    /// </remarks>
    public async Task SetValueAsync(string key, string? value)
    {
        for (var attempt = 1; ; attempt++)
        {
            var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key);

            if (string.IsNullOrWhiteSpace(value))
            {
                if (setting == null)
                    return;

                db.Settings.Remove(setting);
            }
            else if (setting == null)
            {
                setting = new Setting { Key = key, Value = value };
                db.Settings.Add(setting);
            }
            else
            {
                setting.Value = value;
                db.Settings.Update(setting);
            }

            try
            {
                await db.SaveChangesAsync();
                return;
            }
            catch (DbUpdateException ex) when (attempt < DbWriteConflict.MaxAttempts
                                              && DbWriteConflict.IsLostRace(ex)
                                              && ex.Entries.Any(entry => ReferenceEquals(entry.Entity, setting)))
            {
                // Another caller added or removed this key first. Nothing of the failed save is stored. Forget this
                // row only, then read the key again and decide from what is stored now.
                db.Entry(setting).State = EntityState.Detached;
            }
        }
    }

    public async Task<bool> GetBoolAsync(string key, bool defaultValue = false)
    {
        var raw = await GetValueAsync(key);
        return ParseBool(raw, defaultValue);
    }

    public async Task SetBoolAsync(string key, bool value) =>
        await SetValueAsync(key, value ? "true" : "false");

    public static bool ParseBool(string? value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        return bool.TryParse(value, out var b) ? b : defaultValue;
    }

    public static TerminalColorScheme ParseTerminalColorScheme(string? value, TerminalColorScheme defaultValue = TerminalColorScheme.Yellow)
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        return Enum.TryParse<TerminalColorScheme>(value, ignoreCase: true, out var s) ? s : defaultValue;
    }

    public async Task<TerminalColorScheme> GetTerminalColorSchemeAsync(TerminalColorScheme defaultValue = TerminalColorScheme.Yellow)
    {
        var raw = await GetValueAsync(TerminalColorSchemeKey);
        return ParseTerminalColorScheme(raw, defaultValue);
    }

    public async Task SetTerminalColorSchemeAsync(TerminalColorScheme scheme) =>
        await SetValueAsync(TerminalColorSchemeKey, scheme.ToString());
}
