using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using KanbanContracts;

namespace Curs.ViewModels;

public sealed class ChecklistItemViewModel : ViewModelBase
{
    private readonly BoardContentViewModel owner;
    private string title;
    private bool completed;
    public Guid Id { get; }
    public string Title { get => title; set { title=value;Notify();owner.ChecklistChanged(); } }
    public bool IsCompleted { get => completed; set { completed=value;Notify();owner.ChecklistChanged(); } }
    public bool CanEdit => owner.CanEdit;
    public ActionCommand DeleteCommand { get; }
    public ActionCommand UpCommand { get; }
    public ActionCommand DownCommand { get; }
    public ChecklistItemViewModel(Guid id,string title,bool completed,BoardContentViewModel owner)
    {
        Id=id;this.title=title;this.completed=completed;this.owner=owner;
        DeleteCommand=new(()=>owner.RemoveChecklistItem(this),()=>CanEdit);
        UpCommand=new(()=>owner.StepChecklistItem(this,-1),()=>CanEdit&&owner.ChecklistItems.IndexOf(this)>0);
        DownCommand=new(()=>owner.StepChecklistItem(this,1),()=>CanEdit&&owner.ChecklistItems.IndexOf(this)>=0&&owner.ChecklistItems.IndexOf(this)<owner.ChecklistItems.Count-1);
    }
    public void Refresh(){Notify(nameof(CanEdit));DeleteCommand.Refresh();UpCommand.Refresh();DownCommand.Refresh();}
}
public sealed partial class CardItemViewModel
{
    public bool HasChecklist=>Card.ChecklistTotal>0;
    public string ChecklistLabel=>$"{Card.ChecklistCompleted} з {Card.ChecklistTotal}";
    public double ChecklistPercent=>Card.ChecklistTotal==0?0:100.0*Card.ChecklistCompleted/Card.ChecklistTotal;
}
public sealed partial class BoardContentViewModel
{
    private string newChecklistText="";
    public ObservableCollection<ChecklistItemViewModel> ChecklistItems { get; }=[];
    public string NewChecklistText {get=>newChecklistText;set{newChecklistText=value;Notify();AddChecklistItemCommand?.Refresh();}}
    public bool HasChecklist=>ChecklistItems.Count>0;
    public string ChecklistLabel=>$"{ChecklistItems.Count(i=>i.IsCompleted)} з {ChecklistItems.Count}";
    public double ChecklistPercent=>ChecklistItems.Count==0?0:100.0*ChecklistItems.Count(i=>i.IsCompleted)/ChecklistItems.Count;
    public ActionCommand AddChecklistItemCommand {get;private set;}=null!;
    private void InitializeChecklist()=>AddChecklistItemCommand=new(AddChecklistItem,()=>CanEdit&&EditorVisible&&ChecklistItems.Count<100&&!string.IsNullOrWhiteSpace(NewChecklistText)&&NewChecklistText.Length<=300);
    public void AddChecklistItem()
    {
        if(!CanEdit||!EditorVisible||ChecklistItems.Count>=100||string.IsNullOrWhiteSpace(NewChecklistText)||NewChecklistText.Length>300)return;
        ChecklistItems.Add(new(Guid.NewGuid(),NewChecklistText.Trim(),false,this));NewChecklistText="";ChecklistChanged();
    }
    public void RemoveChecklistItem(ChecklistItemViewModel item){if(!CanEdit||!EditorVisible)return;ChecklistItems.Remove(item);ChecklistChanged();}
    public void StepChecklistItem(ChecklistItemViewModel item,int step)
    {if(!CanEdit||!EditorVisible)return;var index=ChecklistItems.IndexOf(item);var next=index+step;if(index>=0&&next>=0&&next<ChecklistItems.Count){ChecklistItems.Move(index,next);ChecklistChanged();}}
    public void MoveChecklistItem(Guid source,Guid target,bool after)
    {
        if(!CanEdit||!EditorVisible||source==target)return;
        var row=ChecklistItems.FirstOrDefault(i=>i.Id==source);var anchor=ChecklistItems.FirstOrDefault(i=>i.Id==target);
        if(row is null||anchor is null)return;ChecklistItems.Remove(row);ChecklistItems.Insert(ChecklistItems.IndexOf(anchor)+(after?1:0),row);ChecklistChanged();
    }
    internal void ChecklistChanged()
    {Notify(nameof(HasChecklist));Notify(nameof(ChecklistLabel));Notify(nameof(ChecklistPercent));AddChecklistItemCommand?.Refresh();foreach(var row in ChecklistItems)row.Refresh();}
    private void LoadChecklist(IReadOnlyList<ChecklistItemDto>? items)
    {
        ChecklistItems.Clear();foreach(var item in (items??[]).OrderBy(i=>i.Position))ChecklistItems.Add(new(item.Id,item.Title,item.IsCompleted,this));NewChecklistText="";ChecklistChanged();
    }
    private List<ChecklistItemInput> ChecklistPayload()=>ChecklistItems.Select(i=>new ChecklistItemInput{Id=i.Id,Title=i.Title,IsCompleted=i.IsCompleted}).ToList();
    private bool ChecklistHasChanges()
    {
        if(NewChecklistText.Length>0)return true;
        var original=selectedCard?.Checklist??[];
        return !ChecklistItems.Select(i=>(i.Id,i.Title,i.IsCompleted)).SequenceEqual(original.OrderBy(i=>i.Position).Select(i=>(i.Id,i.Title,i.IsCompleted)));
    }
}
