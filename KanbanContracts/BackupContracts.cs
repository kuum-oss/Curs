namespace KanbanContracts;
public static class BackupLimits
{
    public const int MaxFileBytes=32*1024*1024;
    public const int MaxCards=5000;
}
public sealed record BoardBackup(string Format,int Version,DateTime CreatedAtUtc,string Checksum,BackupBoard Data);
public sealed record BackupBoard(string Name,string? Description,bool IsArchived,byte[]? Cover,List<BackupColumn> Columns,List<BackupTag> Tags,List<BackupCard> Cards);
public sealed record BackupColumn(Guid Key,string Name,int Position);
public sealed record BackupTag(Guid Key,string Name,string Color);
public sealed record BackupCard(Guid Key,Guid ColumnKey,string Title,string? Description,DateOnly? DueDate,CardPriority Priority,string? AssigneeName,bool IsArchived,int Position,DateTime CreatedAtUtc,DateTime UpdatedAtUtc,List<Guid> TagKeys,List<BackupCheck> Checklist,List<BackupComment> Comments,byte[]? Image);
public sealed record BackupCheck(string Title,bool IsCompleted,int Position);
public sealed record BackupComment(string Text,string AuthorName,DateTime CreatedAtUtc,DateTime? EditedAtUtc);
public sealed record RestoreBoardRequest(Guid OperationId,BoardBackup Backup);
public sealed record RestoreBoardResult(Guid BoardId,bool AlreadyRestored);
