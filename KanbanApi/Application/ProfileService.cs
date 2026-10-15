using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.Application;

public static class UserPresentation
{
    public static UserDto Dto(User user) => new(user.Id, user.Email, user.Name, Convert.ToBase64String(user.RowVersion), user.AvatarData is not null);
}

public sealed class ProfileService(KanbanDbContext db, PasswordService passwords, JwtService jwt, ImageProcessor images)
{
    private async Task<User> Current(Guid id, string version, CancellationToken token)
    {
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == id, token)
            ?? throw new ApiException(401, "unauthorized", "Увійдіть знову.");
        if (Convert.ToBase64String(user.RowVersion) != version)
            throw new ApiException(409, "concurrency_conflict", "Профіль уже змінено. Оновіть дані.");
        return user;
    }
    private void VerifyPassword(User user, string? value)
    {
        if (string.IsNullOrEmpty(value) || !passwords.Verify(value, user.PasswordHash))
            throw new ApiException(400, "current_password_invalid", "Поточний пароль неправильний.");
    }
    public async Task<UserDto> Update(Guid id, UpdateProfileRequest request, CancellationToken token)
    {
        var user = await Current(id, request.ExpectedVersion, token);
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ApiException(400, "validation_failed", "Вкажіть ім’я.");
        var email = request.Email.Trim(); var normalized = email.ToUpperInvariant();
        if (normalized != user.NormalizedEmail) VerifyPassword(user, request.CurrentPassword);
        user.Name = request.Name.Trim(); user.Email = email; user.NormalizedEmail = normalized;
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        { throw new ApiException(409, "email_exists", "Цей email уже використовується."); }
        return UserPresentation.Dto(user);
    }
    public async Task<AuthResponse> ChangePassword(Guid id, ChangePasswordRequest request, CancellationToken token)
    {
        var user = await Current(id, request.ExpectedVersion, token); VerifyPassword(user, request.CurrentPassword);
        user.PasswordHash = passwords.Hash(request.NewPassword); user.SessionVersion++;
        await db.SaveChangesAsync(token);
        return jwt.Issue(UserPresentation.Dto(user), user.SessionVersion);
    }
    public async Task<UserDto> PutAvatar(Guid id, string version, byte[] bytes, CancellationToken token)
    {
        var user = await Current(id, version, token);
        var image = await images.Process(bytes, token);
        user.AvatarData = image.Thumbnail;
        await db.SaveChangesAsync(token); return UserPresentation.Dto(user);
    }
    public async Task<UserDto> DeleteAvatar(Guid id, string version, CancellationToken token)
    {
        var user = await Current(id, version, token); user.AvatarData = null;
        await db.SaveChangesAsync(token); return UserPresentation.Dto(user);
    }
    public async Task<byte[]> Avatar(Guid id, Guid viewer, CancellationToken token)
    {
        async Task Allowed()
        {
            if (id != viewer && !await db.BoardMembers.AnyAsync(target => target.UserId == id &&
                db.BoardMembers.Any(caller => caller.UserId == viewer && caller.BoardId == target.BoardId), token))
                throw BoardAccess.NotFound();
        }
        await Allowed();
        var bytes = await db.Users.Where(x => x.Id == id).Select(x => x.AvatarData).SingleOrDefaultAsync(token) ?? throw BoardAccess.NotFound();
        await Allowed(); return bytes;
    }
}

public static class MediaUpload
{
    public static async Task<byte[]> Read(IFormFile file, CancellationToken token)
    {
        if (file.Length > ImageLimits.MaxBytes) throw new ApiException(413, "image_too_large", "Фото: не більше 5 МіБ.");
        await using var input = file.OpenReadStream(); using var bytes = new MemoryStream(); var chunk = new byte[65536]; int read;
        while ((read = await input.ReadAsync(chunk, token)) != 0)
        {
            if (bytes.Length + read > ImageLimits.MaxBytes) throw new ApiException(413, "image_too_large", "Фото: не більше 5 МіБ.");
            await bytes.WriteAsync(chunk.AsMemory(0, read), token);
        }
        return bytes.ToArray();
    }
}
