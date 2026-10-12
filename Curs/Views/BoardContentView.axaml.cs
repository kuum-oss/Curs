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
    private Point pressed, pointer;
    private DragPayload? active;
    private Border? indicator;
    private IBrush? originalBrush;
    private Thickness originalThickness;
    private ScrollViewer[] scrollTargets = [];
    private double maximumFeedback;
    private BoardContentViewModel? Model => DataContext as BoardContentViewModel;
    private sealed record DragPayload(BoardContentViewModel Model, Guid Board, Guid Id, bool Card, string Nonce);

    public BoardContentView()
    {
        AvaloniaXamlLoader.Load(this);
        poll.Tick += async (_, _) => { if (Model is { } model) await model.PollAsync(); };
        scroll.Tick += (_, _) => ScrollAtEdges();
        AttachedToVisualTree += (_, _) => poll.Start();
        DetachedFromVisualTree += (_, _) => { poll.Stop(); scroll.Stop(); ClearIndicator(); active?.Model.EndDrag(); active = null; };
    }
    private void DragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control source || Model is not { } model || !e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual visual && (visual is Button || visual.GetVisualAncestors().OfType<Button>().Any())) return;
        var allowed = source.DataContext is CardItemViewModel ? model.CanDragCards : source.DataContext is ColumnItemViewModel && model.CanDragColumns;
        if (!allowed) return;
        candidate = source; pressed = e.GetPosition(this); e.Pointer.Capture(source); e.Handled = true;
    }
    private async void DragMoved(object? sender, PointerEventArgs e)
    {
        if (candidate is not { } source || Model is not { BoardId: Guid board } model || active is not null) return;
        var delta = e.GetPosition(this) - pressed;
        if (delta.X * delta.X + delta.Y * delta.Y < 36 || !e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) return;
        var card = source.DataContext is CardItemViewModel;
        var id = card ? ((CardItemViewModel)source.DataContext!).Card.Id : ((ColumnItemViewModel)source.DataContext!).Column.Id;
        candidate = null; e.Pointer.Capture(null);
        if (!model.BeginDrag(id, card)) return;
        var payload = new DragPayload(model, board, id, card, Guid.NewGuid().ToString("N")); active = payload; maximumFeedback = 0;
        var data = new DataTransfer(); data.Add(DataTransferItem.CreateText(payload.Nonce));
        try { await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move); }
        catch (Exception) { /* Native drag cancellation must not leave polling suspended. */ }
        finally
        {
            scroll.Stop(); scrollTargets = []; ClearIndicator(); model.EndDrag(); if (ReferenceEquals(active, payload)) active = null;
            if (Environment.GetEnvironmentVariable("KANBAN_VERIFY_DRAG") == "1")
                Console.WriteLine($"PH05 drag: visual feedback max {maximumFeedback:F3} ms; optimistic reorder {model.LastReorderFeedbackMilliseconds:F3} ms.");
        }
    }
    private void DragReleased(object? sender, PointerReleasedEventArgs e) { candidate = null; e.Pointer.Capture(null); }
    private DragPayload? Payload(DragEventArgs e) => active is { } payload && ReferenceEquals(Model, payload.Model) && Model?.BoardId == payload.Board &&
        e.DataTransfer.TryGetText() == payload.Nonce && !payload.Model.IsBusy ? payload : null;
    private (Guid Column, Guid? BeforeCard, Guid? BeforeColumn, Border Target, bool After)? Destination(Control source, DragPayload payload, DragEventArgs e)
    {
        if (payload.Model.EditorVisible || payload.Model.HasColumn || !payload.Model.IsComplete || (payload.Card && payload.Model.CardsFiltered)) return null;
        var border = source as Border;
        var row = source.DataContext as ColumnItemViewModel ?? source.GetVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<ColumnItemViewModel>().FirstOrDefault();
        if (!payload.Card)
        {
            if (row is null) return border is not null ? (Guid.Empty, null, null, border, true) : null;
            var columnBorder = source.DataContext is ColumnItemViewModel ? border : source.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.DataContext is ColumnItemViewModel);
            if (columnBorder is null || row.Column.Id == payload.Id) return null;
            var after = e.GetPosition(columnBorder).X > columnBorder.Bounds.Width / 2;
            var index = payload.Model.Columns.IndexOf(row);
            var before = after ? payload.Model.Columns.Skip(index + 1).FirstOrDefault()?.Column.Id : row.Column.Id;
            return (row.Column.Id, null, before, columnBorder, after);
        }
        if (row is null || border is null) return null;
        if (source.DataContext is CardItemViewModel target)
        {
            if (target.Card.Id == payload.Id) return null;
            var after = e.GetPosition(border).Y > border.Bounds.Height / 2;
            var index = row.Cards.IndexOf(target);
            var before = after ? row.Cards.Skip(index + 1).FirstOrDefault()?.Card.Id : target.Card.Id;
            return (row.Column.Id, before, null, border, after);
        }
        return (row.Column.Id, null, null, border, true);
    }
    private void DragOver(object? sender, DragEventArgs e)
    {
        var started = Stopwatch.GetTimestamp(); e.Handled = true; e.DragEffects = DragDropEffects.None;
        if (sender is not Control target || Payload(e) is not { } payload || Destination(target, payload, e) is not { } destination) { ClearIndicator(); return; }
        e.DragEffects = DragDropEffects.Move;
        if (!ReferenceEquals(indicator, destination.Target))
        {
            ClearIndicator(); indicator = destination.Target; originalBrush = indicator.BorderBrush; originalThickness = indicator.BorderThickness;
        }
        indicator.BorderBrush = Brushes.DodgerBlue;
        indicator.BorderThickness = payload.Card ? (destination.After ? new Thickness(0, 0, 0, 3) : new Thickness(0, 3, 0, 0)) :
            (destination.After ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0));
        scrollTargets = destination.Target.GetVisualAncestors().OfType<ScrollViewer>().ToArray();
        if (TopLevel.GetTopLevel(this) is { } root) { pointer = e.GetPosition(root); scroll.Start(); }
        maximumFeedback = Math.Max(maximumFeedback, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
    private async void Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true; e.DragEffects = DragDropEffects.None;
        if (sender is not Control target || Payload(e) is not { } payload || Destination(target, payload, e) is not { } destination) return;
        ClearIndicator(); scroll.Stop(); e.DragEffects = DragDropEffects.Move;
        if (payload.Card) await payload.Model.MoveCardAsync(payload.Id, destination.Column, destination.BeforeCard);
        else await payload.Model.ReorderColumnAsync(payload.Id, destination.BeforeColumn);
    }
    private void DragLeave(object? sender, DragEventArgs e) { ClearIndicator(); scroll.Stop(); }
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
    }
}
