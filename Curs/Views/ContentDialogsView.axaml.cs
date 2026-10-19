using System;
using System.Linq;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Curs.ViewModels;
namespace Curs.Views;
public partial class ContentDialogsView:UserControl
{
    public ContentDialogsView()=>AvaloniaXamlLoader.Load(this);
    private Guid? checklistSource;
    private Border? checklistTarget;
    private Point checklistStart;
    private bool checklistDragging,checklistAfter;
    private void ChecklistDragPressed(object? sender,PointerPressedEventArgs e)
    {
        if(sender is not Control{DataContext:ChecklistItemViewModel row} grip||DataContext is not BoardContentViewModel{CanEdit:true}||!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)return;
        checklistSource=row.Id;checklistStart=e.GetPosition(this);checklistDragging=false;e.Pointer.Capture(grip);e.Handled=true;
    }
    private void ChecklistDragMoved(object? sender,PointerEventArgs e)
    {
        if(checklistSource is null||DataContext is not BoardContentViewModel{CanEdit:true})return;
        var point=e.GetPosition(this);if(!checklistDragging&&Math.Abs(point.Y-checklistStart.Y)<5)return;checklistDragging=true;
        checklistTarget?.Classes.Remove("checklist-over");checklistTarget=null;
        foreach(var row in this.GetVisualDescendants().OfType<Border>().Where(b=>Equals(b.Tag,"ChecklistRow")))
        {
            var local=e.GetPosition(row);
            if(local.X>=0&&local.X<=row.Bounds.Width&&local.Y>=0&&local.Y<=row.Bounds.Height&&row.DataContext is ChecklistItemViewModel item&&item.Id!=checklistSource)
            {checklistTarget=row;checklistAfter=local.Y>row.Bounds.Height/2;row.Classes.Add("checklist-over");break;}
        }
        e.Handled=true;
    }
    private void ChecklistDragReleased(object? sender,PointerReleasedEventArgs e)
    {
        var source=checklistSource;var target=checklistTarget?.DataContext as ChecklistItemViewModel;var after=checklistAfter;var moved=checklistDragging;
        ResetChecklistDrag();e.Pointer.Capture(null);
        if(moved&&source is {} id&&target is not null&&DataContext is BoardContentViewModel model)model.MoveChecklistItem(id,target.Id,after);
        e.Handled=true;
    }
    private void ChecklistDragCaptureLost(object? sender,PointerCaptureLostEventArgs e)=>ResetChecklistDrag();
    private void ResetChecklistDrag(){checklistTarget?.Classes.Remove("checklist-over");checklistTarget=null;checklistSource=null;checklistDragging=false;}
    private void CoverDragOver(object? sender,DragEventArgs e)
    {
        if(sender is Control target&&DataContext is BoardContentViewModel model)
            MediaPicker.DragOver(target,e,model.EditingCardId is {} id&&model.CanDropCover(id));
    }
    private void CoverDragLeave(object? sender,DragEventArgs e){if(sender is Control target)target.Classes.Remove("file-over");}
    private async void CoverDrop(object? sender,DragEventArgs e)
    {
        if(sender is Control target)target.Classes.Remove("file-over");e.Handled=true;
        if(DataContext is not BoardContentViewModel model||model.EditingCardId is not {} id||!model.CanDropCover(id))return;
        try{var file=MediaPicker.SingleFile(e);await model.ImportCoverAsync(id,t=>MediaPicker.Read(file,t));}
        catch(ArgumentException ex){model.ImageError(ex.Message);}
        catch(Exception){model.ImageError("Не вдалося відкрити фото.");}
    }
    private void AvatarAttached(object? sender,Avalonia.VisualTreeAttachmentEventArgs e){if(sender is Control{DataContext:AvatarViewModel avatar}&&DataContext is BoardContentViewModel model)model.LoadAvatar(avatar);}
    private async void ChooseCover(object? sender,RoutedEventArgs e)
    {
        if(DataContext is not BoardContentViewModel model||!model.CanChangeCover)return;var board=model.BoardId;var card=model.EditingCardId;
        try
        {
            if(await MediaPicker.Pick(this,"Фото-обкладинка") is not {} file)return;
            while(model.IsPolling&&model.BoardId==board&&model.EditingCardId==card)await System.Threading.Tasks.Task.Delay(30);
            if(model.BoardId==board&&model.EditingCardId==card&&model.CanChangeCover)await model.UploadCoverAsync(file.Bytes,file.Name);
        }
        catch(Exception ex){model.ImageError(ex is ArgumentException?ex.Message:"Не вдалося відкрити фото.");}
    }
}
