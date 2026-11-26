namespace Com.AndroidSync.App.Protocol;

[Flags]
public enum AndroidMetaState
{
    None = 0,
    ShiftOn = 0x00000001,
    AltOn = 0x00000002,
    AltLeftOn = 0x00000010,
    AltRightOn = 0x00000020,
    ShiftLeftOn = 0x00000040,
    ShiftRightOn = 0x00000080,
    CtrlOn = 0x00001000,
    CtrlLeftOn = 0x00002000,
    CtrlRightOn = 0x00004000,
    MetaOn = 0x00010000,
    MetaLeftOn = 0x00020000,
    MetaRightOn = 0x00040000,
    CapsLockOn = 0x00100000
}
