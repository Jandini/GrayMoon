using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public class AppSettingRepository(AppDbContext db)
{
    public const string WorkspaceRootPathKey = "WorkspaceRootPath";

    public const string TerminalShowByDefaultKey = "Terminal.ShowByDefault";
    public const string TerminalTransparentBackdropKey = "Terminal.TransparentBackdrop";
    public const string TerminalColorSchemeKey = "Terminal.ColorScheme";

    public async Task<string?> GetValueAsync(string key)
    {
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key);
        return setting?.Value;
    }

    public async Task SetValueAsync(string key, string? value)
    {
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key);

        if (string.IsNullOrWhiteSpace(value))
        {
            if (setting != null)
            {
                db.Settings.Remove(setting);
                await db.SaveChangesAsync();
            }
            return;
        }

        if (setting == null)
        {
            db.Settings.Add(new Setting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
            db.Settings.Update(setting);
        }

        await db.SaveChangesAsync();
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

    public static TerminalColorScheme ParseTerminalColorScheme(string? value, TerminalColorScheme defaultValue = TerminalColorScheme.Green)
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        return Enum.TryParse<TerminalColorScheme>(value, ignoreCase: true, out var s) ? s : defaultValue;
    }

    public async Task<TerminalColorScheme> GetTerminalColorSchemeAsync(TerminalColorScheme defaultValue = TerminalColorScheme.Green)
    {
        var raw = await GetValueAsync(TerminalColorSchemeKey);
        return ParseTerminalColorScheme(raw, defaultValue);
    }

    public async Task SetTerminalColorSchemeAsync(TerminalColorScheme scheme) =>
        await SetValueAsync(TerminalColorSchemeKey, scheme.ToString());
}
