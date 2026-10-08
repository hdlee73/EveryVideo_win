using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using EveryVideo.Services;

namespace EveryVideo;

public enum SortKind { NameAsc, NameDesc, DateNewest, DateOldest, SizeLargest, SizeSmallest }

public static class ListHelpers
{
    private const string Format = "EveryVideo.Reorder";

    /// <summary>목록 항목을 마우스로 끌어 순서를 바꿀 수 있게 한다.</summary>
    public static void EnableDragReorder<T>(ListBox list, ObservableCollection<T> items, Action? moved = null) where T : class
    {
        Point? start = null;
        T? dragged = null;
        list.AllowDrop = true;

        list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            start = null;
            if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) != null) return;
            if (ItemAt(list, e.OriginalSource) is T item)
            {
                start = e.GetPosition(list);
                dragged = item;
            }
        };
        list.PreviewMouseMove += (_, e) =>
        {
            if (start is not Point p || dragged == null || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(list);
            if (Math.Abs(now.Y - p.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
            start = null;
            DragDrop.DoDragDrop(list, new DataObject(Format, dragged), DragDropEffects.Move);
        };
        list.DragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent(Format)) return;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        };
        list.Drop += (_, e) =>
        {
            if (e.Data.GetData(Format) is not T item) return;
            e.Handled = true;
            var from = items.IndexOf(item);
            if (from < 0) return;
            var to = items.Count - 1;
            if (ItemAt(list, e.OriginalSource) is T target)
            {
                to = items.IndexOf(target);
            }
            to = Math.Clamp(to, 0, items.Count - 1);
            if (to != from) items.Move(from, to);
            list.SelectedItem = item;
            moved?.Invoke();
        };
    }

    public static object? ItemAt(ListBox list, object? source)
    {
        var container = FindAncestor<ListBoxItem>(source as DependencyObject);
        return container == null ? null : list.ItemContainerGenerator.ItemFromContainer(container);
    }

    public static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    public static IEnumerable<T> Sort<T>(IEnumerable<T> items, Func<T, string> location, SortKind kind)
    {
        var list = items.Select(i => (item: i, loc: location(i), info: Info(location(i)))).ToList();
        IEnumerable<(T item, string loc, FileInfo? info)> sorted = kind switch
        {
            SortKind.NameAsc => list.OrderBy(x => RemoteSources.DisplayName(x.loc), new NaturalComparer()),
            SortKind.NameDesc => list.OrderByDescending(x => RemoteSources.DisplayName(x.loc), new NaturalComparer()),
            SortKind.DateNewest => list.OrderByDescending(x => x.info?.LastWriteTime ?? DateTime.MinValue),
            SortKind.DateOldest => list.OrderBy(x => x.info?.LastWriteTime ?? DateTime.MaxValue),
            SortKind.SizeLargest => list.OrderByDescending(x => x.info?.Length ?? -1),
            _ => list.OrderBy(x => x.info?.Length ?? long.MaxValue),
        };
        return sorted.Select(x => x.item).ToList();
    }

    private static FileInfo? Info(string path)
    {
        try { return !RemoteSources.IsUrl(path) && File.Exists(path) ? new FileInfo(path) : null; }
        catch { return null; }
    }

    /// <summary>정렬 메뉴를 띄운다.</summary>
    public static void ShowSortMenu(FrameworkElement target, Action<SortKind> apply)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        foreach (var (label, kind) in new[]
                 {
                     ("이름순 (가나다)", SortKind.NameAsc), ("이름 역순", SortKind.NameDesc),
                     ("수정한 날짜 (최신 먼저)", SortKind.DateNewest), ("수정한 날짜 (오래된 먼저)", SortKind.DateOldest),
                     ("크기 (큰 것 먼저)", SortKind.SizeLargest), ("크기 (작은 것 먼저)", SortKind.SizeSmallest),
                 })
        {
            var mi = new MenuItem { Header = label };
            mi.Click += (_, _) => apply(kind);
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    /// <summary>"영상2" 가 "영상10" 보다 앞에 오도록 숫자를 숫자로 비교한다.</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public int Compare(string? a, string? b)
        {
            a ??= "";
            b ??= "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    var si = i; while (i < a.Length && char.IsDigit(a[i])) i++;
                    var sj = j; while (j < b.Length && char.IsDigit(b[j])) j++;
                    var na = a[si..i].TrimStart('0');
                    var nb = b[sj..j].TrimStart('0');
                    var c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    var c = string.Compare(a[i].ToString(), b[j].ToString(), StringComparison.CurrentCultureIgnoreCase);
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}
