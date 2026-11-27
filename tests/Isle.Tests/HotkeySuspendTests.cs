using Isle.Modules.Hotkeys;

namespace Isle.Tests;

public class HotkeySuspendTests
{
    // Send WM_HOTKEY ids directly. Empty gestures prevent native registration on the dummy handle.
    const int FirstId = 0x4000;

    [Fact]
    public void Suspended_hotkeys_ignore_queued_presses()
    {
        var hotkeys = new HotkeyService();
        var presses = 0;
        hotkeys.Register("shortcut", "", "", () => presses++);
        hotkeys.Attach(new IntPtr(1));

        Assert.True(hotkeys.HandleHotkey(FirstId));
        hotkeys.Suspend(true);
        Assert.False(hotkeys.HandleHotkey(FirstId));
        hotkeys.Suspend(false);
        Assert.True(hotkeys.HandleHotkey(FirstId));
        Assert.Equal(2, presses);
    }
}
