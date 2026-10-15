using System.Security.Claims;
using KanbanApi.Application;
using KanbanContracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KanbanApi.Controllers;

[Authorize,ApiController]
public sealed class WorkspaceController(ContentService content):ControllerBase
{
    private Guid UserId=>Guid.Parse(User.FindFirstValue("sub")!);
    [HttpPut("api/cards/{cardId:guid}/archive")]
    public async Task<ActionResult<CardDetailsDto>> Archive(Guid cardId,ArchiveCardRequest request,CancellationToken token)=>Ok(await content.Archive(cardId,UserId,request,token));
    [HttpPut("api/cards/{cardId:guid}/comments/{commentId:guid}")]
    public async Task<ActionResult<CommentDto>> Edit(Guid cardId,Guid commentId,UpdateCommentRequest request,CancellationToken token)=>Ok(await content.EditComment(cardId,commentId,UserId,request.Text,new CommentVersionQuery{ExpectedBoardVersion=request.ExpectedBoardVersion,ExpectedVersion=request.ExpectedVersion,ExpectedCommentVersion=request.ExpectedCommentVersion},token));
    [HttpDelete("api/cards/{cardId:guid}/comments/{commentId:guid}")]
    public async Task<IActionResult> Delete(Guid cardId,Guid commentId,[FromQuery]CommentVersionQuery query,CancellationToken token){await content.EditComment(cardId,commentId,UserId,null,query,token);return NoContent();}
    [HttpGet("api/me/cards")]
    public async Task<ActionResult<PagedResult<PersonalCardDto>>> Cards([FromQuery]PageQuery page,[FromQuery]bool archived=false,[FromQuery]bool assignedOnly=true,CancellationToken token=default)=>Ok(await content.Overview(UserId,page,archived,assignedOnly,token));
    [HttpGet("api/me/team")]
    public async Task<ActionResult<IReadOnlyList<UserDto>>> Team(CancellationToken token)=>Ok(await content.Team(UserId,token));
}
