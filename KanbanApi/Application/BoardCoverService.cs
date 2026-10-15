using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.Application;

public sealed class BoardCoverService(KanbanDbContext db,BoardAccess access,ImageProcessor processor,BoardService boards)
{
    public async Task<BoardDetailsDto> Put(Guid id,Guid userId,string version,byte[] bytes,CancellationToken token)
    {
        await access.RequireAsync(id,userId,true,token);
        var image=await processor.Process(bytes,token);
        return await access.MutateAsync(id,userId,version,true,async _ =>
        {
            await db.Boards.Where(b=>b.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(b=>b.CoverOriginalData,image.Original)
                .SetProperty(b=>b.CoverThumbnailData,image.Thumbnail).SetProperty(b=>b.CoverContentType,image.ContentType),token);
            return await boards.GetAsync(id,userId,token);
        },token);
    }
    public Task<BoardDetailsDto> Delete(Guid id,Guid userId,string version,CancellationToken token) =>
        access.MutateAsync(id,userId,version,true,async _ =>
        {
            await db.Boards.Where(b=>b.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(b=>b.CoverOriginalData,(byte[]?)null)
                .SetProperty(b=>b.CoverThumbnailData,(byte[]?)null).SetProperty(b=>b.CoverContentType,(string?)null),token);
            return await boards.GetAsync(id,userId,token);
        },token);
    public async Task<(byte[] Bytes,string Type)> Get(Guid id,Guid userId,bool thumbnail,CancellationToken token)
    {
        await access.RequireAsync(id,userId,false,token);
        var row=await db.Boards.Where(b=>b.Id==id).Select(b=>new{Bytes=thumbnail?b.CoverThumbnailData:b.CoverOriginalData,Type=thumbnail?"image/png":b.CoverContentType}).SingleAsync(token);
        if(row.Bytes is null)throw BoardAccess.NotFound();
        await access.RequireAsync(id,userId,false,token);return(row.Bytes,row.Type!);
    }
}
