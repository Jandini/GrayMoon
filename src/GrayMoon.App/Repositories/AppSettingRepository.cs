using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Repositories;

public class AppSettingRepository(AppDbContext db)
{
    public const string WorkspaceRootPathKey = "WorkspaceRootPath";

    public async Task<string?> GetValueAsync(string key)
    {
        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        return setting?.Value;
    }

    public async Task SetValueAsync(string key, string? value)
    {
        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);

        if (string.IsNullOrWhiteSpace(value))
        {
            if (setting != null)
            {
                db.AppSettings.Remove(setting);
                await db.SaveChangesAsync();
            }
            return;
        }

        if (setting == null)
        {
            db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
            db.AppSettings.Update(setting);
        }

        await db.SaveChangesAsync();
    }
}
