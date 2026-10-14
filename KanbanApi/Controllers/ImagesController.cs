using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using KanbanApi.Application;
using KanbanContracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KanbanApi.Controllers;

public sealed class UploadImageForm : ResourceVersionQuery
{
    [Required(ErrorMessage="Оберіть зображення.")] public IFormFile File { get; set; } = null!;
}
[Authorize, ApiController, Route("api/cards/{cardId:guid}/image")]
public sealed class ImagesController(ImageService images) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue("sub")!);
    [HttpGet]
    public Task<IActionResult> Original(Guid cardId,CancellationToken token)=>Read(cardId,false,token);
    [HttpGet("thumbnail")]
    public Task<IActionResult> Thumbnail(Guid cardId,CancellationToken token)=>Read(cardId,true,token);
    private async Task<IActionResult> Read(Guid card,bool thumbnail,CancellationToken token)
    {
        var image=await images.Get(card,UserId,thumbnail,token);
        Response.Headers.CacheControl="no-store, private"; Response.Headers["X-Content-Type-Options"]="nosniff";
        return File(image.Data,image.ContentType);
    }
    [HttpPut, RequestSizeLimit(ImageLimits.RequestBytes), RequestFormLimits(MultipartBodyLengthLimit=ImageLimits.RequestBytes)]
    public async Task<IActionResult> Put(Guid cardId,[FromForm] UploadImageForm form,CancellationToken token)
    {
        if (Request.Form.Files.Count!=1) throw new ApiException(400,"invalid_image","Передайте одну обкладинку.");
        return Ok(await images.Put(cardId,UserId,form,form.File,token));
    }
    [HttpDelete]
    public async Task<IActionResult> Delete(Guid cardId,[FromQuery] ResourceVersionQuery version,CancellationToken token)
    { await images.Delete(cardId,UserId,version,token); return NoContent(); }
}
