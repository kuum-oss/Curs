using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using KanbanApi.Application;
using KanbanContracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KanbanApi.Controllers;

public sealed class BoardCoverForm
{
    [Required] public IFormFile File { get; set; } = null!;
    [Required,RowVersion] public string ExpectedBoardVersion { get; set; } = "";
}
[Authorize,ApiController,Route("api/boards/{boardId:guid}/cover")]
public sealed class BoardCoverController(BoardCoverService covers):ControllerBase
{
    private Guid UserId=>Guid.Parse(User.FindFirstValue("sub")!);
    [HttpPut,RequestSizeLimit(ImageLimits.RequestBytes),RequestFormLimits(MultipartBodyLengthLimit=ImageLimits.RequestBytes)]
    public async Task<ActionResult<BoardDetailsDto>> Put(Guid boardId,[FromForm] BoardCoverForm form,CancellationToken token)
    {
        if(Request.Form.Files.Count!=1)throw new ApiException(400,"invalid_image","Оберіть одне зображення.");
        return Ok(await covers.Put(boardId,UserId,form.ExpectedBoardVersion,await MediaUpload.Read(form.File,token),token));
    }
    [HttpDelete] public async Task<ActionResult<BoardDetailsDto>> Delete(Guid boardId,[FromQuery] BoardVersionQuery query,CancellationToken token)=>Ok(await covers.Delete(boardId,UserId,query.ExpectedBoardVersion,token));
    [HttpGet] public Task<IActionResult> Original(Guid boardId,CancellationToken token)=>Get(boardId,false,token);
    [HttpGet("thumbnail")] public Task<IActionResult> Thumbnail(Guid boardId,CancellationToken token)=>Get(boardId,true,token);
    private async Task<IActionResult> Get(Guid id,bool thumbnail,CancellationToken token)
    {
        var image=await covers.Get(id,UserId,thumbnail,token);Response.Headers.CacheControl="no-store, private";Response.Headers.XContentTypeOptions="nosniff";return File(image.Bytes,image.Type);
    }
}
