using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KanbanContracts;
namespace Curs.Services;
public sealed class InsightsService(ApiClient api)
{
    public async Task<PagedResult<ActivityDto>> History(Guid board, HistoryQuery q, CancellationToken t)=>
        (await api.SendAsync<PagedResult<ActivityDto>>(HttpMethod.Get,$"api/boards/{board}/history?offset={q.Offset}&limit={q.Limit}&days={q.Days}"+
            (q.CardId is {} card?$"&cardId={card}":"")+(q.ActorId is {} actor?$"&actorId={actor}":"")+
            (q.Field is {} field?$"&field={Uri.EscapeDataString(field)}":"")+(string.IsNullOrWhiteSpace(q.Search)?"":$"&search={Uri.EscapeDataString(q.Search)}"),cancellationToken:t))!;
    public async Task<BoardStatisticsDto> Statistics(Guid board,CancellationToken t)=>
        (await api.SendAsync<BoardStatisticsDto>(HttpMethod.Get,$"api/boards/{board}/statistics?today={DateTime.Today:yyyy-MM-dd}&utcOffsetMinutes={(int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes}",cancellationToken:t))!;
    public Task SetCompletion(Guid board,Guid? column,string version,CancellationToken t)=>
        api.SendAsync<object>(HttpMethod.Put,$"api/boards/{board}/completion-column",new CompletionColumnRequest{ColumnId=column,ExpectedBoardVersion=version},t);
}
