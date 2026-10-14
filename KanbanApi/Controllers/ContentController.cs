using System.Security.Claims;
using KanbanApi.Application;
using KanbanContracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KanbanApi.Controllers;

[Authorize, ApiController, Route("api")]
public sealed class ContentController(ContentService content) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue("sub")!);
    [HttpGet("boards/{boardId:guid}/columns")]
    public async Task<IActionResult> Columns(Guid boardId, CancellationToken token) => Ok(await content.Columns(boardId, UserId, token));
    [HttpPost("boards/{boardId:guid}/columns")]
    public async Task<IActionResult> CreateColumn(Guid boardId, CreateColumnRequest request, CancellationToken token)
    {
        var col = await content.CreateColumn(boardId, UserId, request, token); return Created($"/api/boards/{boardId}/columns/{col.Id}", col);
    }
    [HttpPut("boards/{boardId:guid}/columns/{columnId:guid}")]
    public async Task<IActionResult> UpdateColumn(Guid boardId, Guid columnId, UpdateColumnRequest request, CancellationToken token) => Ok(await content.UpdateColumn(boardId, columnId, UserId, request, token));
    [HttpDelete("boards/{boardId:guid}/columns/{columnId:guid}")]
    public async Task<IActionResult> DeleteColumn(Guid boardId, Guid columnId, [FromQuery] ResourceVersionQuery request, CancellationToken token)
    {
        await content.DeleteColumn(boardId, columnId, UserId, request, token); return NoContent();
    }
    [HttpGet("boards/{boardId:guid}/cards")]
    public async Task<IActionResult> Cards(Guid boardId, [FromQuery] CardQuery page, CancellationToken token) => Ok(await content.Cards(boardId, UserId, page, token));
    [HttpPost("boards/{boardId:guid}/cards")]
    public async Task<IActionResult> CreateCard(Guid boardId, CreateCardRequest request, CancellationToken token)
    {
        var card = await content.CreateCard(boardId, UserId, request, token); return Created($"/api/cards/{card.Card.Id}", card);
    }
    [HttpGet("cards/{cardId:guid}")]
    public async Task<IActionResult> Card(Guid cardId, CancellationToken token) => Ok(await content.GetCard(cardId, UserId, token));
    [HttpPut("cards/{cardId:guid}")]
    public async Task<IActionResult> UpdateCard(Guid cardId, UpdateCardRequest request, CancellationToken token) => Ok(await content.UpdateCard(cardId, UserId, request, token));
    [HttpDelete("cards/{cardId:guid}")]
    public async Task<IActionResult> DeleteCard(Guid cardId, [FromQuery] ResourceVersionQuery request, CancellationToken token)
    {
        await content.DeleteCard(cardId, UserId, request, token); return NoContent();
    }
    [HttpGet("boards/{boardId:guid}/tags")]
    public async Task<IActionResult> Tags(Guid boardId, CancellationToken token) => Ok(await content.Tags(boardId, UserId, token));
    [HttpPost("boards/{boardId:guid}/tags")]
    public async Task<IActionResult> CreateTag(Guid boardId, CreateTagRequest request, CancellationToken token)
    {
        var tag = await content.CreateTag(boardId, UserId, request, token); return Created($"/api/boards/{boardId}/tags", tag);
    }
    [HttpGet("cards/{cardId:guid}/comments")]
    public async Task<IActionResult> Comments(Guid cardId, [FromQuery] PageQuery page, CancellationToken token) => Ok(await content.Comments(cardId, UserId, page, token));
    [HttpPost("cards/{cardId:guid}/comments")]
    public async Task<IActionResult> AddComment(Guid cardId, CreateCommentRequest request, CancellationToken token)
    {
        var comment = await content.AddComment(cardId, UserId, request, token); return Created($"/api/cards/{cardId}/comments", comment);
    }
}
