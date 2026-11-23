using System.ComponentModel;

namespace Isle.Core;

/// <summary>
/// All UI text: <c>Strings.T("key")</c>; views re-render on language switch.
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
