using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using KanbanContracts;

namespace Curs.ViewModels;

public sealed record TagPillViewModel(TagDto Tag)
{
    public string Name=>Tag.Name;
    public IBrush Foreground=>Brush.Parse(Tag.Color);
    public IBrush Background=>Pastel(Tag.Color);
    public static IBrush Pastel(string hex){var c=Color.Parse(hex);return new SolidColorBrush(Color.FromRgb((byte)(c.R*.15+255*.85),(byte)(c.G*.15+255*.85),(byte)(c.B*.15+255*.85)));}
}
public sealed partial class CardItemViewModel
{
    public AvatarViewModel? Avatar{get;}
    public bool HasAssignee=>Avatar is not null;
    public string Description=>Card.Description??"";
    public bool HasDescription=>!string.IsNullOrWhiteSpace(Description);
    public string Priority=>Card.Priority.ToString();
    public IBrush PriorityBackground=>Brush.Parse(Card.Priority switch{CardPriority.Low=>"#BEF0D5",CardPriority.Medium=>"#FFE093",CardPriority.High=>"#FFD0D5",_=>"#FFB6C3"});
    public IBrush PriorityForeground=>Brush.Parse(Card.Priority is CardPriority.High or CardPriority.Critical?"#9A1531":Card.Priority==CardPriority.Low?"#11644B":"#805006");
    public IReadOnlyList<TagPillViewModel> TagItems=>Card.Tags.Select(t=>new TagPillViewModel(t)).ToList();
    public string DateLabel=>Card.DueDate?.ToString("d MMM",CultureInfo.GetCultureInfo("uk-UA"))??"Без дати";
    public bool IsOverdue=>Card.DueDate is {} date&&date<DateOnly.FromDateTime(DateTime.Today);
    public IBrush DateForeground=>IsOverdue?Brush.Parse("#D95155"):Brush.Parse("#738097");
    public string CommentCount=>Card.CommentCount.ToString(CultureInfo.InvariantCulture);
}
public sealed partial class ColumnItemViewModel
{
    public int Count=>Cards.Count;
    public IBrush Background=>Brush.Parse(Column.Position%4==3?"#EBF9F1":Column.Position%4==2?"#F0F2FE":"#EFF6FA");
}
public sealed class CommentItemViewModel : IDisposable
{
    public CommentDto Comment{get;}
    public string Heading=>Comment.Author.Name;
    public string Date=>$"{Comment.CreatedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}"+(Comment.EditedAtUtc is null?"":" · змінено");
    public string Text=>Comment.Text;
    public AvatarViewModel Avatar{get;}
    public bool CanEdit{get;}
    public bool CanDelete{get;}
    public ActionCommand EditCommand{get;}
    public ActionCommand DeleteCommand{get;}
    public CommentItemViewModel(CommentDto comment,BoardContentViewModel owner)
    {
        Comment=comment;Avatar=new(comment.Author);CanEdit=comment.Author.Id==owner.CurrentUserId;CanDelete=CanEdit||owner.IsBoardAdmin;
        EditCommand=new(()=>owner.StartCommentEdit(this),()=>CanEdit);DeleteCommand=new(()=>owner.RequestCommentDelete(this),()=>CanDelete);
    }
    public void Dispose()=>Avatar.Dispose();
}
public sealed partial class BoardContentViewModel
{
    public System.Collections.Generic.IEnumerable<TagChoiceViewModel> SelectedTags=>Tags.Where(t=>t.Selected);
    private void NotifySelectedTags()=>Notify(nameof(SelectedTags));
    private double columnWidth=255;
    public double ColumnWidth{get=>columnWidth;set{columnWidth=value;Notify();}}
    private bool newColumnVisible,fullImageVisible;
    private CommentItemViewModel? editingComment,deletingComment;
    private string editCommentText="";
    public Guid? CurrentUserId=>services.Session.User?.Id;
    public bool IsBoardAdmin=>board?.Role==BoardRole.Admin;
    public bool ColumnEditorVisible=>HasColumn&&!EditorVisible;
    public bool IsArchivedCard=>selectedCard?.Card.IsArchived==true;
    public string ArchiveCardLabel=>IsArchivedCard?"Відновити картку":"Архівувати картку";
    public string ColumnLabel=>selectedColumn?.Name??"Картка";
    public DateTime? CalendarDate{get=>DateOnly.TryParseExact(DueDate,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)?date.ToDateTime(TimeOnly.MinValue):null;set=>DueDate=value?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)??"";}
    public bool NewColumnVisible{get=>newColumnVisible;private set{newColumnVisible=value;Notify();NotifyDrag();}}
    public bool FullImageVisible{get=>fullImageVisible;private set{fullImageVisible=value;Notify();}}
    public bool CommentEditorVisible=>editingComment is not null;
    public bool CommentDeleteVisible=>deletingComment is not null;
    public string EditCommentText{get=>editCommentText;set{editCommentText=value;Notify();}}
    public ActionCommand ShowNewColumnCommand{get;private set;}=null!;
    public ActionCommand CancelNewColumnCommand{get;private set;}=null!;
    public ActionCommand ShowFullImageCommand{get;private set;}=null!;
    public ActionCommand CloseFullImageCommand{get;private set;}=null!;
    public ActionCommand ArchiveCardCommand{get;private set;}=null!;
    public ActionCommand SaveCommentCommand{get;private set;}=null!;
    public ActionCommand CancelCommentCommand{get;private set;}=null!;
    public ActionCommand DeleteCommentCommand{get;private set;}=null!;
    public ActionCommand CancelDeleteCommentCommand{get;private set;}=null!;
    private void InitializePresentation()
    {
        ShowNewColumnCommand=new(()=>{NewColumnName="";NewColumnVisible=true;});CancelNewColumnCommand=new(()=>NewColumnVisible=false);
        ShowFullImageCommand=new(()=>FullImageVisible=true,()=>HasCover);CloseFullImageCommand=new(()=>FullImageVisible=false);
        ArchiveCardCommand=new(()=>_=ArchiveCard(),()=>!IsBusy&&CanDeleteCard);
        SaveCommentCommand=new(()=>_=SaveComment(),()=>!IsBusy&&CommentEditorVisible);CancelCommentCommand=new(CancelCommentEdit);
        DeleteCommentCommand=new(()=>_=DeleteComment(),()=>!IsBusy&&CommentDeleteVisible);CancelDeleteCommentCommand=new(()=>{deletingComment=null;Notify(nameof(CommentDeleteVisible));});
    }
    public void StartCommentEdit(CommentItemViewModel row){if(!row.CanEdit)return;editingComment=row;EditCommentText=row.Text;Notify(nameof(CommentEditorVisible));SaveCommentCommand.Refresh();}
    public void CancelCommentEdit(){editingComment=null;EditCommentText="";deletingComment=null;Notify(nameof(CommentEditorVisible));Notify(nameof(CommentDeleteVisible));}
    public void RequestCommentDelete(CommentItemViewModel row){if(!row.CanDelete)return;deletingComment=row;Notify(nameof(CommentDeleteVisible));DeleteCommentCommand.Refresh();}
    private Task SaveComment()=>Run(async t=>
    {
        if(board is null||selectedCard is null||editingComment is null)return;
        await services.Content.EditComment(selectedCard.Card.Id,editingComment.Comment.Id,new(){Text=EditCommentText,ExpectedBoardVersion=board.Version,ExpectedVersion=selectedCard.Card.Version,ExpectedCommentVersion=editingComment.Comment.Version!},t);
        CancelCommentEdit();await RefreshComments(t);Status="Коментар змінено.";
    });
    private Task DeleteComment()=>Run(async t=>
    {
        if(board is null||selectedCard is null||deletingComment is null)return;
        await services.Content.DeleteComment(selectedCard.Card.Id,deletingComment.Comment.Id,new(){ExpectedBoardVersion=board.Version,ExpectedVersion=selectedCard.Card.Version,ExpectedCommentVersion=deletingComment.Comment.Version!},t);
        CancelCommentEdit();await RefreshComments(t);Status="Коментар видалено.";
    });
    private async Task RefreshComments(System.Threading.CancellationToken t)
    {
        if(selectedCard is null||board is null)return;selectedCard=await services.Content.Card(selectedCard.Card.Id,t);var rows=await GetComments(selectedCard.Card.Id,t);t.ThrowIfCancellationRequested();DisposeComments();Comments.Clear();foreach(var c in rows)Comments.Add(new(c,this));await Load(board.Id,t);NotifyState();
    }
    private Task ArchiveCard()=>Run(async t=>
    {
        if(board is null||selectedCard is null||!CanDeleteCard)return;await services.Content.Archive(selectedCard.Card.Id,!IsArchivedCard,new(){ExpectedBoardVersion=board.Version,ExpectedVersion=selectedCard.Card.Version},t);
        CloseEditor();await Load(board.Id,t);Status="Архів карток оновлено.";
    });
    private void DisposeComments(){foreach(var row in Comments)row.Dispose();}
    public void LoadAvatar(AvatarViewModel? avatar){if(avatar is not null)_=avatar.Load(services);}
    private void NotifyPresentation()
    {
        foreach(var n in new[]{nameof(ColumnEditorVisible),nameof(IsArchivedCard),nameof(ArchiveCardLabel),nameof(ColumnLabel)})Notify(n);
        NotifySelectedTags();ArchiveCardCommand.Refresh();SaveCommentCommand.Refresh();DeleteCommentCommand.Refresh();ShowFullImageCommand.Refresh();
    }
}
