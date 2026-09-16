using GrayMoon.App.Components.Modals;
using Microsoft.AspNetCore.Components.Web;

namespace GrayMoon.App.Tests;

public sealed class ModalKeyboardTests
{
    [Fact]
    public void IsPlainEnter_is_true_only_for_enter_without_modifiers()
    {
        Assert.True(ModalKeyboard.IsPlainEnter(new KeyboardEventArgs { Key = "Enter" }));
        Assert.True(ModalKeyboard.IsPlainEnter(new KeyboardEventArgs { Key = "NumpadEnter" }));
        Assert.False(ModalKeyboard.IsPlainEnter(new KeyboardEventArgs { Key = "Enter", CtrlKey = true }));
        Assert.False(ModalKeyboard.IsPlainEnter(new KeyboardEventArgs { Key = "Enter", MetaKey = true }));
        Assert.False(ModalKeyboard.IsPlainEnter(new KeyboardEventArgs { Key = "Enter", AltKey = true }));
        Assert.False(ModalKeyboard.IsPlainEnter(new KeyboardEventArgs { Key = "Escape" }));
    }
}
