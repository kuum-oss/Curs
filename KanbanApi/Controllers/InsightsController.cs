using System.Security.Claims;
using KanbanApi.Application;
using KanbanContracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace KanbanApi.Controllers;
[Authorize,ApiController]
public sealed class InsightsController(InsightsService insights):ControllerBase
{
    private Guid UserId=>Guid.Parse(User.FindFirstValue("sub")!);
    [HttpGet("api/boards/{board:guid}/history")]
    public async Task<ActionResult<PagedResult<ActivityDto>>> History(Guid board,[FromQuery]HistoryQuery query,CancellationToken token)=>Ok(await insights.History(board,UserId,query,token));
    [HttpGet("api/boards/{board:guid}/statistics")]
    public async Task<ActionResult<BoardStatisticsDto>> Statistics(Guid board,[FromQuery]StatisticsQuery query,CancellationToken token)=>Ok(await insights.Statistics(board,UserId,query,token));
    [HttpPut("api/boards/{board:guid}/completion-column")]
    public async Task<IActionResult> Completion(Guid board,CompletionColumnRequest request,CancellationToken token){await insights.SetCompletion(board,UserId,request,token);return NoContent();}
}
