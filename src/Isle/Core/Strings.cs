using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;

namespace Isle.Core;

/// <summary>
/// All UI text. Code: <c>Strings.T("key")</c>. XAML: <c>{core:Loc key}</c> (updates live on language switch).
/// Add every key to both dictionaries (StringsTests enforces parity).
/// </summary>
public sealed partial class Strings : INotifyPropertyChanged
{
    public static Strings Instance { get; } = new();

    static string _language = "en";

    public event PropertyChangedEventHandler? PropertyChanged;
    public static event Action? LanguageChanged;

    public static string Language
    {
        get => _language;
        set
        {
            var lang = value == "uk" ? "uk" : "en";
            if (lang == _language) return;
            _language = lang;
            Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
            LanguageChanged?.Invoke();
        }
    }

    public string this[string key] => T(key);

    public static string T(string key) =>
        (_language == "uk" ? Uk : En).TryGetValue(key, out var s) ? s : En.TryGetValue(key, out s) ? s : key;

    public static string T(string key, params object?[] args) => string.Format(T(key), args);

    internal static IReadOnlyDictionary<string, string> English => En;
    internal static IReadOnlyDictionary<string, string> Ukrainian => Uk;
}

/// <summary>XAML markup extension: <c>Text="{core:Loc tray.quit}"</c>.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Strings.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
