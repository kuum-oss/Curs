using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KanbanContracts;

namespace Curs.Services;

public sealed class ContentService(ApiClient api)
{
    public async Task<List<ColumnDto>> Columns(Guid board, CancellationToken token) => await api.SendAsync<List<ColumnDto>>(HttpMethod.Get,$"api/boards/{board}/columns",cancellationToken:token) ?? [];
    public Task<ColumnDto?> CreateColumn(Guid board,CreateColumnRequest body,CancellationToken token) => api.SendAsync<ColumnDto>(HttpMethod.Post,$"api/boards/{board}/columns",body,token);
    public Task<ColumnDto?> UpdateColumn(Guid board,Guid col,UpdateColumnRequest body,CancellationToken token) => api.SendAsync<ColumnDto>(HttpMethod.Put,$"api/boards/{board}/columns/{col}",body,token);
    public Task<object?> DeleteColumn(Guid board,Guid col,ResourceVersionQuery version,CancellationToken token) => api.SendAsync<object>(HttpMethod.Delete,$"api/boards/{board}/columns/{col}"+Query(version),cancellationToken:token);
    public async Task<PagedResult<CardSummaryDto>> Cards(Guid board,int offset,CancellationToken token,CardQuery? filter=null) => await api.SendAsync<PagedResult<CardSummaryDto>>(HttpMethod.Get,$"api/boards/{board}/cards?offset={offset}&limit=100"+Filters(filter),cancellationToken:token) ?? throw new InvalidOperationException();
    public async Task<CardDetailsDto> Card(Guid id,CancellationToken token) => await api.SendAsync<CardDetailsDto>(HttpMethod.Get,$"api/cards/{id}",cancellationToken:token) ?? throw new InvalidOperationException();
    public Task<CardDetailsDto?> CreateCard(Guid board,CreateCardRequest body,CancellationToken token) => api.SendAsync<CardDetailsDto>(HttpMethod.Post,$"api/boards/{board}/cards",body,token);
    public Task<CardDetailsDto?> UpdateCard(Guid id,UpdateCardRequest body,CancellationToken token) => api.SendAsync<CardDetailsDto>(HttpMethod.Put,$"api/cards/{id}",body,token);
    public Task<object?> DeleteCard(Guid id,ResourceVersionQuery version,CancellationToken token) => api.SendAsync<object>(HttpMethod.Delete,$"api/cards/{id}"+Query(version),cancellationToken:token);
    public async Task<List<TagDto>> Tags(Guid board,CancellationToken token) => await api.SendAsync<List<TagDto>>(HttpMethod.Get,$"api/boards/{board}/tags",cancellationToken:token) ?? [];
    public Task<TagDto?> CreateTag(Guid board,CreateTagRequest body,CancellationToken token) => api.SendAsync<TagDto>(HttpMethod.Post,$"api/boards/{board}/tags",body,token);
    public async Task<PagedResult<CommentDto>> Comments(Guid card,int offset,CancellationToken token) => await api.SendAsync<PagedResult<CommentDto>>(HttpMethod.Get,$"api/cards/{card}/comments?offset={offset}&limit=100",cancellationToken:token) ?? throw new InvalidOperationException();
    public Task<CommentDto?> AddComment(Guid card,CreateCommentRequest body,CancellationToken token) => api.SendAsync<CommentDto>(HttpMethod.Post,$"api/cards/{card}/comments",body,token);
    public Task<OrderResultDto?> ReorderColumns(Guid board, ReorderColumnsRequest body, CancellationToken token) => api.SendAsync<OrderResultDto>(HttpMethod.Put,$"api/boards/{board}/columns/order",body,token);
    public Task<MoveResultDto?> MoveCard(Guid card, MoveCardRequest body, CancellationToken token) => api.SendAsync<MoveResultDto>(HttpMethod.Post,$"api/cards/{card}/move",body,token);
    public Task<CardDetailsDto?> Archive(Guid card,bool archived,ResourceVersionQuery version,CancellationToken token)=>api.SendAsync<CardDetailsDto>(HttpMethod.Put,$"api/cards/{card}/archive",new ArchiveCardRequest{Archived=archived,ExpectedBoardVersion=version.ExpectedBoardVersion,ExpectedVersion=version.ExpectedVersion},token);
    public Task<CommentDto?> EditComment(Guid card,Guid comment,UpdateCommentRequest request,CancellationToken token)=>api.SendAsync<CommentDto>(HttpMethod.Put,$"api/cards/{card}/comments/{comment}",request,token);
    public Task<object?> DeleteComment(Guid card,Guid comment,CommentVersionQuery version,CancellationToken token)=>api.SendAsync<object>(HttpMethod.Delete,$"api/cards/{card}/comments/{comment}"+Query(version)+"&expectedCommentVersion="+Uri.EscapeDataString(version.ExpectedCommentVersion),cancellationToken:token);
    public async Task<PagedResult<PersonalCardDto>> Overview(int offset,bool archived,bool assignedOnly,CancellationToken token)=>await api.SendAsync<PagedResult<PersonalCardDto>>(HttpMethod.Get,$"api/me/cards?offset={offset}&limit=100&archived={archived}&assignedOnly={assignedOnly}",cancellationToken:token)??throw new InvalidOperationException();
    public async Task<System.Collections.Generic.List<UserDto>> Team(CancellationToken token)=>await api.SendAsync<System.Collections.Generic.List<UserDto>>(HttpMethod.Get,"api/me/team",cancellationToken:token)??[];
    private static string Filters(CardQuery? f)
    {
        if(f is null)return "";
        var result=f.Archived?"&archived=true":"";
        if(!string.IsNullOrWhiteSpace(f.Search))result+="&search="+Uri.EscapeDataString(f.Search.Trim());
        if(f.Priority is {} priority)result+="&priority="+priority;
        if(f.TagId is {} tag)result+="&tagId="+tag;
        if(f.AssigneeId is {} assignee)result+="&assigneeId="+assignee;
        if(f.Deadline is {} deadline)result+="&deadline="+deadline+"&today="+f.Today?.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture);
        return result;
    }
    private static string Query(ResourceVersionQuery v) => "?expectedBoardVersion="+Uri.EscapeDataString(v.ExpectedBoardVersion)+"&expectedVersion="+Uri.EscapeDataString(v.ExpectedVersion);
}
