using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using KanbanApi.Application;
using KanbanContracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KanbanApi.Controllers;

public sealed class AvatarForm
{
    [Required] public IFormFile File { get; set; } = null!;
    [Required, RowVersion] public string ExpectedVersion { get; set; } = "";
}

[Authorize, ApiController, Route("api/profile")]
public sealed class ProfileController(ProfileService profile, AccountDeletionService deletion) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue("sub")!);
    [HttpGet("deletion-preview")] public async Task<ActionResult<AccountDeletionPreviewDto>> DeletionPreview(CancellationToken token)
    { Response.Headers.CacheControl = "no-store, private"; return Ok(await deletion.Preview(UserId, token)); }
    [HttpDelete] public async Task<IActionResult> Delete(DeleteAccountRequest request, CancellationToken token)
    { return Accepted(await deletion.Schedule(UserId, request, token)); }
    [HttpPut] public async Task<ActionResult<UserDto>> Update(UpdateProfileRequest request, CancellationToken token) => Ok(await profile.Update(UserId, request, token));
    [HttpPut("password")] public async Task<ActionResult<AuthResponse>> Password(ChangePasswordRequest request, CancellationToken token) => Ok(await profile.ChangePassword(UserId, request, token));
    [HttpPut("avatar"), RequestSizeLimit(ImageLimits.RequestBytes), RequestFormLimits(MultipartBodyLengthLimit = ImageLimits.RequestBytes)]
    public async Task<ActionResult<UserDto>> PutAvatar([FromForm] AvatarForm form, CancellationToken token)
    {
        if (Request.Form.Files.Count != 1) throw new ApiException(400, "invalid_image", "Оберіть одне зображення.");
        return Ok(await profile.PutAvatar(UserId, form.ExpectedVersion, await MediaUpload.Read(form.File, token), token));
    }
    [HttpDelete("avatar")] public async Task<ActionResult<UserDto>> DeleteAvatar([FromQuery] ProfileVersionQuery query, CancellationToken token) => Ok(await profile.DeleteAvatar(UserId, query.ExpectedVersion, token));
    [HttpGet("/api/users/{id:guid}/avatar")]
    public async Task<IActionResult> Avatar(Guid id, CancellationToken token)
    {
        var bytes = await profile.Avatar(id, UserId, token);
        Response.Headers.CacheControl = "no-store, private"; Response.Headers.XContentTypeOptions = "nosniff";
        return File(bytes, "image/png");
    }
}
