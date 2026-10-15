namespace KanbanContracts;

public enum BoardRole : byte { Admin = 0, Member = 1 }
public enum CardPriority : byte { Low = 0, Medium = 1, High = 2, Critical = 3 }

public static class FieldLimits
{
    public const int Email = 254;
    public const int UserName = 100;
    public const int BoardName = 150;
    public const int BoardDescription = 4000;
    public const int ColumnName = 100;
    public const int CardTitle = 200;
    public const int CardDescription = 10000;
    public const int TagName = 50;
    public const int Comment = 4000;
    public const int ImageBytes = 5 * 1024 * 1024;
}

public sealed record UserDto(Guid Id, string Email, string Name, string? Version = null, bool HasAvatar = false);
public sealed record BoardSummaryDto(Guid Id, string Name, string? Description, BoardRole Role,
    int CardCount = 0, int MemberCount = 0, IReadOnlyList<UserDto>? Participants = null,
    bool HasCover = false, string? Version = null, bool IsFavorite = false, bool IsArchived = false, DateTime? LastOpenedAtUtc = null);
public sealed record BoardDetailsDto(Guid Id, string Name, string? Description, BoardRole Role,
    bool CanCreateCards, bool CanDeleteCards, string Version, bool HasCover = false, bool IsArchived = false, bool IsFavorite = false, Guid? OwnerId = null);
public sealed record MemberDto(UserDto User, BoardRole Role, bool CanCreateCards, bool CanDeleteCards);
public sealed record ColumnDto(Guid Id, Guid BoardId, string Name, int Position, string Version);
public sealed record TagDto(Guid Id, string Name, string Color);
public sealed record CardSummaryDto(Guid Id, Guid ColumnId, string Title, DateOnly? DueDate,
    CardPriority Priority, UserDto? Assignee, int Position, IReadOnlyList<TagDto> Tags,
    bool HasImage, string Version, string? Description = null, int CommentCount = 0, bool IsArchived = false);
public sealed record CardDetailsDto(CardSummaryDto Card, string? Description,
    string? ImageContentType, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
public sealed record CommentDto(Guid Id, Guid CardId, UserDto Author, string Text, DateTime CreatedAtUtc, string? Version = null, DateTime? EditedAtUtc = null);
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Offset, int Limit,
    string? BoardVersion = null);

// Independent of ASP.NET Core: shared shape for application/problem+json.
public sealed class ApiProblem
{
    public string? Title { get; init; }
    public int? Status { get; init; }
    public string? Detail { get; init; }
    public string? Code { get; init; }
    public string? TraceId { get; init; }
    public Dictionary<string, string[]>? Errors { get; init; }
}
