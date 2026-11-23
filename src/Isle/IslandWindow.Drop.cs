using System.Windows;

namespace Isle;

// Files dragged onto the island: it expands to the shelf tab and hands the paths to the shelf module.
public partial class IslandWindow
{
    /// <summary>Set by the shelf module: tab to open on drag-over and what to do with dropped paths. Null = drops refused.</summary>
    public (string TabId, Func<string[], bool> Drop, Action<bool> Hint)? FileDropTarget { get; set; }

    void InitDrop()
    {
        AllowDrop = true;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => FileDropTarget?.Hint(false);
        Drop += OnDrop;
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (FileDropTarget is not { } t || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = ShelfDropEffect(e.AllowedEffects);
        if (e.Effects == DragDropEffects.None) return;
        if (!_expanded) Expand(t.TabId, sticky: true);
        else SelectTab(t.TabId, animate: true);
        t.Hint(true);
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (FileDropTarget is not { } t) return;
        t.Hint(false);
        var effect = ShelfDropEffect(e.AllowedEffects);
        if (effect != DragDropEffects.None &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            Safe(() => { if (t.Drop(files)) e.Effects = effect; }, "shelf drop");
    }

    static DragDropEffects ShelfDropEffect(DragDropEffects allowed) =>
        allowed.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link : allowed & DragDropEffects.Copy;
}
