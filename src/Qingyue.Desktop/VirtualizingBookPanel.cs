using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace EpubKindleFix;

/// <summary>Four-column shelves using the page's existing scroll viewer. Only nearby rows own visuals.</summary>
public sealed class VirtualizingBookPanel : VirtualizingPanel
{
    public const int Columns = 4;
    public const double RowHeight = 344;
    private ScrollViewer? scroll;
    private double cellWidth;
    public VirtualizingBookPanel()
    {
        Loaded += (_, _) => AttachScroll();
        Unloaded += (_, _) => { if (scroll is not null) scroll.ScrollChanged -= ScrollChanged; scroll = null; };
    }

    private void AttachScroll()
    {
        if (scroll is not null) return;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer viewer) { scroll = viewer; scroll.ScrollChanged += ScrollChanged; break; }
        InvalidateMeasure();
    }
    private void ScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        if (ReferenceEquals(args.OriginalSource, scroll)
            && (args.VerticalChange != 0 || args.ViewportHeightChange != 0 || args.ViewportWidthChange != 0)) InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null) return new();
        var count = owner.Items.Count;
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : Math.Max(ActualWidth, 720);
        cellWidth = width / Columns;
        var rows = (count + Columns - 1) / Columns;
        var first = 0;
        var last = count - 1;
        if (scroll is not null && scroll.ViewportHeight > 0 && rows > 0)
        {
            var origin = TransformToAncestor(scroll).Transform(new Point());
            var startRow = Math.Max(0, (int)Math.Floor(-origin.Y / RowHeight) - 1);
            var endRow = Math.Min(rows - 1, (int)Math.Ceiling((scroll.ViewportHeight - origin.Y) / RowHeight) + 1);
            first = startRow * Columns;
            last = Math.Min(count - 1, (endRow + 1) * Columns - 1);
        }
        // Accessing InternalChildren initializes the items-host generator on its first measure.
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        // Discard offscreen controls; their Unloaded handlers release per-frame subscriptions.
        for (var child = InternalChildren.Count - 1; child >= 0; child--)
        {
            var index = owner.ItemContainerGenerator.IndexFromContainer(InternalChildren[child]);
            if (index >= first && index <= last) continue;
            generator.Remove(new GeneratorPosition(child, 0), 1);
            RemoveInternalChildRange(child, 1);
        }
        if (last >= first)
        {
            var position = generator.GeneratorPositionFromIndex(first);
            var childIndex = position.Offset == 0 ? position.Index : position.Index + 1;
            using (generator.StartAt(position, GeneratorDirection.Forward, true))
                for (var index = first; index <= last; index++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out var created);
                    if (created)
                    {
                        if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(new Size(cellWidth, RowHeight));
                }
        }
        return new Size(width, rows * RowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null) return finalSize;
        cellWidth = finalSize.Width / Columns;
        foreach (UIElement child in InternalChildren)
        {
            var index = owner.ItemContainerGenerator.IndexFromContainer(child);
            if (index >= 0) child.Arrange(new Rect(index % Columns * cellWidth, index / Columns * RowHeight, cellWidth, RowHeight));
        }
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        if (args.Action is not System.Collections.Specialized.NotifyCollectionChangedAction.Add)
        {
            // Reset/remove also changes container indices; rebuild only the currently visible rows.
            ItemContainerGenerator.RemoveAll();
            RemoveInternalChildRange(0, InternalChildren.Count);
        }
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        if (scroll is null) return;
        var origin = TransformToAncestor(scroll).Transform(new Point());
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + origin.Y + index / Columns * RowHeight);
        InvalidateMeasure();
    }
}
