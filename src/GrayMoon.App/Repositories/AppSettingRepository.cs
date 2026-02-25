using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public class AppSettingRepository(AppDbContext db)
{
    public const string WorkspaceRootPathKey = "WorkspaceRootPath";

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
}
