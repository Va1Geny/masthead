using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Isle.Core;
using Isle.Modules.Hotkeys;
using Isle.Views.SettingsPanel;

namespace Isle.Tests;

// real keyboard focus: must not run beside other WPF windows
[Collection(nameof(GlobalSettingsTests))]
public class KeyboardNavigationTests
{
    [Fact]
    public void Theme_changes_keep_keyboard_focus_and_tab_inside_settings()
    {
        Sta.Run(_ =>
        {
            var settings = Settings.Current;
            var originalTheme = settings.Theme;
            try
            {
                settings.Theme = "Dark";
                var view = new SettingsView(new SettingsActions(() => [], (_, _) => true, _ => { },
                    _ => { }, _ => { }, () => { }));
                KeyboardNavigation.SetTabNavigation(view, KeyboardNavigationMode.Cycle);
                var host = new Grid { Children = { new Button { Content = "Music" }, view } };
                var parameters = new HwndSourceParameters("IsleThemeKeyboard") { Width = 640, Height = 350 };
                parameters.SetPosition(-32000, -32000);
                using var source = new HwndSource(parameters) { RootVisual = host };
                host.UpdateLayout();
                var radios = Descendants(view).OfType<RadioButton>().Take(3).ToArray();
                foreach (var (theme, radio) in new[] { ("Light", radios[1]), ("System", radios[2]), ("Dark", radios[0]) })
                {
                    Assert.True(FocusHelp.Take(source, radio));
                    // select the way a keyboard user's screen reader does; a synthetic Space press needs mouse
                    // capture, which Windows only grants to the foreground window, so it fails while someone works
                    ((System.Windows.Automation.Provider.ISelectionItemProvider)new System.Windows.Automation.Peers.RadioButtonAutomationPeer(radio)).Select();
                    Assert.Equal(theme, settings.Theme);
                    Assert.True(radio.IsChecked);
                    Assert.Same(radio, Keyboard.FocusedElement);
                    Assert.True(radio.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                    Assert.True(view.IsKeyboardFocusWithin);
                }
            }
            finally { settings.Theme = originalTheme; settings.Save("Theme"); }
        });
    }

    [Fact]
    public void Settings_sections_use_native_buttons_and_hotkey_capture_allows_tab()
    {
        Sta.Run(_ =>
        {
            var view = new SettingsView(new SettingsActions(
                () => [new HotkeyInfo("toggle", "hotkey.toggle", "F5", "F5", true)],
                (_, _) => true, _ => { },
                _ => { }, _ => { }, () => { }));
            var nav = ((DockPanel)view.Children[0]).Children.OfType<StackPanel>().Single();
            Assert.Equal(SettingsView.Sections, nav.Children.OfType<Button>().Select(b => (string)b.Tag));
            foreach (var section in SettingsView.Sections)
            {
                var button = nav.Children.OfType<Button>().Single(b => Equals(b.Tag, section));
                Assert.True(button.Focusable);
                Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(button)));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(section, view.Section);
            }
            view.ShowSection("general");
            var right = (Grid)view.Children[1];
            var page = (StackPanel)((ScrollViewer)right.Children[1]).Content;
            var box = Descendants(page).OfType<TextBox>().Single(t => t.IsReadOnly);
            using var source = new HwndSource(new HwndSourceParameters("IsleKeyboardTest") { Width = 1, Height = 1 });
            source.RootVisual = view;
            var tab = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Tab) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            box.RaiseEvent(tab);
            Assert.False(tab.Handled);
        });
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
