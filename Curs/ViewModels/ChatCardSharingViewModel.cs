using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KanbanContracts;
namespace Curs.ViewModels;
public sealed record ShareBoardOption(Guid Id,string Name){public override string ToString()=>Name;}
public sealed record ShareCardOption(Guid Id,string Title,string Context);
public sealed partial class ChatViewModel
{
    private readonly Action<Guid,Guid>? openCard;
    private bool shareVisible;
    private string shareSearch="";
    private ShareBoardOption? shareBoard;
    private ShareCardOption? shareCard;
    private Guid shareNonce=Guid.NewGuid();
    public ObservableCollection<ShareBoardOption> ShareBoards{get;}=[];
    public ObservableCollection<ShareCardOption> ShareCards{get;}=[];
    public IEnumerable<ShareCardOption> VisibleShareCards=>ShareCards.Where(c=>c.Title.Contains(ShareSearch,StringComparison.CurrentCultureIgnoreCase));
    public bool ShareVisible{get=>shareVisible;private set{shareVisible=value;Notify();RefreshSharing();}}
    public bool CanChooseShare=>CanSend;
    public bool NoShareCards=>!VisibleShareCards.Any()&&!IsBusy;
    public string ShareSearch{get=>shareSearch;set{shareSearch=value;Notify();Notify(nameof(VisibleShareCards));Notify(nameof(NoShareCards));if(shareCard is not null&&!shareCard.Title.Contains(value,StringComparison.CurrentCultureIgnoreCase))SelectedShareCard=null;}}
    public ShareBoardOption? SelectedShareBoard
    {
        get=>shareBoard;
        set{if(shareBoard==value)return;shareBoard=value;Notify();SelectedShareCard=null;if(ShareVisible&&!IsBusy)_=Run(LoadShareCards);}
    }
    public ShareCardOption? SelectedShareCard{get=>shareCard;set{if(shareCard?.Id!=value?.Id)shareNonce=Guid.NewGuid();shareCard=value;Notify();ShareSendCommand?.Refresh();}}
    public ActionCommand ShareCommand{get;private set;}=null!;
    public ActionCommand ShareSendCommand{get;private set;}=null!;
    public ActionCommand CloseShareCommand{get;private set;}=null!;
    private void InitializeSharing()
    {
        ShareCommand=new(()=>_=ShowShare(),()=>CanSend&&!ShareVisible);
        ShareSendCommand=new(()=>_=SendShared(),()=>CanSend&&ShareVisible&&SelectedShareCard is not null);
        CloseShareCommand=new(CloseShare,()=>!IsBusy);
    }
    private void RefreshSharing(){ShareCommand?.Refresh();ShareSendCommand?.Refresh();CloseShareCommand?.Refresh();Notify(nameof(CanChooseShare));Notify(nameof(NoShareCards));}
    public Task ShowShare()=>Run(async t=>
    {
        if(selected?.CanSend!=true)return;
        var boards=await services.Boards.ListAsync(t);t.ThrowIfCancellationRequested();ShareBoards.Clear();
        foreach(var b in boards.Where(b=>!b.IsArchived))ShareBoards.Add(new(b.Id,b.Name));
        ShareSearch="";ShareVisible=true;SelectedShareBoard=ShareBoards.FirstOrDefault();SelectedShareCard=null;shareNonce=Guid.NewGuid();await LoadShareCards(t);
    });
    private async Task LoadShareCards(CancellationToken t)
    {
        ShareCards.Clear();SelectedShareCard=null;Notify(nameof(VisibleShareCards));
        if(SelectedShareBoard is not {} board){Notify(nameof(NoShareCards));return;}
        var columns=await services.Content.Columns(board.Id,t);t.ThrowIfCancellationRequested();var rows=new List<ShareCardOption>();var offset=0;PagedResult<CardSummaryDto> page;
        do
        {
            page=await services.Content.Cards(board.Id,offset,t);t.ThrowIfCancellationRequested();
            rows.AddRange(page.Items.Select(c=>new ShareCardOption(c.Id,c.Title,board.Name+" · "+columns.FirstOrDefault(x=>x.Id==c.ColumnId)?.Name)));offset+=page.Items.Count;
        }while(page.Items.Count>0&&offset<page.TotalCount);
        if(SelectedShareBoard?.Id!=board.Id)return;
        foreach(var row in rows.DistinctBy(c=>c.Id))ShareCards.Add(row);Notify(nameof(VisibleShareCards));Notify(nameof(NoShareCards));
    }
    public Task SendShared()=>Run(async t=>
    {
        if(selected is not {} chat||!chat.CanSend||SelectedShareCard is not {} card)return;
        var message=await services.Chats.Share(chat.Id,card.Id,shareNonce,t);t.ThrowIfCancellationRequested();
        Merge([message]);CloseShare();ScrollToEnd?.Invoke();await ReloadList(t);
    });
    public Task OpenShared(long message)=>Run(async t=>
    {
        if(selected is not {} chat)return;var card=await services.Chats.OpenShared(chat.Id,message,t);t.ThrowIfCancellationRequested();
        if(card.BoardId is {} board&&card.CardId is {} id)openCard?.Invoke(board,id);
    });
    public void CloseShare(){ShareVisible=false;ShareBoards.Clear();ShareCards.Clear();shareBoard=null;shareCard=null;shareSearch="";shareNonce=Guid.NewGuid();Notify(nameof(SelectedShareBoard));Notify(nameof(SelectedShareCard));Notify(nameof(ShareSearch));Notify(nameof(VisibleShareCards));Notify(nameof(NoShareCards));}
}
