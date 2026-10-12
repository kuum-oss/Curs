using System.Security.Claims;
using KanbanApi.Application;
using KanbanContracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KanbanApi.Controllers;

[Authorize, ApiController, Route("api")]
public sealed class OrderingController(OrderingService ordering) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue("sub")!);
    [HttpPut("boards/{boardId:guid}/columns/order")]
    public async Task<IActionResult> Columns(Guid boardId, ReorderColumnsRequest request, CancellationToken token) =>
        Ok(await ordering.ReorderColumns(boardId, UserId, request, token));
    [HttpPost("cards/{cardId:guid}/move")]
    public async Task<IActionResult> Card(Guid cardId, MoveCardRequest request, CancellationToken token) =>
        Ok(await ordering.MoveCard(cardId, UserId, request, token));
}
