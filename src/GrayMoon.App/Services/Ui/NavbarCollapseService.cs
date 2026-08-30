namespace GrayMoon.App.Services.Ui;

public sealed class NavbarCollapseService
{
    public bool IsCollapsed { get; private set; }
    public void LoadSilently(bool collapsed) => IsCollapsed = collapsed;
}
