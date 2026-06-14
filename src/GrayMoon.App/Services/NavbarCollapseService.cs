namespace GrayMoon.App.Services;

public sealed class NavbarCollapseService(Repositories.AppSettingRepository settings)
{
    public bool IsCollapsed { get; private set; }
    public event Action? Changed;

    public void LoadSilently(bool collapsed) => IsCollapsed = collapsed;

    public void Toggle()
    {
        IsCollapsed = !IsCollapsed;
        Changed?.Invoke();
        _ = settings.SetBoolAsync(
            Repositories.AppSettingRepository.SidebarCollapsedKey, IsCollapsed);
    }
}
