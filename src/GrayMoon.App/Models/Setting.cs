namespace GrayMoon.App.Models;

public class Setting
{
    public int SettingId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
}
