using System.Security.Claims;
using KanbanApi.Application;
using KanbanContracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace KanbanApi.Controllers;
[Authorize,ApiController]
public sealed class BackupController(BackupService backups):ControllerBase
{
    private Guid UserId=>Guid.Parse(User.FindFirstValue("sub")!);
    [HttpGet("api/boards/{id:guid}/backup")]
    public async Task<IActionResult> Export(Guid id,CancellationToken token)=>File(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(await backups.Export(UserId,id,token),BackupService.Json),"application/json","board.kanban.json");
    [HttpGet("api/boards/{id:guid}/export.csv")]
    public async Task<IActionResult> Csv(Guid id,CancellationToken token)=>File(await backups.Csv(UserId,id,token),"text/csv; charset=utf-8","tasks.csv");
    [HttpPost("api/backups/restore"),RequestSizeLimit(BackupLimits.MaxFileBytes+1024*1024)]
    public async Task<ActionResult<RestoreBoardResult>> Restore(RestoreBoardRequest request,CancellationToken token)=>Ok(await backups.Restore(UserId,request,token));
}
