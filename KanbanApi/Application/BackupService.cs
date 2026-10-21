using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;
namespace KanbanApi.Application;
public sealed class BackupService(KanbanDbContext db,BoardAccess access,ImageProcessor images)
{
    public static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){MaxDepth=32,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    private static ApiException Invalid()=>new(400,"invalid_backup","Копія пошкоджена, має непідтримувану версію або некоректні дані.");
    private static ApiException Large()=>new(413,"backup_too_large","Копія: до 32 МіБ, 5000 карток і 20 МіБ оригіналів фото.");
    public static string Hash(BackupBoard data)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(data,Json)));
    public async Task<BoardBackup> Export(Guid user,Guid board,CancellationToken token)
    {
        var data=await Snapshot(user,board,true,token);var result=new BoardBackup("KanbanBoard",1,DateTime.UtcNow,Hash(data),data);
        if(JsonSerializer.SerializeToUtf8Bytes(result,Json).Length>BackupLimits.MaxFileBytes)throw Large();return result;
    }
    private Task<BackupBoard> Snapshot(Guid user,Guid id,bool media,CancellationToken token)=>db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>
    {
        await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,token);
        await access.RequireAsync(id,user,false,token);var b=await db.Boards.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,token)??throw BoardAccess.NotFound();
        await access.RequireAsync(id,user,false,token);
        var cols=await db.Columns.AsNoTracking().Where(x=>x.BoardId==id).OrderBy(x=>x.Position).ThenBy(x=>x.Id).ToListAsync(token);var colids=cols.Select(x=>x.Id).ToArray();
        var query=db.Cards.AsNoTracking().Where(x=>colids.Contains(x.ColumnId));if(await query.CountAsync(token)>BackupLimits.MaxCards)throw Large();
        var cards=await query.OrderBy(x=>x.Position).ThenBy(x=>x.Id).ToListAsync(token);var ids=cards.Select(x=>x.Id).ToArray();
        if(media&&((await db.CardImages.Where(x=>ids.Contains(x.CardId)).SumAsync(x=>(long?)x.SizeBytes,token)??0)+(b.CoverOriginalData?.LongLength??0)>20L*1024*1024))throw Large();
        var tags=await db.Tags.AsNoTracking().Where(x=>x.BoardId==id).OrderBy(x=>x.Name).ToListAsync(token);var links=await db.CardTags.Where(x=>ids.Contains(x.CardId)).ToListAsync(token);
        var checks=await db.ChecklistItems.AsNoTracking().Where(x=>ids.Contains(x.CardId)).OrderBy(x=>x.Position).ThenBy(x=>x.Id).ToListAsync(token);
        if(await db.Comments.CountAsync(x=>ids.Contains(x.CardId),token)>20000)throw Large();
        var comments=await db.Comments.AsNoTracking().Where(x=>ids.Contains(x.CardId)).OrderBy(x=>x.CreatedAtUtc).ThenBy(x=>x.Id).ToListAsync(token);
        if(comments.Count>20000)throw Large();
        var userids=cards.Select(x=>x.AssigneeId).Concat(comments.Select(x=>x.AuthorId)).OfType<Guid>().Distinct().ToArray();var users=await db.Users.Where(x=>userids.Contains(x.Id)).ToDictionaryAsync(x=>x.Id,x=>x.Name,token);
        var photos=media?await db.CardImages.AsNoTracking().Where(x=>ids.Contains(x.CardId)).ToDictionaryAsync(x=>x.CardId,x=>x.OriginalData,token):[];
        var data=new BackupBoard(b.Name,b.Description,b.IsArchived,media?b.CoverOriginalData:null,cols.Select(x=>new BackupColumn(x.Id,x.Name,x.Position)).ToList(),tags.Select(x=>new BackupTag(x.Id,x.Name,x.Color)).ToList(),cards.Select(c=>new BackupCard(c.Id,c.ColumnId,c.Title,c.Description,c.DueDate,c.Priority,c.AssigneeId is {} uid?users.GetValueOrDefault(uid):c.SourceAssigneeName,c.IsArchived,c.Position,c.CreatedAtUtc,c.UpdatedAtUtc,links.Where(x=>x.CardId==c.Id).Select(x=>x.TagId).Order().ToList(),checks.Where(x=>x.CardId==c.Id).Select(x=>new BackupCheck(x.Title,x.IsCompleted,x.Position)).ToList(),comments.Where(x=>x.CardId==c.Id).Select(x=>new BackupComment(x.Text,x.SourceAuthorName??(x.AuthorId is {} aid?users.GetValueOrDefault(aid):null)??"Видалений користувач",x.CreatedAtUtc,x.EditedAtUtc)).ToList(),photos.GetValueOrDefault(c.Id))).ToList());
        await tx.CommitAsync(token);return data;
    });
    public static void Validate(BoardBackup? backup)
    {
        if(backup is null||backup.Format!="KanbanBoard"||backup.Version!=1||backup.Data is not {} b||backup.Checksum is null||backup.Checksum.Length!=64)throw Invalid();
        if(JsonSerializer.SerializeToUtf8Bytes(backup,Json).Length>BackupLimits.MaxFileBytes)throw Large();
        if(!string.Equals(Hash(b),backup.Checksum,StringComparison.OrdinalIgnoreCase))throw Invalid();
        static bool Text(string? x,int max,bool required=false)=>x is null?!required:x.Length<=max&&(!required||!string.IsNullOrWhiteSpace(x));
        if(!Text(b.Name,150,true)||!Text(b.Description,4000)||b.Columns is null||b.Tags is null||b.Cards is null||b.Columns.Count>200||b.Tags.Count>500||b.Cards.Count>5000||b.Columns.Any(x=>x is null)||b.Tags.Any(x=>x is null)||b.Cards.Any(x=>x is null))throw Invalid();
        var cols=b.Columns.Select(x=>x.Key).ToHashSet();var tags=b.Tags.Select(x=>x.Key).ToHashSet();
        if(cols.Contains(Guid.Empty)||cols.Count!=b.Columns.Count||tags.Contains(Guid.Empty)||tags.Count!=b.Tags.Count||b.Columns.Any(x=>!Text(x.Name,100,true)||x.Position<0)||b.Tags.Any(x=>!Text(x.Name,50,true)||x.Color is null||!Regex.IsMatch(x.Color,"^#[0-9a-fA-F]{6}$"))||b.Tags.Select(x=>x.Name.ToUpperInvariant()).Distinct().Count()!=tags.Count||b.Cards.Select(x=>x.Key).Distinct().Count()!=b.Cards.Count)throw Invalid();
        long bytes=b.Cover?.Length??0;var comments=0;
        foreach(var c in b.Cards)
        {
            if(c.Key==Guid.Empty||!cols.Contains(c.ColumnKey)||!Text(c.Title,200,true)||!Text(c.Description,10000)||!Text(c.AssigneeName,160)||!Enum.IsDefined(c.Priority)||c.Position<0||c.TagKeys is null||c.Checklist is null||c.Comments is null||c.TagKeys.Count>100||c.TagKeys.Distinct().Count()!=c.TagKeys.Count||c.TagKeys.Any(x=>!tags.Contains(x))||c.Checklist.Count>100||c.Checklist.Any(x=>x is null||!Text(x.Title,300,true)||x.Position<0)||c.Comments.Any(x=>x is null||!Text(x.Text,4000,true)||!Text(x.AuthorName,160,true)))throw Invalid();
            comments+=c.Comments.Count;bytes+=c.Image?.Length??0;
            if(c.Image?.Length>ImageLimits.MaxBytes)throw Large();
        }
        if(comments>20000||bytes>20L*1024*1024||b.Cover?.Length>ImageLimits.MaxBytes)throw Large();
    }
    public async Task<RestoreBoardResult> Restore(Guid user,RestoreBoardRequest request,CancellationToken token)
    {
        if(request.OperationId==Guid.Empty)throw Invalid();Validate(request.Backup);var data=request.Backup.Data;var checksum=Hash(data);
        var bytes=SHA256.HashData(Encoding.UTF8.GetBytes(user+"/"+request.OperationId));var id=new Guid(bytes.AsSpan(0,16));
        var cover=data.Cover is {} original?await images.Process(original,token):null;var photos=new Dictionary<Guid,ProcessedImage>();
        foreach(var c in data.Cards)if(c.Image is {} image)photos[c.Key]=await images.Process(image,token);
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>
        {
            db.ChangeTracker.Clear();await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,token);
            // A key-range update lock also makes two concurrent retries create only one board.
            var previous=await db.Boards.FromSqlInterpolated($"SELECT * FROM Boards WITH (UPDLOCK,HOLDLOCK) WHERE Id={id}").SingleOrDefaultAsync(token);
            if(previous is not null){if(previous.OwnerId!=user||previous.RestoreChecksum!=checksum)throw new ApiException(409,"restore_conflict","Цю операцію вже виконано з іншим файлом.");await tx.CommitAsync(token);return new RestoreBoardResult(id,true);}
            if(!await db.Users.AnyAsync(x=>x.Id==user&&x.DeletionDueAtUtc==null,token))throw new ApiException(401,"unauthorized","Увійдіть знову.");
            db.Boards.Add(new Board{Id=id,OwnerId=user,RestoreChecksum=checksum,Name=data.Name,Description=data.Description,IsArchived=data.IsArchived,CoverOriginalData=cover?.Original,CoverThumbnailData=cover?.Thumbnail,CoverContentType=cover?.ContentType});db.BoardMembers.Add(new(){BoardId=id,UserId=user,Role=BoardRole.Admin});
            var cols=data.Columns.ToDictionary(x=>x.Key,_=>Guid.NewGuid());var tags=data.Tags.ToDictionary(x=>x.Key,_=>Guid.NewGuid());
            foreach(var c in data.Columns.OrderBy(x=>x.Position).ThenBy(x=>x.Key).Select((x,i)=>(x,i)))db.Columns.Add(new(){Id=cols[c.x.Key],BoardId=id,Name=c.x.Name,Position=c.i});
            foreach(var t in data.Tags)db.Tags.Add(new(){Id=tags[t.Key],BoardId=id,Name=t.Name,NormalizedName=t.Name.ToUpperInvariant(),Color=t.Color.ToUpperInvariant()});
            foreach(var group in data.Cards.GroupBy(x=>x.ColumnKey))foreach(var pair in group.OrderBy(x=>x.Position).ThenBy(x=>x.Key).Select((x,i)=>(x,i)))
            {
                var c=pair.x;var card=new Card{ColumnId=cols[c.ColumnKey],Title=c.Title,Description=c.Description,DueDate=c.DueDate,Priority=c.Priority,SourceAssigneeName=c.AssigneeName,IsArchived=c.IsArchived,Position=pair.i,CreatedById=user,CreatedAtUtc=c.CreatedAtUtc,UpdatedAtUtc=c.UpdatedAtUtc};db.Cards.Add(card);
                foreach(var key in c.TagKeys)db.CardTags.Add(new(){CardId=card.Id,TagId=tags[key]});
                foreach(var check in c.Checklist.OrderBy(x=>x.Position).Select((x,i)=>(x,i)))db.ChecklistItems.Add(new(){Id=Guid.NewGuid(),CardId=card.Id,Title=check.x.Title,IsCompleted=check.x.IsCompleted,Position=check.i});
                foreach(var comment in c.Comments)db.Comments.Add(new(){CardId=card.Id,Text=comment.Text,SourceAuthorName=comment.AuthorName,CreatedAtUtc=comment.CreatedAtUtc,EditedAtUtc=comment.EditedAtUtc});
                if(photos.TryGetValue(c.Key,out var photo))db.CardImages.Add(new(){CardId=card.Id,OriginalData=photo.Original,ThumbnailData=photo.Thumbnail,ContentType=photo.ContentType,ThumbnailContentType="image/png",OriginalName="restored-image",SizeBytes=photo.Original.Length});
            }
            await db.SaveChangesAsync(token);await tx.CommitAsync(token);return new RestoreBoardResult(id,false);
        });
    }
    public async Task<byte[]> Csv(Guid user,Guid board,CancellationToken token)
    {
        var b=await Snapshot(user,board,false,token);var cols=b.Columns.ToDictionary(x=>x.Key,x=>x.Name);var tags=b.Tags.ToDictionary(x=>x.Key,x=>x.Name);
        static string Cell(string? s){s??="";if(s.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@'||s.StartsWith('\t')||s.StartsWith('\r')||s.StartsWith('\n'))s="'"+s;return "\""+s.Replace("\"","\"\"")+"\"";}
        var text=new StringBuilder("\uFEFF");void Row(params string?[] values)=>text.AppendJoin(',',values.Select(Cell)).Append("\r\n");
        Row("ID","Назва","Опис","Дошка","Колонка","Дедлайн","Пріоритет","Виконавець","Теги","Чек-лист","Архів","Створено UTC","Оновлено UTC");
        foreach(var c in b.Cards)Row(c.Key.ToString(),c.Title,c.Description,b.Name,cols[c.ColumnKey],c.DueDate?.ToString("yyyy-MM-dd"),c.Priority.ToString(),c.AssigneeName,string.Join("; ",c.TagKeys.Select(x=>tags[x])),$"{c.Checklist.Count(x=>x.IsCompleted)}/{c.Checklist.Count}",c.IsArchived?"Так":"Ні",c.CreatedAtUtc.ToString("O"),c.UpdatedAtUtc.ToString("O"));return Encoding.UTF8.GetBytes(text.ToString());
    }
}
