using Microsoft.AspNetCore.Components.Web;

namespace GrayMoon.App.Components.Modals;

/// <summary>
/// Shared dialog keyboard helpers. Ctrl/Cmd+Enter is handled in wwwroot/js/modal-default-action.js
/// (clicks <c>data-default-action</c>) so C# Enter handlers must ignore those combinations or the
/// default action would run twice.
/// </summary>
public static class ModalKeyboard
{
    public static bool IsPlainEnter(KeyboardEventArgs e) =>
        (e.Key == "Enter" || e.Key == "NumpadEnter")
        && !e.CtrlKey
        && !e.MetaKey
        && !e.AltKey;
}
