using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Generators;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Emutastic.Controls
{
    /// <summary>
    /// A virtualizing, vertically-scrolling wrap panel for use as a <see cref="ListBox"/>
    /// <c>ItemsPanel</c>. Replaces the plain non-virtualizing <see cref="WrapPanel"/> that caused a
    /// ~17s UI-thread freeze on launch (all ~800 game cards realized at once). Only the cards within
    /// the viewport (plus a one-screen buffer) are realized; the rest are recycled.
    ///
    /// Assumes a UNIFORM cell size, which holds for the library grid: every card has the same width
    /// (LibraryCardWidth) and, within any single view, the same height (mixed/All-Games uses one art
    /// ratio for all cards; a single-console view is all one console). The cell size is re-measured
    /// from a sample container each pass, and the panel re-measures when LibraryCardWidth changes, so
    /// the card-size / spacing sliders still re-flow live.
    ///
    /// Realize/recycle mirrors Avalonia's own VirtualizingStackPanel (generator + recycle pool).
    /// Selection (Ctrl+A), keyboard nav and SelectedItems are preserved because the host stays a
    /// ListBox — Avalonia's selection machinery only needs the VirtualizingPanel overrides below.
    /// </summary>
    public class VirtualizingWrapPanel : VirtualizingPanel
    {
        private static readonly object s_ownContainerKey = new();

        // index -> realized container, and the reverse + recycle-key bookkeeping.
        private readonly Dictionary<int, Control> _realized = new();
        private readonly Dictionary<Control, int> _indexOf = new();
        private readonly Dictionary<Control, object?> _recycleKeyOf = new();
        private readonly Dictionary<object, Stack<Control>> _recyclePool = new();

        private Rect _viewport;                 // effective viewport in panel coordinates
        private double _cellW, _cellH;          // measured uniform cell size (incl. card margin)
        private int _cols = 1;                  // columns from the last measure (used by arrange + nav)
        private int _scrollToIndex = -1;        // index pinned by ScrollIntoView (not recycled until in-band)
        private IDisposable? _cardWidthSub;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            EffectiveViewportChanged += OnEffectiveViewportChanged;
            // Re-flow when the card-size / spacing sliders change the layout resources (the card's own
            // size change doesn't invalidate our measure — VirtualizingPanel suppresses that).
            _cardWidthSub = this.GetResourceObservable("LibraryCardWidth").Subscribe(new AnonymousObserver(() =>
            {
                _cellW = _cellH = 0;            // force re-measure of the sample cell
                InvalidateMeasure();
            }));
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            EffectiveViewportChanged -= OnEffectiveViewportChanged;
            _cardWidthSub?.Dispose();
            _cardWidthSub = null;
            // Release pooled containers (and the bitmaps their Images root) when the panel leaves the
            // tree, so switching views doesn't accumulate detached containers.
            _recyclePool.Clear();
        }

        private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
        {
            var vp = e.EffectiveViewport;
            // Re-measure only when the visible vertical band actually moved (avoids churn).
            if (Math.Abs(vp.Top - _viewport.Top) > 1 || Math.Abs(vp.Height - _viewport.Height) > 1 ||
                Math.Abs(vp.Width - _viewport.Width) > 1)
            {
                _viewport = vp;
                InvalidateMeasure();
            }
            else
            {
                _viewport = vp;
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var items = Items;
            int count = items.Count;
            if (count == 0)
            {
                RecycleRange(int.MinValue, int.MaxValue);
                return default;
            }

            // Establish the uniform cell size from a sample container if unknown.
            EnsureCellSize(items);
            if (_cellW <= 0 || _cellH <= 0)
                return default;

            double availW = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
                ? _cellW : availableSize.Width;
            int cols = Math.Max(1, (int)Math.Floor(availW / _cellW));
            int rows = (count + cols - 1) / cols;
            _cols = cols;

            // Visible vertical band + a half-viewport buffer above & below. On the very FIRST measure
            // the effective viewport isn't known yet — realize only a tiny band so the window paints
            // immediately; the EffectiveViewportChanged that fires right after expands it to the real
            // visible area (keeping each pass small avoids a pre-paint freeze).
            double vpTop = _viewport.Height > 0 ? _viewport.Top : 0;
            double vpHeight = _viewport.Height > 0 ? _viewport.Height : _cellH * 2;

            double buffer = vpHeight * 0.5;
            double bandTop = vpTop - buffer;
            double bandBottom = vpTop + vpHeight + buffer;
            int firstRow = Math.Max(0, (int)Math.Floor(bandTop / _cellH));
            int lastRow = Math.Min(rows - 1, (int)Math.Ceiling(bandBottom / _cellH));
            int firstIndex = firstRow * cols;
            int lastIndex = Math.Min(count - 1, (lastRow + 1) * cols - 1);

            // Once the scroll-to target lands inside the band it no longer needs recycle protection.
            if (_scrollToIndex >= firstIndex && _scrollToIndex <= lastIndex)
                _scrollToIndex = -1;

            // Recycle anything outside the new band, then realize + measure the band. (No per-pass cap:
            // card realization no longer decodes images on the UI thread — AsyncImage moved that
            // off-thread — so a single pass is cheap; a cap here only risked a measure/livelock storm.)
            RecycleRange(int.MinValue, firstIndex - 1);
            RecycleRange(lastIndex + 1, int.MaxValue);

            var cellConstraint = new Size(_cellW, _cellH);
            for (int i = firstIndex; i <= lastIndex; i++)
                GetOrCreateElement(items, i).Measure(cellConstraint);

            return new Size(cols * _cellW, rows * _cellH);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int cols = Math.Max(1, _cols);
            foreach (var (index, child) in _realized)
            {
                int row = index / cols;
                int col = index % cols;
                child.Arrange(new Rect(col * _cellW, row * _cellH, _cellW, _cellH));
            }
            return finalSize;
        }

        // Measure a sample container (index 0) at infinity to learn the uniform cell size. The card
        // has an explicit Width (LibraryCardWidth) + a height binding, so DesiredSize is deterministic.
        private void EnsureCellSize(IReadOnlyList<object?> items)
        {
            if (_cellW > 0 && _cellH > 0) return;
            if (items.Count == 0) return;

            var sample = GetOrCreateElement(items, 0);
            sample.Measure(Size.Infinity);
            _cellW = Math.Max(1, sample.DesiredSize.Width);
            _cellH = Math.Max(1, sample.DesiredSize.Height);
        }

        // ── realize / recycle (generator + pool), mirroring VirtualizingStackPanel ──
        private Control GetOrCreateElement(IReadOnlyList<object?> items, int index)
        {
            if (_realized.TryGetValue(index, out var existing))
                return existing;

            var item = items[index];
            var generator = ItemContainerGenerator!;
            Control container;
            object? recycleKey;

            if (generator.NeedsContainer(item, index, out recycleKey))
            {
                if (recycleKey is not null && _recyclePool.TryGetValue(recycleKey, out var pool) && pool.Count > 0)
                {
                    container = pool.Pop();
                    container.SetCurrentValue(Visual.IsVisibleProperty, true);
                    generator.PrepareItemContainer(container, item, index);
                    AddInternalChild(container);
                    generator.ItemContainerPrepared(container, item, index);
                }
                else
                {
                    container = generator.CreateContainer(item, index, recycleKey);
                    generator.PrepareItemContainer(container, item, index);
                    AddInternalChild(container);
                    generator.ItemContainerPrepared(container, item, index);
                }
            }
            else
            {
                // Item is its own container (not used by the library grid, but handle it safely).
                container = (Control)item!;
                recycleKey = s_ownContainerKey;
                if (!_indexOf.ContainsKey(container))
                {
                    generator.PrepareItemContainer(container, item, index);
                    AddInternalChild(container);
                    generator.ItemContainerPrepared(container, item, index);
                }
                container.SetCurrentValue(Visual.IsVisibleProperty, true);
            }

            _realized[index] = container;
            _indexOf[container] = index;
            _recycleKeyOf[container] = recycleKey;
            return container;
        }

        // Recycle every realized index in [from, to] (inclusive).
        private void RecycleRange(int from, int to)
        {
            if (_realized.Count == 0) return;
            var focused = ItemsControl is { } ic ? KeyboardNavigation.GetTabOnceActiveElement(ic) : null;
            List<int>? toRemove = null;
            foreach (var (index, container) in _realized)
            {
                if (index < from || index > to) continue;
                // Keep the focused container (else keyboard nav breaks once it scrolls out) and the
                // pending scroll-to target (else BringIntoView loses it) realized in place.
                if (index == _scrollToIndex || ReferenceEquals(container, focused)) continue;
                (toRemove ??= new()).Add(index);
            }
            if (toRemove is null) return;

            foreach (var index in toRemove)
            {
                var container = _realized[index];
                _realized.Remove(index);
                _indexOf.Remove(container);
                var recycleKey = _recycleKeyOf[container];
                _recycleKeyOf.Remove(container);

                if (recycleKey is null)
                {
                    ItemContainerGenerator!.ClearItemContainer(container);
                    RemoveInternalChild(container);
                }
                else if (ReferenceEquals(recycleKey, s_ownContainerKey))
                {
                    container.SetCurrentValue(Visual.IsVisibleProperty, false);
                    RemoveInternalChild(container);
                }
                else
                {
                    ItemContainerGenerator!.ClearItemContainer(container);
                    container.SetCurrentValue(Visual.IsVisibleProperty, false);
                    RemoveInternalChild(container);
                    if (!_recyclePool.TryGetValue(recycleKey, out var pool))
                        _recyclePool[recycleKey] = pool = new();
                    pool.Push(container);
                }
            }
        }

        protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
        {
            // GHOST-CARD root cause: RecycleRange exempts the container recorded in
            // KeyboardNavigation.TabOnceActiveElement (the "where does Tab return to" memory —
            // set by clicking a card, replaced only by the NEXT click, indifferent to selection,
            // keyboard focus, and time). Across a collection change that memory points at a
            // container bound to the OLD item set, so the exempted container survived every
            // swap, painted over the new view. The memory is meaningless once the items change —
            // drop it so the recycle sweep below is total.
            if (ItemsControl is { } ic && KeyboardNavigation.GetTabOnceActiveElement(ic) != null)
                KeyboardNavigation.SetTabOnceActiveElement(ic, null);
            // Simplest correct policy for our usage (navigation re-seats the whole collection, deletes
            // remove a handful): drop all realized containers and re-realize on the next measure.
            RecycleRange(int.MinValue, int.MaxValue);
            _cellW = _cellH = 0;
            InvalidateMeasure();
        }

        // ── VirtualizingPanel contract ── (overridden as 'protected'; the base 'internal' access
        // does not cross the assembly boundary, so an external override drops it)
        protected override Control? ContainerFromIndex(int index)
            => _realized.TryGetValue(index, out var c) ? c : null;

        protected override int IndexFromContainer(Control container)
            => _indexOf.TryGetValue(container, out var i) ? i : -1;

        protected override IEnumerable<Control>? GetRealizedContainers()
            => _realized.Values.ToList();

        protected override Control? ScrollIntoView(int index)
        {
            var items = Items;
            if (index < 0 || index >= items.Count) return null;
            // Realize the target (creating it if off-screen) and PIN it so the next viewport-driven
            // measure can't recycle it before the ScrollViewer's BringIntoView reads its bounds. Don't
            // hand-arrange (cols/cell may be 0 pre-measure) — let the layout pass position it.
            _scrollToIndex = index;
            var container = GetOrCreateElement(items, index);
            if (_cellW > 0 && _cellH > 0)
                container.Measure(new Size(_cellW, _cellH));
            InvalidateMeasure();
            return container;
        }

        protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
        {
            var items = Items;
            int count = items.Count;
            if (count == 0) return null;

            int cols = Math.Max(1, _cols);
            int current = from is Control c ? IndexFromContainer(c) : -1;

            int target = direction switch
            {
                NavigationDirection.First => 0,
                NavigationDirection.Last => count - 1,
                NavigationDirection.Left or NavigationDirection.Previous => current - 1,
                NavigationDirection.Right or NavigationDirection.Next => current + 1,
                NavigationDirection.Up => current - cols,
                NavigationDirection.Down => current + cols,
                NavigationDirection.PageUp => current - cols * VisibleRows(),
                NavigationDirection.PageDown => current + cols * VisibleRows(),
                _ => current,
            };

            if (current < 0) target = 0;
            if (target < 0 || target >= count)
            {
                if (!wrap) return null;
                target = target < 0 ? count - 1 : 0;
            }
            return ScrollIntoView(target);
        }

        private int VisibleRows()
            => _cellH > 0 && _viewport.Height > 0 ? Math.Max(1, (int)(_viewport.Height / _cellH)) : 1;

        private sealed class AnonymousObserver : IObserver<object?>
        {
            private readonly Action _onNext;
            public AnonymousObserver(Action onNext) => _onNext = onNext;
            public void OnNext(object? value) => _onNext();
            public void OnError(Exception error) { }
            public void OnCompleted() { }
        }
    }
}
