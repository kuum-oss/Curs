using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Curs.ViewModels;

namespace Curs.Views;

[SuppressMessage("ReSharper", "PartialTypeWithSinglePart", Justification = "Avalonia generates a second class declaration during compilation.")]
public partial class BoardContentView : UserControl
{
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer scroll = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private Control? candidate;
    private IPointer? capturedPointer;
    private TopLevel? keyRoot;
    private Point pressed, pointer;
    private DragPayload? active;
    private Border? indicator;
    private IBrush? originalBrush;
    private Thickness originalThickness;
    private ScrollViewer[] scrollTargets = [];
    private double maximumFeedback;
    private BoardContentViewModel? Model => DataContext as BoardContentViewModel;
    private sealed record DragPayload(BoardContentViewModel Model, Guid Board, Guid Id, bool Card);

    public BoardContentView()
    {
        AvaloniaXamlLoader.Load(this);
        poll.Tick += async (_, _) => { if (Model is { } model) await model.PollAsync(); };
        scroll.Tick += (_, _) => ScrollAtEdges();
        AttachedToVisualTree += (_, _) => poll.Start();
        DetachedFromVisualTree += (_, _) => { poll.Stop(); CancelDrag(); };
    }
    private void DragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control source || Model is not { } model || active is not null ||
            !e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual visual && (visual is Button || visual.GetVisualAncestors().OfType<Button>().Any())) return;
        var allowed = source.DataContext is CardItemViewModel ? model.CanDragCards : source.DataContext is ColumnItemViewModel && model.CanDragColumns;
        if (!allowed) return;
        candidate = source; pressed = e.GetPosition(this); capturedPointer = e.Pointer;
        keyRoot = TopLevel.GetTopLevel(this); if (keyRoot is not null) keyRoot.KeyDown += DragKeyDown;
        e.Pointer.Capture(source); e.Handled = true;
    }
    private void DragMoved(object? sender, PointerEventArgs e)
    {
        if (candidate is not { } source || Model is not { BoardId: Guid board } model) return;
        if (active is null)
        {
            var delta = e.GetPosition(this) - pressed;
            if (delta.X * delta.X + delta.Y * delta.Y < 36) return;
            var card = source.DataContext is CardItemViewModel;
            var id = card ? ((CardItemViewModel)source.DataContext!).Card.Id : ((ColumnItemViewModel)source.DataContext!).Column.Id;
            if (!model.BeginDrag(id, card)) { CancelDrag(); return; }
            active = new(model, board, id, card); maximumFeedback = 0;
        }
        UpdateDestination(e.GetPosition(this)); ScrollAtEdges(); e.Handled = true;
    }
    private async void DragReleased(object? sender, PointerReleasedEventArgs e)
    {
        var payload = active;
        var destination = payload is not null ? FindDestination(e.GetPosition(this), payload) : null;
        CancelDrag(); e.Handled = payload is not null;
        if (payload is null || destination is null) return;
        if (payload.Card) await payload.Model.MoveCardAsync(payload.Id, destination.Value.Column, destination.Value.BeforeCard);
        else await payload.Model.ReorderColumnAsync(payload.Id, destination.Value.BeforeColumn);
        if (Environment.GetEnvironmentVariable("KANBAN_VERIFY_DRAG") == "1")
            Console.WriteLine($"PH05 drag: visual feedback max {maximumFeedback:F3} ms; optimistic reorder {payload.Model.LastReorderFeedbackMilliseconds:F3} ms.");
    }
    private void DragKeyDown(object? sender, KeyEventArgs e) { if (e.Key == Key.Escape) { CancelDrag(); e.Handled = true; } }
    private void DragCaptureLost(object? sender, PointerCaptureLostEventArgs e) => CancelDrag();
    private void CancelDrag()
    {
        var pointerToRelease = capturedPointer; capturedPointer = null; candidate = null;
        if (keyRoot is not null) keyRoot.KeyDown -= DragKeyDown; keyRoot = null;
        active?.Model.EndDrag(); active = null; scroll.Stop(); scrollTargets = []; ClearIndicator();
        pointerToRelease?.Capture(null);
    }
    private (Guid Column, Guid? BeforeCard, Guid? BeforeColumn, Border Target, bool After)? FindDestination(Point point, DragPayload payload)
    {
        if (!ReferenceEquals(Model, payload.Model) || Model?.BoardId != payload.Board || payload.Model.IsBusy) return null;
        var hit = this.InputHitTest(point) as Visual;
        var border = hit as Border;
        if (border?.Tag is not ("CardDropTarget" or "ColumnDropTarget" or "ColumnsEndDropTarget"))
            border = hit?.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Tag is "CardDropTarget" or "ColumnDropTarget" or "ColumnsEndDropTarget");
        if (border is null || this.TranslatePoint(point, border) is not { } local) return null;
        return Destination(border, payload, local);
    }
    private (Guid Column, Guid? BeforeCard, Guid? BeforeColumn, Border Target, bool After)? Destination(Control source, DragPayload payload, Point point)
    {
        if (payload.Model.EditorVisible || payload.Model.HasColumn || !payload.Model.IsComplete || (payload.Card && payload.Model.CardsFiltered)) return null;
        var border = source as Border;
        var row = source.DataContext as ColumnItemViewModel ?? source.GetVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<ColumnItemViewModel>().FirstOrDefault();
        if (!payload.Card)
        {
            if (row is null) return border?.Tag is "ColumnsEndDropTarget" ? (Guid.Empty, null, null, border, true) : null;
            var columnBorder = source.DataContext is ColumnItemViewModel ? border : source.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.DataContext is ColumnItemViewModel);
            if (columnBorder is null || row.Column.Id == payload.Id) return null;
            if (source.TranslatePoint(point, columnBorder) is not { } columnPoint) return null;
            var after = columnPoint.X > columnBorder.Bounds.Width / 2;
            var index = payload.Model.Columns.IndexOf(row);
            var before = after ? payload.Model.Columns.Skip(index + 1).FirstOrDefault()?.Column.Id : row.Column.Id;
            return (row.Column.Id, null, before, columnBorder, after);
        }
        if (row is null || border is null) return null;
        if (source.DataContext is CardItemViewModel target)
        {
            if (target.Card.Id == payload.Id) return null;
            var after = point.Y > border.Bounds.Height / 2;
            var index = row.Cards.IndexOf(target);
            var before = after ? row.Cards.Skip(index + 1).FirstOrDefault()?.Card.Id : target.Card.Id;
            return (row.Column.Id, before, null, border, after);
        }
        return (row.Column.Id, null, null, border, true);
    }
    private void UpdateDestination(Point point)
    {
        var started = Stopwatch.GetTimestamp();
        if (active is not { } payload || FindDestination(point, payload) is not { } destination) { ClearIndicator(); scroll.Stop(); return; }
        if (!ReferenceEquals(indicator, destination.Target))
        {
            ClearIndicator(); indicator = destination.Target; originalBrush = indicator.BorderBrush; originalThickness = indicator.BorderThickness;
        }
        indicator.BorderBrush = Brushes.DodgerBlue;
        indicator.BorderThickness = payload.Card ? (destination.After ? new Thickness(0, 0, 0, 3) : new Thickness(0, 3, 0, 0)) :
            (destination.After ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0));
        scrollTargets = destination.Target.GetVisualAncestors().OfType<ScrollViewer>().ToArray();
        if (TopLevel.GetTopLevel(this) is { } root && this.TranslatePoint(point, root) is { } rootPoint) { pointer = rootPoint; scroll.Start(); }
        maximumFeedback = Math.Max(maximumFeedback, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
    private void ClearIndicator()
    {
        if (indicator is not null) { indicator.BorderBrush = originalBrush; indicator.BorderThickness = originalThickness; }
        indicator = null;
    }
    private void ScrollAtEdges()
    {
        if (TopLevel.GetTopLevel(this) is not { } root || active is null) return;
        foreach (var viewer in scrollTargets)
        {
            if (root.TranslatePoint(pointer, viewer) is not { } point) continue;
            var x = viewer.Offset.X; var y = viewer.Offset.Y;
            if (viewer.Extent.Width > viewer.Viewport.Width)
                x += point.X < 28 ? -14 : point.X > viewer.Bounds.Width - 28 ? 14 : 0;
            if (viewer.Extent.Height > viewer.Viewport.Height)
                y += point.Y < 28 ? -14 : point.Y > viewer.Bounds.Height - 28 ? 14 : 0;
            viewer.Offset = new Vector(Math.Clamp(x, 0, Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width)), Math.Clamp(y, 0, Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height)));
        }
        if (root.TranslatePoint(pointer, this) is { } localPoint) UpdateDestination(localPoint);
    }
}
