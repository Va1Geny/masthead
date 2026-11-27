using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Isle.Core;
using Isle.Modules.Hotkeys;
using Isle.Views.SettingsPanel;

namespace Isle.Tests;

[CollectionDefinition(nameof(GlobalSettingsTests), DisableParallelization = true)]
public sealed class GlobalSettingsTests { }

[Collection(nameof(GlobalSettingsTests))]
public class SettingsPanelTests
{
    [Theory]
    [InlineData(30, 30)]
    [InlineData(0, 18)]
    public void Scale_slider_accepts_drags_at_both_edges_of_its_hit_area(double minimum, double height)
    {
        Sta.Run(_ =>
        {
            var view = new SettingsView(new SettingsActions(() => [], (_, _) => true, _ => { },
                _ => { }, _ => { }, () => { }));
            view.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Isle;component/Styles/Theme.xaml", UriKind.Relative) });
            view.Resources["CompactTargetMin"] = minimum;
            var slider = Descendants(view).OfType<Slider>().Single();
            slider.Value = 1;
            slider.Measure(new Size(slider.Width, double.PositiveInfinity));
            slider.Arrange(new Rect(slider.DesiredSize));
            var track = (Track)slider.Template.FindName("PART_Track", slider);
            Assert.Equal(height, slider.ActualHeight);
            var x = track.Thumb.TranslatePoint(new Point(track.Thumb.ActualWidth / 2, 0), slider).X;
            foreach (var y in new[] { 1d, height - 1 })
            {
                var hit = VisualTreeHelper.HitTest(slider, new Point(x, y))?.VisualHit;
                Assert.True(hit is not null && track.Thumb.IsAncestorOf(hit));
            }
            track.Thumb.RaiseEvent(new DragDeltaEventArgs(20, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            Assert.True(slider.Value > 1);
            var dragged = slider.Value;
            Slider.DecreaseSmall.Execute(null, slider);
            Assert.True(slider.Value < dragged);
        });
    }

    [Theory]
    [InlineData(ModifierKeys.Alt, false, 0x4B, "Alt+K")]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift, true, 0x46, "Ctrl+Win+Shift+F")]
    [InlineData(ModifierKeys.None, true, 0x20, "Win+Space")]
    [InlineData(ModifierKeys.None, false, 0x74, "F5")]
    [InlineData(ModifierKeys.Shift, false, 0x77, "Shift+F8")]
    [InlineData(ModifierKeys.Alt, false, 0xBC, "Alt+,")]
    public void Key_press_becomes_gesture_text(ModifierKeys mods, bool win, uint vk, string expected)
    {
        Assert.Equal(expected, SettingsView.HotkeyText(mods, win, vk));
        Assert.True(Gesture.TryParse(expected, out var g));
        Assert.Equal(vk, g.Key);
    }

    [Theory]
    [InlineData(ModifierKeys.None, false, 0x4B)]   // bare letter would swallow typing
    [InlineData(ModifierKeys.Shift, false, 0x4B)]  // shifted letter too
    [InlineData(ModifierKeys.Alt, false, 0xFF)]    // unsupported key
    public void Unusable_key_presses_are_rejected(ModifierKeys mods, bool win, uint vk) =>
        Assert.Null(SettingsView.HotkeyText(mods, win, vk));

    [Fact]
    public void Inputs_have_row_labels_close_relocalizes_and_scale_commits_when_hidden()
    {
        Sta.Run(_ =>
        {
            var savedScale = Settings.Current.Scale;
            try
            {
                var view = new SettingsView(new SettingsActions(() => [], (_, _) => true, _ => { },
                    _ => { }, _ => { }, () => { }));
                foreach (var section in new[] { "general", "options" })
                {
                    view.ShowSection(section);
                    var right = (System.Windows.Controls.Grid)view.Children[1];
                    var page = (System.Windows.Controls.StackPanel)((System.Windows.Controls.ScrollViewer)right.Children[1]).Content;
                    foreach (var input in Descendants(page).Where(d => d is System.Windows.Controls.Primitives.ToggleButton or System.Windows.Controls.TextBox or System.Windows.Controls.PasswordBox or System.Windows.Controls.Slider))
                        Assert.NotNull(System.Windows.Automation.AutomationProperties.GetLabeledBy(input));
                }
                view.ShowSection("general");
                var choices = Descendants(view).OfType<System.Windows.Controls.RadioButton>().ToList();
                Assert.All(choices, choice => Assert.EndsWith(": " + choice.Content, System.Windows.Automation.AutomationProperties.GetName(choice)));
                var slider = Descendants(view).OfType<System.Windows.Controls.Slider>().Single();
                slider.Value = savedScale == 1.35 ? 1.4 : 1.35;
                view.OnHidden();
                Assert.Equal(slider.Value, Settings.Current.Scale, 2);
                Strings.Language = "uk";
                view.Render();
                var close = Descendants(view).OfType<System.Windows.Controls.Button>().Single(b => b.ToolTip?.ToString() == Strings.T("set.close"));
                Assert.Equal(Strings.T("set.close"), System.Windows.Automation.AutomationProperties.GetName(close));
                view.ShowSection("options");
                var jumps = Descendants(view).OfType<System.Windows.Controls.ComboBox>().Single();
                Assert.Equal(9, jumps.Items.Count); // placeholder + agents + 7 Dev groups
                jumps.SelectedIndex = jumps.Items.Count - 1;
            }
            finally { Strings.Language = "en"; Settings.Current.Scale = savedScale; Settings.Current.Save("Scale"); }
        });
    }

    static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    [Fact]
    public void Failed_hotkey_rebind_preserves_previous_override()
    {
        var id = "test.rollback." + Guid.NewGuid().ToString("N");
        var other = id + ".other";
        try
        {
            Settings.Current.Hotkeys[id] = "Ctrl+F23";
            var service = new HotkeyService();
            service.Register(id, "", "Ctrl+F22", () => { });
            service.Register(other, "", "Ctrl+F24", () => { });
            Assert.False(service.Rebind(id, "Ctrl+F24"));
            Assert.Equal("Ctrl+F23", Settings.Current.Hotkeys[id]);
            Assert.False(service.Rebind(id, "invalid"));
            Assert.False(service.Rebind(id, "Ctrl+F21")); // no attached HWND: registration cannot succeed
            Assert.Equal("Ctrl+F23", Settings.Current.Hotkeys[id]);
            Assert.Equal("Ctrl+F23", service.List().Single(h => h.Id == id).Gesture);
        }
        finally { Settings.Current.Hotkeys.Remove(id); }
    }

    [Fact]
    public void Every_section_has_labels_in_both_languages()
    {
        var keys = SettingsView.Sections.Select(s => "set." + s);
        foreach (var k in keys)
        {
            Assert.True(Strings.English.ContainsKey(k), k);
            Assert.True(Strings.Ukrainian.ContainsKey(k), k);
        }
    }

    [Fact]
    public void Every_section_builds_in_both_languages()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var rebinds = new List<(string, string?)>();
                var v = new SettingsView(new SettingsActions(
                    Hotkeys: () => [new HotkeyInfo("toggle", "hotkey.toggle", "Win+Alt+Space", "Win+Alt+Space", true), new HotkeyInfo("other", "hotkey.toggle", "", "Win+Alt+V", false)],
                    Rebind: (id, g) => { rebinds.Add((id, g)); return true; },
                    SuspendHotkeys: _ => { },
                    RestartModule: _ => { }, Open: _ => { }, Close: () => { }));
                foreach (var lang in new[] { "en", "uk" })
                {
                    Strings.Language = lang;
                    foreach (var s in SettingsView.Sections)
                    {
                        v.ShowSection(s);
                        Assert.Equal(s, v.Section);
                    }
                }
                v.ShowSection("nonsense");
                Assert.Equal("general", v.Section);
            }
            catch (Exception ex) { error = ex; }
            finally { Strings.Language = "en"; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Exception("STA test failed", error);
    }
}
