using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KanbanContracts;
namespace Curs.Services;
public sealed class CalendarService(ApiClient api)
{
    public async Task<PagedResult<CalendarCardDto>> List(DateOnly from,DateOnly to,int offset,Guid? assignee,bool unassigned,CancellationToken token)=>
        (await api.SendAsync<PagedResult<CalendarCardDto>>(HttpMethod.Get,$"api/me/calendar?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&offset={offset}&limit=500&unassigned={unassigned.ToString().ToLowerInvariant()}"+(assignee is {} id?$"&assigneeId={id}":""),cancellationToken:token))!;
    public Task Change(CalendarCardDto card,DateOnly? date,CancellationToken token)=>api.SendAsync<object>(HttpMethod.Put,$"api/cards/{card.Id}/deadline",new ChangeDeadlineRequest{DueDate=date,ExpectedVersion=card.Version,ExpectedBoardVersion=card.BoardVersion},token);
}
