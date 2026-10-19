using System.Security.Claims;
using KanbanApi.Application;
using KanbanContracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace KanbanApi.Controllers;
[Authorize,ApiController,Route("api/notifications"),ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public sealed class NotificationsController(NotificationService service):ControllerBase
{
    private Guid UserId=>Guid.Parse(User.FindFirstValue("sub")!);
    [HttpGet] public async Task<ActionResult<NotificationsDto>> List([FromQuery]bool unreadOnly,[FromQuery]long? beforeId,CancellationToken t)=>Ok(await service.List(UserId,unreadOnly,beforeId,t));
    [HttpPost("{id:long}/open")] public async Task<ActionResult<NotificationDto>> Open(long id,CancellationToken t)=>Ok(await service.Open(UserId,id,t));
    [HttpPost("{id:long}/read")] public async Task<IActionResult> Read(long id,CancellationToken t){await service.Read(UserId,id,t);return NoContent();}
    [HttpPost("read-all")] public async Task<IActionResult> ReadAll(ReadNotificationsRequest r,CancellationToken t){await service.ReadAll(UserId,r.ThroughId,t);return NoContent();}
}
