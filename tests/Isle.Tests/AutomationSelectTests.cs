using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Isle.Core;
using Isle.Views;
using Isle.Views.SettingsPanel;

namespace Isle.Tests;

/// <summary>Screen readers select tabs and options through UI Automation, which checks a radio button without a Click.</summary>
[Collection(nameof(GlobalSettingsTests))]
public class AutomationSelectTests
{
    static void Select(RadioButton r) => ((ISelectionItemProvider)new RadioButtonAutomationPeer(r)).Select();

    static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    [Fact]
    public void Selecting_a_tab_through_automation_switches_to_it()
    {
        Sta.Run(_ =>
        {
            var bar = new TabBar { AvailableWidth = 600 };
            bar.SetTabs(new[] { "music", "timers", "notes" }.Select((id, order) => new TabInfo(id, "tab." + id, "Icon.More", () => new Grid(), order)), "music");
            bar.Measure(new System.Windows.Size(600, 40));
            bar.Arrange(new System.Windows.Rect(0, 0, 600, 40));
            var picked = new List<string>();
            bar.TabSelected += picked.Add;
            var timers = Descendants(bar).OfType<RadioButton>().Single(r => r.ToolTip as string == Strings.T("tab.timers") || ((TextBlock)r.Tag).Text == Strings.T("tab.timers"));
            Select(timers);
            Assert.Equal(["timers"], picked);
            bar.Select("notes");             // a programmatic selection does not echo back as a user choice
            Assert.Equal(["timers"], picked);
        });
    }

    [Fact]
    public void Selecting_a_settings_choice_through_automation_applies_it()
    {
        Sta.Run(_ =>
        {
            var settings = Settings.Current;
            var original = settings.Theme;
            try
            {
                settings.Theme = "Dark";
                var view = new SettingsView(new SettingsActions(() => [], (_, _) => true, _ => { },
                    _ => { }, _ => { }, () => { }));
                view.Measure(new System.Windows.Size(640, 350));
                view.Arrange(new System.Windows.Rect(0, 0, 640, 350));
                var light = Descendants(view).OfType<RadioButton>().First(r => r.Content as string == Strings.T("set.theme.Light"));
                Select(light);
                Assert.Equal("Light", settings.Theme);
            }
            finally { settings.Theme = original; settings.Save("Theme"); }
        });
    }
}
