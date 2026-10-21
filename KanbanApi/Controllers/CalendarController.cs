using System.Security.Claims;
using KanbanApi.Application;
using KanbanContracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace KanbanApi.Controllers;
[Authorize,ApiController]
public sealed class CalendarController(CalendarService calendar):ControllerBase
{
    private Guid UserId=>Guid.Parse(User.FindFirstValue("sub")!);
    [HttpGet("api/me/calendar")]
    public async Task<ActionResult<PagedResult<CalendarCardDto>>> List([FromQuery]CalendarQuery query,CancellationToken token)=>Ok(await calendar.List(UserId,query,token));
    [HttpPut("api/cards/{id:guid}/deadline")]
    public async Task<IActionResult> Change(Guid id,ChangeDeadlineRequest request,CancellationToken token){await calendar.Change(UserId,id,request,token);return NoContent();}
}
