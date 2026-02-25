namespace GrayMoon.App.Models;

public class AppSetting
{
    public int AppSettingId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
}
