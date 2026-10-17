using System.Security.Claims;
using KanbanApi.Application;
using KanbanContracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KanbanApi.Controllers;

[Authorize, ApiController, Route("api/chats"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ChatsController(ChatService chats) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue("sub")!);
    [HttpGet] public async Task<ActionResult<IReadOnlyList<ChatDto>>> List(CancellationToken token) => Ok(await chats.List(UserId, token));
    [HttpPost] public async Task<ActionResult<ChatDto>> Start(StartChatRequest request, CancellationToken token) => Ok(await chats.Start(UserId, request.RecipientId, token));
    [HttpGet("{id:guid}/messages")] public async Task<ActionResult<ChatMessagesDto>> Messages(Guid id, [FromQuery] long? beforeId, [FromQuery] long? afterId, CancellationToken token) => Ok(await chats.Messages(UserId, id, beforeId, afterId, token));
    [HttpPost("{id:guid}/messages")] public async Task<ActionResult<ChatMessageDto>> Send(Guid id, SendChatMessageRequest request, CancellationToken token) => Ok(await chats.Send(UserId, id, request, token));
    [HttpPost("{id:guid}/read")] public async Task<IActionResult> Read(Guid id, ChatReadRequest request, CancellationToken token) { await chats.Read(UserId, id, request.LastReadMessageId, token); return NoContent(); }
}
