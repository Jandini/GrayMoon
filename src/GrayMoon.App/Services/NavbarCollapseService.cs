namespace GrayMoon.App.Services;

public sealed class NavbarCollapseService
{
    public bool IsCollapsed { get; private set; }
    public void LoadSilently(bool collapsed) => IsCollapsed = collapsed;
}
