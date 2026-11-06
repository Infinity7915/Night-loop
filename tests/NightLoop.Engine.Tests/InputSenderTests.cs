using NightLoop.Engine.Input;

namespace NightLoop.Engine.Tests;

public class InputSenderTests
{
    /// <summary>Lets go of nothing the first time it's asked, like SendInput blocked by an admin window.</summary>
    private sealed class FlakyInput : IInputSender
    {
        public int Ups;
        public int Fails = 1;
        public void KeyDown(int virtualKey) { }
        public void KeyUp(int virtualKey) => Up();
        public void MouseDown(MouseButton button) { }
        public void MouseUp(MouseButton button) => Up();
        public void MoveTo(int x, int y) { }
        public void MoveBy(int dx, int dy) { }
        public void Scroll(int notches) { }
        public void TypeChar(char c) { }
        public (int X, int Y) CursorPosition => (0, 0);

        private void Up()
        {
            if (Fails-- > 0) throw new InvalidOperationException("blocked");
            Ups++;
        }
    }

    [Fact]
    public void A_release_that_failed_is_tried_again_when_everything_is_let_go()
    {
        var inner = new FlakyInput();
        var input = new TrackingInputSender(inner);
        input.MouseDown(MouseButton.Right);
        Assert.Throws<InvalidOperationException>(() => input.MouseUp(MouseButton.Right));
        Assert.Contains(MouseButton.Right, input.HeldButtons);
        input.ReleaseAll();
        Assert.Equal(1, inner.Ups);
        Assert.Empty(input.HeldButtons);
    }

    [Fact]
    public void Arrow_keys_go_out_as_extended_scan_codes_not_number_pad_keys()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        Assert.Equal(((ushort)0x4B, true), Win32InputSender.ScanCode(0x25));
        Assert.Equal(((ushort)0x48, true), Win32InputSender.ScanCode(0x26));
        Assert.Equal(((ushort)0x4D, true), Win32InputSender.ScanCode(0x27));
        Assert.Equal(((ushort)0x50, true), Win32InputSender.ScanCode(0x28));
        // Number pad 4 shares the scan code with Left but isn't extended.
        Assert.Equal(((ushort)0x4B, false), Win32InputSender.ScanCode(0x64));
        Assert.Equal(((ushort)0x11, false), Win32InputSender.ScanCode(0x57)); // W
    }
}
