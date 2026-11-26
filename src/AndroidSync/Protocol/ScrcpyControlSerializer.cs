using System.Buffers.Binary;
using System.Text;

namespace Com.AndroidSync.App.Protocol;

public static class ScrcpyControlSerializer
{
    private const byte TypeInjectKeyCode = 0;
    private const byte TypeInjectText = 1;
    private const byte TypeInjectTouchEvent = 2;
    private const byte TypeInjectScrollEvent = 3;
    private const byte TypeGetClipboard = 8;
    private const byte TypeSetClipboard = 9;
    private const byte TypeSetDisplayPower = 10;
    private const byte TypeRotateDevice = 11;

    public const int MaxInjectTextBytes = 300;
    public const int MaxClipboardTextBytes = (256 * 1024) - 14;

    private const ulong MousePointerId = unchecked((ulong)-1L);
    private const uint PrimaryMouseButton = 1;

    public static byte[] CreateKeyCodeMessage(
        AndroidKeyCode keyCode,
        bool isDown,
        int repeat = 0,
        AndroidMetaState metaState = AndroidMetaState.None)
    {
        var buffer = new byte[14];
        buffer[0] = TypeInjectKeyCode;
        buffer[1] = isDown ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(2, 4), (int)keyCode);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(6, 4), repeat);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(10, 4), (int)metaState);
        return buffer;
    }

    public static byte[] CreateInjectTextMessage(string text)
    {
        var utf8 = EncodeUtf8Truncated(text, MaxInjectTextBytes);
        var buffer = new byte[5 + utf8.Length];
        buffer[0] = TypeInjectText;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1, 4), utf8.Length);
        utf8.CopyTo(buffer.AsSpan(5));
        return buffer;
    }

    public static byte[] CreateGetClipboardMessage(ScrcpyClipboardCopyKey copyKey = ScrcpyClipboardCopyKey.None)
    {
        return [TypeGetClipboard, (byte)copyKey];
    }

    public static byte[] CreateSetClipboardMessage(string text, bool paste = false, ulong sequence = 0)
    {
        var utf8 = EncodeUtf8Truncated(text, MaxClipboardTextBytes);
        var buffer = new byte[14 + utf8.Length];
        buffer[0] = TypeSetClipboard;
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(1, 8), sequence);
        buffer[9] = paste ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(10, 4), utf8.Length);
        utf8.CopyTo(buffer.AsSpan(14));
        return buffer;
    }

    public static byte[] CreateTouchEventMessage(
        AndroidMotionAction action,
        int x,
        int y,
        ushort screenWidth,
        ushort screenHeight,
        bool isPrimaryPressed,
        ulong pointerId = MousePointerId)
    {
        var buffer = new byte[32];
        buffer[0] = TypeInjectTouchEvent;
        buffer[1] = (byte)action;
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(2, 8), pointerId);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(10, 4), x);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(14, 4), y);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(18, 2), screenWidth);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(20, 2), screenHeight);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(22, 2), action == AndroidMotionAction.Up ? (ushort)0 : ushort.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(24, 4), PrimaryMouseButton);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(28, 4), isPrimaryPressed ? PrimaryMouseButton : 0u);
        return buffer;
    }

    public static byte[] CreateScrollEventMessage(
        int x,
        int y,
        ushort screenWidth,
        ushort screenHeight,
        float horizontalScroll,
        float verticalScroll)
    {
        var buffer = new byte[21];
        buffer[0] = TypeInjectScrollEvent;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1, 4), x);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(5, 4), y);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(9, 2), screenWidth);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(11, 2), screenHeight);
        BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(13, 2), FloatToFixedPoint16(horizontalScroll));
        BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(15, 2), FloatToFixedPoint16(verticalScroll));
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(17, 4), 0u);
        return buffer;
    }

    public static byte[] CreateSetScreenPowerModeMessage(bool screenOn)
    {
        return [TypeSetDisplayPower, screenOn ? (byte)1 : (byte)0];
    }

    public static byte[] CreateRotateDeviceMessage()
    {
        return [TypeRotateDevice];
    }

    private static byte[] EncodeUtf8Truncated(string text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
        {
            return bytes;
        }

        // Back off from any UTF-8 continuation bytes (10xxxxxx) so we never split a multi-byte character.
        var len = maxBytes;
        while (len > 0 && (bytes[len] & 0xC0) == 0x80)
        {
            len--;
        }

        return bytes[..len];
    }

    private static short FloatToFixedPoint16(float value)
    {
        var clamped = Math.Clamp(value, -1.0f, 1.0f);
        var scaled = (int)MathF.Round(clamped * 32767.0f);
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }
}
