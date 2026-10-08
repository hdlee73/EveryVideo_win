using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using EveryVideo.Services;

namespace EveryVideo.Dialogs;

public partial class BookmarksWindow : CardWindow
{
    public Bookmark? Chosen { get; private set; }

    public BookmarksWindow()
    {
        InitializeComponent();
        SearchBox.TextChanged += (_, _) => Reload();
        Reload();
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void Reload()
    {
        var q = SearchBox.Text.Trim();
        var items = BookmarkStore.All
            .Where(b => q.Length == 0 || b.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                        b.Note.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(b => b.PositionMs)
            .ToList();
        var view = new ListCollectionView(items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Bookmark.Title)));
        List.ItemsSource = view;
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not Bookmark b) return;
        Chosen = b;
        DialogResult = true;
    }

    private void List_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Open_Click(sender, e);

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not Bookmark b) return;
        var note = CardDialog.Prompt("메모 고치기", $"{b.Title} · {b.Time}", b.Note);
        if (note == null) return;
        b.Note = note.Trim();
        BookmarkStore.Update();
        Reload();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not Bookmark b) return;
        BookmarkStore.Remove(b);
        Reload();
    }
}
