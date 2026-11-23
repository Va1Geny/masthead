using Isle.Modules.Hotkeys;

namespace Isle.Tests;

public class HotkeyHoldTests
{
    // Send WM_HOTKEY ids directly. Empty gestures prevent native registration on the dummy handle.
    const int HoldId = 0x4000;

    [Fact]
    public void Shortcut_recorder_finishes_active_hold_once_and_ignores_queued_presses()
    {
        Sta.Run(sink =>
        {
            var hotkeys = new HotkeyService();
            int presses = 0, releases = 0, shortcuts = 0;
            hotkeys.Register("hold", "", "", () => presses++, () => releases++);
            hotkeys.Register("shortcut", "", "", () => shortcuts++);
            hotkeys.Attach(new IntPtr(1));

            Assert.True(hotkeys.HandleHotkey(HoldId));
            hotkeys.Suspend(false); // Settings can close without ever focusing the shortcut recorder.
            Assert.Equal(0, releases);
            Assert.True(hotkeys.HandleHotkey(HoldId + 1));
            hotkeys.Suspend(true);
            Assert.Equal(1, releases);
            Assert.False(hotkeys.HandleHotkey(HoldId));
            Assert.False(hotkeys.HandleHotkey(HoldId + 1));
            hotkeys.Suspend(true);
            hotkeys.Suspend(false);
            Assert.Equal((1, 1, 1), (presses, releases, shortcuts));

            Assert.True(hotkeys.HandleHotkey(HoldId + 1));
            Assert.True(hotkeys.HandleHotkey(HoldId));
            // The empty gesture has virtual key zero, so its next poll sees key-up.
            Assert.True(sink.PumpUntil(() => releases == 2));
            hotkeys.Suspend(true);
            Assert.Equal((2, 2, 2), (presses, releases, shortcuts));
        });
    }

    [Fact]
    public void Teardown_discards_the_pending_release()
    {
        Sta.Run(_ =>
        {
            var hotkeys = new HotkeyService();
            int releases = 0;
            hotkeys.Register("hold", "", "", () => { }, () => releases++);
            hotkeys.Attach(new IntPtr(1));
            Assert.True(hotkeys.HandleHotkey(HoldId));
            hotkeys.Detach();
            hotkeys.Suspend(true);
            Assert.Equal(0, releases);
        });
    }
}
