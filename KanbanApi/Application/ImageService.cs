using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.Application;

public sealed record ImageBytes(byte[] Data, string ContentType);
public sealed class ImageService(KanbanDbContext db, BoardAccess access, ImageProcessor processor)
{
    private async Task<Guid> Board(Guid card, Guid user, CancellationToken token) =>
        await (from c in db.Cards join col in db.Columns on c.ColumnId equals col.Id join m in db.BoardMembers on col.BoardId equals m.BoardId
               where c.Id == card && m.UserId == user select (Guid?)col.BoardId).SingleOrDefaultAsync(token) ?? throw BoardAccess.NotFound();
    public async Task<ImageBytes> Get(Guid card, Guid user, bool thumbnail, CancellationToken token)
    {
        var board = await Board(card,user,token);
        var result = await db.CardImages.AsNoTracking().Where(i => i.CardId == card)
            .Select(i => thumbnail ? new ImageBytes(i.ThumbnailData,i.ThumbnailContentType) : new ImageBytes(i.OriginalData,i.ContentType)).SingleOrDefaultAsync(token) ?? throw BoardAccess.NotFound();
        await access.RequireAsync(board,user,false,token); return result;
    }
    public async Task<ImageMutationDto> Put(Guid card, Guid user, ResourceVersionQuery version, IFormFile file, CancellationToken token)
    {
        var board = await Board(card,user,token);
        if (file.Length > ImageLimits.MaxBytes) throw new ApiException(413,"image_too_large","Фото: не більше 5 МіБ.");
        await using var input = file.OpenReadStream(); using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024]; int read;
        while ((read = await input.ReadAsync(chunk,token)) != 0)
        { if (buffer.Length + read > ImageLimits.MaxBytes) throw new ApiException(413,"image_too_large","Фото: не більше 5 МіБ."); await buffer.WriteAsync(chunk.AsMemory(0,read),token); }
        var decoded = await processor.Process(buffer.ToArray(),token);
        return await access.MutateAsync(board,user,version.ExpectedBoardVersion,false,async _ =>
        {
            var row = await db.Cards.SingleOrDefaultAsync(c => c.Id == card,token) ?? throw BoardAccess.NotFound(); Check(row,version);
            var image = await db.CardImages.SingleOrDefaultAsync(i => i.CardId == card,token);
            if (image is null) { image = new CardImage {CardId=card}; db.CardImages.Add(image); }
            image.ContentType=decoded.ContentType; image.OriginalData=decoded.Original; image.ThumbnailData=decoded.Thumbnail; image.ThumbnailContentType="image/png";
            // Do not retain a client-supplied path or unbounded filename.
            image.OriginalName="cover"; image.SizeBytes=decoded.Original.Length; image.CreatedAtUtc=DateTime.UtcNow; row.UpdatedAtUtc=DateTime.UtcNow;
            await db.SaveChangesAsync(token); return new ImageMutationDto(Convert.ToBase64String(row.RowVersion),Convert.ToBase64String((await db.Boards.AsNoTracking().SingleAsync(b=>b.Id==board,token)).RowVersion));
        },token);
    }
    public async Task<ImageMutationDto> Delete(Guid card, Guid user, ResourceVersionQuery version, CancellationToken token)
    {
        var board=await Board(card,user,token);
        return await access.MutateAsync(board,user,version.ExpectedBoardVersion,false,async _ =>
        {
            var row=await db.Cards.SingleOrDefaultAsync(c=>c.Id==card,token) ?? throw BoardAccess.NotFound(); Check(row,version);
            await db.CardImages.Where(i=>i.CardId==card).ExecuteDeleteAsync(token); row.UpdatedAtUtc=DateTime.UtcNow; await db.SaveChangesAsync(token);
            return new ImageMutationDto(Convert.ToBase64String(row.RowVersion),Convert.ToBase64String((await db.Boards.AsNoTracking().SingleAsync(b=>b.Id==board,token)).RowVersion));
        },token);
    }
    private static void Check(Card card, ResourceVersionQuery version)
    { if (Convert.ToBase64String(card.RowVersion)!=version.ExpectedVersion) throw new ApiException(409,"concurrency_conflict","Картку вже змінено. Оновіть дані."); }
}
