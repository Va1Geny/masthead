using System.Windows;

namespace Isle.Core;

/// <summary>Priorities of live activities in the collapsed pill (highest first).</summary>
public static class PillPriority
{
    public const int Idle = 0;
    public const int AgentWorking = 20; // agent session working (below music; shares the pill as a dot)
    public const int Music = 30;
    public const int Download = 40;
    public const int TimerRunning = 50;
    public const int MicCamera = 60;
    public const int AgentDone = 75;    // short "agent finished" check
    public const int AgentWaiting = 80;
    public const int Transient = 85;   // brief indicators and banners (layout, Caps Lock, VPN, track title, notifications)
    public const int Finished = 90;    // finished timer / reminder
}

/// <summary>One thing a module wants to show in the collapsed pill.</summary>
public sealed class PillItem
{
    public required string Id { get; init; }
    public int Priority { get; init; }

    /// <summary>Full view, used when this item owns the pill. Created lazily, cached while the item is active.</summary>
    public required Func<FrameworkElement> View { get; init; }

    /// <summary>Optional small view for the right-hand slot when another item owns the pill.</summary>
    public Func<FrameworkElement>? Compact { get; init; }

    /// <summary>Pill size (DIPs at 100%) when this item owns it.</summary>
    public double Width { get; init; } = 120;
    public double Height { get; init; } = 32;
    public double CompactWidth { get; init; } = 36;

    /// <summary>Banners (taller than the plain pill, or flagged) never share the pill.</summary>
    public bool IsBanner { get; init; }

    /// <summary>False for banners with their own buttons: hovering must not expand the island (click still does).</summary>
    public bool HoverExpands { get; init; } = true;

    /// <summary>Slide out from the screen edge to show this item even in auto-hide mode.</summary>
    public bool Peek { get; init; }

    /// <summary>Clicking the pill while this item owns it. Null = default (expand).</summary>
    public Action? Click { get; init; }

    /// <summary>Tab to open when the island expands while this item owns the pill.</summary>
    public string? TabId { get; init; }

    internal long Sequence;
    internal FrameworkElement? ViewCache, CompactCache;
}

/// <summary>
/// The pill's priority queue. Modules <see cref="Set"/> and <see cref="Remove"/> items; the island listens to
/// <see cref="Changed"/> and shows <see cref="Primary"/> (+ <see cref="Secondary"/> in the side slot).
/// </summary>
public sealed class PillQueue
{
    readonly Dictionary<string, PillItem> _items = [];
    long _sequence;

    public event Action? Changed;

    public PillItem? Primary { get; private set; }
    public PillItem? Secondary { get; private set; }
    public int Count => _items.Count;

    public void Set(PillItem item)
    {
        if (_items.TryGetValue(item.Id, out var old) && ReferenceEquals(old, item)) return;
        item.Sequence = ++_sequence;
        _items[item.Id] = item;
        Recompute();
    }

    public bool Remove(string id)
    {
        if (!_items.Remove(id)) return false;
        Recompute();
        return true;
    }

    public bool Contains(string id) => _items.ContainsKey(id);

    void Recompute()
    {
        var (p, s) = Select(_items.Values);
        if (ReferenceEquals(p, Primary) && ReferenceEquals(s, Secondary)) return;
        Primary = p;
        Secondary = s;
        Changed?.Invoke();
    }

    /// <summary>Pure selection rule: highest priority wins (newest on ties); the runner-up shares the pill
    /// when the winner is not a banner and the runner-up has a compact view.</summary>
    public static (PillItem? Primary, PillItem? Secondary) Select(IEnumerable<PillItem> items)
    {
        PillItem? first = null;
        foreach (var i in items)
            if (first is null || Better(i, first)) first = i;
        if (first is null) return (null, null);
        if (first.IsBanner) return (first, null);

        PillItem? second = null;
        foreach (var i in items)
        {
            if (ReferenceEquals(i, first) || i.Compact is null || i.Priority <= PillPriority.Idle) continue;
            if (second is null || Better(i, second)) second = i;
        }
        return (first, second);
    }

    static bool Better(PillItem a, PillItem b) =>
        a.Priority != b.Priority ? a.Priority > b.Priority : a.Sequence > b.Sequence;
}
