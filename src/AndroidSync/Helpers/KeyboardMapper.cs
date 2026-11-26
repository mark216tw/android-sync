using Com.AndroidSync.App.Protocol;
using Windows.System;

namespace Com.AndroidSync.App.Helpers;

public static class KeyboardMapper
{
    public static AndroidMetaState BuildMetaState(
        bool shift,
        bool ctrl,
        bool alt,
        bool meta = false,
        bool capsLock = false)
    {
        var state = AndroidMetaState.None;
        if (shift)
        {
            state |= AndroidMetaState.ShiftOn | AndroidMetaState.ShiftLeftOn;
        }

        if (ctrl)
        {
            state |= AndroidMetaState.CtrlOn | AndroidMetaState.CtrlLeftOn;
        }

        if (alt)
        {
            state |= AndroidMetaState.AltOn | AndroidMetaState.AltLeftOn;
        }

        if (meta)
        {
            state |= AndroidMetaState.MetaOn | AndroidMetaState.MetaLeftOn;
        }

        if (capsLock)
        {
            state |= AndroidMetaState.CapsLockOn;
        }

        return state;
    }

    public static bool ShouldHandleAsKeyCode(VirtualKey key, AndroidMetaState metaState)
    {
        if (IsModifierKey(key))
        {
            return false;
        }

        if ((metaState & (AndroidMetaState.CtrlOn | AndroidMetaState.AltOn | AndroidMetaState.MetaOn)) != 0)
        {
            return TryMapVirtualKey(key, out _);
        }

        return key switch
        {
            VirtualKey.Enter
            or VirtualKey.Back
            or VirtualKey.Tab
            or VirtualKey.Escape
            or VirtualKey.Delete
            or VirtualKey.Insert
            or VirtualKey.Home
            or VirtualKey.End
            or VirtualKey.PageUp
            or VirtualKey.PageDown
            or VirtualKey.Left
            or VirtualKey.Right
            or VirtualKey.Up
            or VirtualKey.Down
            or >= VirtualKey.F1 and <= VirtualKey.F12 => true,
            _ => false
        };
    }

    public static bool IsModifierKey(VirtualKey key)
    {
        return key is VirtualKey.Shift
            or VirtualKey.LeftShift
            or VirtualKey.RightShift
            or VirtualKey.Control
            or VirtualKey.LeftControl
            or VirtualKey.RightControl
            or VirtualKey.Menu
            or VirtualKey.LeftMenu
            or VirtualKey.RightMenu
            or VirtualKey.LeftWindows
            or VirtualKey.RightWindows
            or VirtualKey.CapitalLock;
    }

    public static bool TryMapVirtualKey(VirtualKey key, out AndroidKeyCode androidKeyCode)
    {
        if (key is >= VirtualKey.A and <= VirtualKey.Z)
        {
            androidKeyCode = AndroidKeyCode.A + (key - VirtualKey.A);
            return true;
        }

        if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9)
        {
            androidKeyCode = AndroidKeyCode.Num0 + (key - VirtualKey.Number0);
            return true;
        }

        if (key is >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9)
        {
            androidKeyCode = AndroidKeyCode.Numpad0 + (key - VirtualKey.NumberPad0);
            return true;
        }

        if (key is >= VirtualKey.F1 and <= VirtualKey.F12)
        {
            androidKeyCode = AndroidKeyCode.F1 + (key - VirtualKey.F1);
            return true;
        }

        androidKeyCode = (int)key switch
        {
            (int)VirtualKey.Enter => AndroidKeyCode.Enter,
            (int)VirtualKey.Back => AndroidKeyCode.Del,
            (int)VirtualKey.Tab => AndroidKeyCode.Tab,
            (int)VirtualKey.Space => AndroidKeyCode.Space,
            (int)VirtualKey.Escape => AndroidKeyCode.Escape,
            (int)VirtualKey.Delete => AndroidKeyCode.ForwardDel,
            (int)VirtualKey.Insert => AndroidKeyCode.Insert,
            (int)VirtualKey.Home => AndroidKeyCode.MoveHome,
            (int)VirtualKey.End => AndroidKeyCode.MoveEnd,
            (int)VirtualKey.PageUp => AndroidKeyCode.PageUp,
            (int)VirtualKey.PageDown => AndroidKeyCode.PageDown,
            (int)VirtualKey.Left => AndroidKeyCode.DpadLeft,
            (int)VirtualKey.Right => AndroidKeyCode.DpadRight,
            (int)VirtualKey.Up => AndroidKeyCode.DpadUp,
            (int)VirtualKey.Down => AndroidKeyCode.DpadDown,
            (int)VirtualKey.Add => AndroidKeyCode.NumpadAdd,
            (int)VirtualKey.Subtract => AndroidKeyCode.NumpadSubtract,
            (int)VirtualKey.Multiply => AndroidKeyCode.NumpadMultiply,
            (int)VirtualKey.Divide => AndroidKeyCode.NumpadDivide,
            (int)VirtualKey.Decimal => AndroidKeyCode.NumpadDot,
            186 => AndroidKeyCode.Semicolon,    // VK_OEM_1 (;:)
            187 => AndroidKeyCode.Equals,       // VK_OEM_PLUS (=+)
            188 => AndroidKeyCode.Comma,        // VK_OEM_COMMA (,<)
            189 => AndroidKeyCode.Minus,        // VK_OEM_MINUS (-_)
            190 => AndroidKeyCode.Period,       // VK_OEM_PERIOD (.>)
            191 => AndroidKeyCode.Slash,        // VK_OEM_2 (/?)
            192 => AndroidKeyCode.Grave,        // VK_OEM_3 (`~)
            219 => AndroidKeyCode.LeftBracket,  // VK_OEM_4 ([{)
            220 => AndroidKeyCode.Backslash,    // VK_OEM_5 (\|)
            221 => AndroidKeyCode.RightBracket, // VK_OEM_6 (]})
            222 => AndroidKeyCode.Apostrophe,   // VK_OEM_7 ('")
            _ => default
        };

        return androidKeyCode != default;
    }

    public static bool IsAsciiPrintable(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (ch < 0x20 || ch > 0x7E)
            {
                return false;
            }
        }

        return true;
    }
}
