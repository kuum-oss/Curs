using System.ComponentModel.DataAnnotations;
namespace KanbanContracts;
public enum NotificationKind { Assignment, Mention, Deadline, Message }
public sealed record NotificationDto(long Id, NotificationKind Kind, string Title, string Context, DateTime CreatedAtUtc,
    bool IsRead, bool CanOpen, Guid? BoardId, Guid? CardId, Guid? ChatId);
public sealed record NotificationsDto(IReadOnlyList<NotificationDto> Items, int UnreadCount, long LatestId, bool HasMore);
public sealed class ReadNotificationsRequest { [Range(1,long.MaxValue)] public long ThroughId { get; set; } }
