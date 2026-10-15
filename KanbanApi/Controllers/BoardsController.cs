using System.Security.Claims;
using KanbanApi.Application;
using KanbanContracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KanbanApi.Controllers;

[Authorize, ApiController, Route("api/boards")]
public sealed class BoardsController(BoardService boards) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue("sub")!);
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BoardSummaryDto>>> List(CancellationToken token) => Ok(await boards.ListAsync(UserId, token));
    [HttpPost]
    public async Task<ActionResult<BoardDetailsDto>> Create(CreateBoardRequest request, CancellationToken token)
    {
        var board = await boards.CreateAsync(UserId, request, token);
        return Created($"/api/boards/{board.Id}", board);
    }
    [HttpGet("{boardId:guid}")]
    public async Task<ActionResult<BoardDetailsDto>> Get(Guid boardId, CancellationToken token) => Ok(await boards.GetAsync(boardId, UserId, token));
    [HttpPut("{boardId:guid}")]
    public async Task<ActionResult<BoardDetailsDto>> Update(Guid boardId, UpdateBoardRequest request, CancellationToken token) => Ok(await boards.UpdateAsync(boardId, UserId, request, token));
    [HttpDelete("{boardId:guid}")]
    public async Task<IActionResult> Delete(Guid boardId, [FromQuery] BoardVersionQuery query, CancellationToken token)
    {
        await boards.DeleteAsync(boardId, UserId, query.ExpectedBoardVersion, token); return NoContent();
    }
    [HttpGet("{boardId:guid}/members")]
    public async Task<ActionResult<IReadOnlyList<MemberDto>>> Members(Guid boardId, CancellationToken token) => Ok(await boards.MembersAsync(boardId, UserId, token));
    [HttpPost("{boardId:guid}/members")]
    public async Task<ActionResult<MemberDto>> AddMember(Guid boardId, AddMemberRequest request, CancellationToken token)
    {
        var member = await boards.AddMemberAsync(boardId, UserId, request, token);
        return Created($"/api/boards/{boardId}/members", member);
    }
    [HttpPatch("{boardId:guid}/members/{userId:guid}")]
    public async Task<ActionResult<MemberDto>> UpdatePermissions(Guid boardId, Guid userId, UpdateMemberPermissionsRequest request, CancellationToken token) =>
        Ok(await boards.UpdatePermissionsAsync(boardId, UserId, userId, request, token));
    [HttpDelete("{boardId:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid boardId, Guid userId, [FromQuery] BoardVersionQuery query, CancellationToken token)
    {
        await boards.RemoveMemberAsync(boardId, UserId, userId, query.ExpectedBoardVersion, token); return NoContent();
    }
    [HttpPut("{boardId:guid}/members")]
    public async Task<ActionResult<IReadOnlyList<MemberDto>>> Settings(Guid boardId,UpdateMembersRequest request,CancellationToken token) => Ok(await boards.SettingsAsync(boardId,UserId,request,token));
    [HttpPut("{boardId:guid}/archive")]
    public async Task<ActionResult<BoardDetailsDto>> Archive(Guid boardId,SetArchiveRequest request,CancellationToken token) => Ok(await boards.ArchiveAsync(boardId,UserId,request,token));
    [HttpPut("{boardId:guid}/favorite")]
    public async Task<IActionResult> Favorite(Guid boardId,FavoriteRequest request,CancellationToken token) {await boards.FavoriteAsync(boardId,UserId,request.Favorite,token);return NoContent();}
    [HttpPost("{boardId:guid}/visit")]
    public async Task<IActionResult> Visit(Guid boardId,CancellationToken token) {await boards.VisitAsync(boardId,UserId,token);return NoContent();}
}
