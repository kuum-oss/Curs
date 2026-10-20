using KanbanContracts;

namespace KanbanApi.Domain;

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public int SessionVersion { get; set; }
    public bool IsProtectedDemo { get; set; }
    public DateTime? DeletionRequestedAtUtc { get; set; }
    public DateTime? DeletionDueAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public byte[]? AvatarData { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class Board
{
    public Guid? OwnerId { get; set; }
    public bool IsArchived { get; set; }
    public byte[]? CoverOriginalData { get; set; }
    public byte[]? CoverThumbnailData { get; set; }
    public string? CoverContentType { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = [];
}

public sealed class BoardMember
{
    public Guid BoardId { get; set; }
    public Guid UserId { get; set; }
    public BoardRole Role { get; set; } = BoardRole.Member;
    public bool CanCreateCards { get; set; } = true;
    public bool CanDeleteCards { get; set; } = true;
    public bool IsFavorite { get; set; }
    public DateTime? LastOpenedAtUtc { get; set; }
    public DateTime JoinedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class BoardColumn
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BoardId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Position { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class Card
{
    public bool IsArchived { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ColumnId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateOnly? DueDate { get; set; }
    public CardPriority Priority { get; set; } = CardPriority.Medium;
    public Guid? AssigneeId { get; set; }
    public Guid? CreatedById { get; set; }
    public int Position { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = [];
}

public sealed class Tag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BoardId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string Color { get; set; } = "#64748B";
}

public sealed class CardTag
{
    public Guid CardId { get; set; }
    public Guid TagId { get; set; }
}

public sealed class CardImage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CardId { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public string OriginalName { get; set; } = string.Empty;
    public int SizeBytes { get; set; }
    public byte[] OriginalData { get; set; } = [];
    public byte[] ThumbnailData { get; set; } = [];
    public string ThumbnailContentType { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class Comment
{
    public byte[] RowVersion { get; set; } = [];
    public DateTime? EditedAtUtc { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CardId { get; set; }
    public Guid? AuthorId { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class DirectChat
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? FirstUserId { get; set; }
    public Guid? SecondUserId { get; set; }
    public long FirstReadId { get; set; }
    public long SecondReadId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
public sealed class DirectMessage
{
    public long Id { get; set; }
    public Guid ChatId { get; set; }
    public Guid? SenderId { get; set; }
    public Guid ClientMessageId { get; set; }
    public Guid? SharedCardId { get; set; }
    public string Text { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ChecklistItem
{
    public Guid Id { get; set; }
    public Guid CardId { get; set; }
    public string Title { get; set; } = "";
    public bool IsCompleted { get; set; }
    public int Position { get; set; }
}

public sealed class Notification
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? ActorId { get; set; }
    public NotificationKind Kind { get; set; }
    public string EventKey { get; set; } = "";
    // Targets intentionally have no FK: deleted resources leave a safe, unavailable history entry.
    public Guid? CardId { get; set; }
    public Guid? CommentId { get; set; }
    public Guid? ChatId { get; set; }
    public long? MessageId { get; set; }
    public DateOnly? DueDate { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ReadAtUtc { get; set; }
}
