using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Curs.Services;
using KanbanContracts;
namespace Curs.ViewModels;
public sealed partial class BoardWorkspaceViewModel
{
    private CancellationTokenSource presentationLifetime=new();
    private bool presentationPolling;
    public async Task PollPresentation()
    {
        if(presentationPolling||IsBusy||Content.IsBusy||Profile.IsBusy||HasDraft||services.Session.AccessToken is null)return;
        presentationPolling=true;var token=presentationLifetime.Token;var id=selected?.Id;var originalPage=Page;
        try
        {
            if(BoardVisible&&id is {} boardId)
            {
                var metadata=await services.Boards.GetAsync(boardId,token);
                if(metadata.Version!=selected?.Version)
                {
                    var members=await services.Boards.MembersAsync(boardId,token);token.ThrowIfCancellationRequested();
                    if(IsBusy||HasDraft||selected?.Id!=id||Page!=originalPage)return;
                    selected=metadata;foreach(var old in Members)old.Avatar.Dispose();Members.Clear();foreach(var member in members){var row=new MemberViewModel(member,this);Members.Add(row);_=row.Avatar.Load(services);}NotifyBoard();
                }
            }
            else if(BoardsVisible)
            {
                var rows=await services.Boards.ListAsync(token);token.ThrowIfCancellationRequested();if(IsBusy||HasDraft||Page!=originalPage)return;
                static string Signature(BoardSummaryDto b)=>$"{b.Id}:{b.Version}:{b.IsFavorite}:{b.LastOpenedAtUtc:O}";
                if(!rows.Select(Signature).SequenceEqual(Boards.Select(b=>Signature(b.Board))))
                {
                    foreach(var old in Boards)old.Dispose();Boards.Clear();foreach(var row in rows)Boards.Add(new(row,this));RefreshTiles();Notify(nameof(HasBoards));Notify(nameof(IsEmpty));
                }
            }
            if(CurrentUser is {} user&&!ProfileVisible)
            {
                var current=await services.Api.SendAsync<UserDto>(System.Net.Http.HttpMethod.Get,"api/auth/me",cancellationToken:token);token.ThrowIfCancellationRequested();
                if(current is not null&&current.Version!=user.Version)services.Session.UpdateUser(current);
            }
        }
        catch(OperationCanceledException){}
        catch(ApiClientException ex) when(ex.Status==404&&ex.Problem.Code=="not_found")
        {
            if(!token.IsCancellationRequested&&selected?.Id==id&&!HasDraft){Content.Clear();selected=null;foreach(var old in Members)old.Avatar.Dispose();Members.Clear();Page="boards";NotifyBoard();Error="Дошку видалено чи доступ відкликано.";await LoadAsync();}
        }
        catch(Exception){}
        finally{presentationPolling=false;}
    }
    private void CancelPresentation(){presentationLifetime.Cancel();presentationLifetime.Dispose();presentationLifetime=new();}
}
