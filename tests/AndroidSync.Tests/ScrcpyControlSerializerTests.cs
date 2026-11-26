using System.Buffers.Binary;
using System.Text;
using Com.AndroidSync.App.Protocol;
using Xunit;

namespace Com.AndroidSync.App.Tests;

public sealed class ScrcpyControlSerializerTests
{
    [Fact]
    public void CreateKeyCodeMessage_EncodesBigEndianHeader()
    {
        var msg = ScrcpyControlSerializer.CreateKeyCodeMessage(AndroidKeyCode.Back, isDown: true);

        Assert.Equal(14, msg.Length);
        Assert.Equal(0, msg[0]);
        Assert.Equal(0, msg[1]);
        Assert.Equal((int)AndroidKeyCode.Back, BinaryPrimitives.ReadInt32BigEndian(msg.AsSpan(2, 4)));
    }

    [Fact]
    public void CreateTouchEventMessage_EncodesCoordinatesAndScreenSize()
    {
        var msg = ScrcpyControlSerializer.CreateTouchEventMessage(
            AndroidMotionAction.Down,
            x: 320,
            y: 640,
            screenWidth: 1080,
            screenHeight: 2400,
            isPrimaryPressed: true);

        Assert.Equal(32, msg.Length);
        Assert.Equal(2, msg[0]);
        Assert.Equal((byte)AndroidMotionAction.Down, msg[1]);
        Assert.Equal(320, BinaryPrimitives.ReadInt32BigEndian(msg.AsSpan(10, 4)));
        Assert.Equal(640, BinaryPrimitives.ReadInt32BigEndian(msg.AsSpan(14, 4)));
        Assert.Equal(1080, BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(18, 2)));
        Assert.Equal(2400, BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(20, 2)));
        Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(22, 2)));
    }

    [Fact]
    public void CreateScrollEventMessage_EncodesFixedPointScroll()
    {
        var msg = ScrcpyControlSerializer.CreateScrollEventMessage(100, 200, 1080, 1920, 0f, 1.0f);

        Assert.Equal(21, msg.Length);
        Assert.Equal(3, msg[0]);
        Assert.Equal(0, BinaryPrimitives.ReadInt16BigEndian(msg.AsSpan(13, 2)));
        Assert.Equal(32767, BinaryPrimitives.ReadInt16BigEndian(msg.AsSpan(15, 2)));
    }

    [Fact]
    public void CreateScreenPowerAndRotateMessages_HaveExpectedBytes()
    {
        Assert.Equal(new byte[] { 10, 0 }, ScrcpyControlSerializer.CreateSetScreenPowerModeMessage(false));
        Assert.Equal(new byte[] { 10, 1 }, ScrcpyControlSerializer.CreateSetScreenPowerModeMessage(true));
        Assert.Equal(new byte[] { 11 }, ScrcpyControlSerializer.CreateRotateDeviceMessage());
    }

    [Fact]
    public void CreateKeyCodeMessage_EncodesRepeatAndMetaState()
    {
        var meta = AndroidMetaState.CtrlOn | AndroidMetaState.CtrlLeftOn;
        var msg = ScrcpyControlSerializer.CreateKeyCodeMessage(AndroidKeyCode.C, isDown: true, repeat: 2, metaState: meta);

        Assert.Equal(14, msg.Length);
        Assert.Equal(0, msg[0]);
        Assert.Equal(0, msg[1]);
        Assert.Equal((int)AndroidKeyCode.C, BinaryPrimitives.ReadInt32BigEndian(msg.AsSpan(2, 4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(msg.AsSpan(6, 4)));
        Assert.Equal((int)meta, BinaryPrimitives.ReadInt32BigEndian(msg.AsSpan(10, 4)));
    }

    [Fact]
    public void CreateInjectTextMessage_EncodesUtf8AndTruncatesCleanly()
    {
        var msg = ScrcpyControlSerializer.CreateInjectTextMessage("Hello!");
        Assert.Equal(1, msg[0]);
        Assert.Equal(6, BinaryPrimitives.ReadInt32BigEndian(msg.AsSpan(1, 4)));
        Assert.Equal("Hello!", Encoding.UTF8.GetString(msg.AsSpan(5)));

        // 150 Chinese characters = 450 UTF-8 bytes > 300 byte limit; must truncate cleanly on 3-byte char boundary (300 bytes = 100 chars).
        var longText = new string('中', 150);
        var truncated = ScrcpyControlSerializer.CreateInjectTextMessage(longText);
        var payloadLen = BinaryPrimitives.ReadInt32BigEndian(truncated.AsSpan(1, 4));
        Assert.Equal(300, payloadLen);
        Assert.Equal(new string('中', 100), Encoding.UTF8.GetString(truncated.AsSpan(5, payloadLen)));
    }

    [Fact]
    public void CreateClipboardMessages_EncodeWireFormatCorrectly()
    {
        Assert.Equal(new byte[] { 8, 0 }, ScrcpyControlSerializer.CreateGetClipboardMessage());
        Assert.Equal(new byte[] { 8, 1 }, ScrcpyControlSerializer.CreateGetClipboardMessage(ScrcpyClipboardCopyKey.Copy));
        Assert.Equal(new byte[] { 8, 2 }, ScrcpyControlSerializer.CreateGetClipboardMessage(ScrcpyClipboardCopyKey.Cut));

        var setMsg = ScrcpyControlSerializer.CreateSetClipboardMessage("測試剪貼簿", paste: true, sequence: 42UL);
        var expectedUtf8 = Encoding.UTF8.GetBytes("測試剪貼簿");
        Assert.Equal(14 + expectedUtf8.Length, setMsg.Length);
        Assert.Equal(9, setMsg[0]);
        Assert.Equal(42UL, BinaryPrimitives.ReadUInt64BigEndian(setMsg.AsSpan(1, 8)));
        Assert.Equal(1, setMsg[9]);
        Assert.Equal(expectedUtf8.Length, BinaryPrimitives.ReadInt32BigEndian(setMsg.AsSpan(10, 4)));
        Assert.Equal("測試剪貼簿", Encoding.UTF8.GetString(setMsg.AsSpan(14)));
    }
}
