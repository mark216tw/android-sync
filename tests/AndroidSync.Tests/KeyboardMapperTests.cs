using Com.AndroidSync.App.Helpers;
using Com.AndroidSync.App.Protocol;
using Windows.System;
using Xunit;

namespace Com.AndroidSync.App.Tests;

public sealed class KeyboardMapperTests
{
    [Theory]
    [InlineData(VirtualKey.A, AndroidKeyCode.A)]
    [InlineData(VirtualKey.Z, AndroidKeyCode.Z)]
    [InlineData(VirtualKey.Number0, AndroidKeyCode.Num0)]
    [InlineData(VirtualKey.Number9, AndroidKeyCode.Num9)]
    [InlineData(VirtualKey.Enter, AndroidKeyCode.Enter)]
    [InlineData(VirtualKey.Back, AndroidKeyCode.Del)]
    [InlineData(VirtualKey.Delete, AndroidKeyCode.ForwardDel)]
    [InlineData(VirtualKey.Escape, AndroidKeyCode.Escape)]
    [InlineData(VirtualKey.Left, AndroidKeyCode.DpadLeft)]
    [InlineData(VirtualKey.F12, AndroidKeyCode.F12)]
    public void TryMapVirtualKey_MapsStandardKeys(VirtualKey input, AndroidKeyCode expected)
    {
        var mapped = KeyboardMapper.TryMapVirtualKey(input, out var actual);
        Assert.True(mapped);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ShouldHandleAsKeyCode_DistinguishesShortcutsControlKeysAndPrintableText()
    {
        var ctrlMeta = KeyboardMapper.BuildMetaState(shift: false, ctrl: true, alt: false);
        var shiftMeta = KeyboardMapper.BuildMetaState(shift: true, ctrl: false, alt: false);

        // Printable characters without Ctrl/Alt go to CharacterReceived (not KeyDown)
        Assert.False(KeyboardMapper.ShouldHandleAsKeyCode(VirtualKey.A, AndroidMetaState.None));
        Assert.False(KeyboardMapper.ShouldHandleAsKeyCode(VirtualKey.A, shiftMeta));
        Assert.False(KeyboardMapper.ShouldHandleAsKeyCode(VirtualKey.Space, AndroidMetaState.None));

        // Shortcuts with Ctrl/Alt go to KeyDown
        Assert.True(KeyboardMapper.ShouldHandleAsKeyCode(VirtualKey.C, ctrlMeta));
        Assert.True(KeyboardMapper.ShouldHandleAsKeyCode(VirtualKey.A, ctrlMeta));

        // Control/navigation keys go to KeyDown
        Assert.True(KeyboardMapper.ShouldHandleAsKeyCode(VirtualKey.Enter, AndroidMetaState.None));
        Assert.True(KeyboardMapper.ShouldHandleAsKeyCode(VirtualKey.Back, AndroidMetaState.None));
        Assert.True(KeyboardMapper.ShouldHandleAsKeyCode(VirtualKey.Up, AndroidMetaState.None));

        // Modifier keys themselves are ignored
        Assert.False(KeyboardMapper.ShouldHandleAsKeyCode(VirtualKey.Control, ctrlMeta));
        Assert.False(KeyboardMapper.ShouldHandleAsKeyCode(VirtualKey.Shift, shiftMeta));
    }

    [Theory]
    [InlineData("Hello, 123!", true)]
    [InlineData(" ", true)]
    [InlineData("你好", false)]
    [InlineData("😀", false)]
    [InlineData("", false)]
    [InlineData("\n", false)]
    public void IsAsciiPrintable_DetectsAsciiVsUnicode(string text, bool expected)
    {
        Assert.Equal(expected, KeyboardMapper.IsAsciiPrintable(text));
    }
}
