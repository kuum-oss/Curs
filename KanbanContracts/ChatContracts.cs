using System.ComponentModel.DataAnnotations;

namespace KanbanContracts;

public sealed record ChatDto(Guid Id, UserDto Peer, bool CanSend, string? LastMessage, DateTime? LastMessageAtUtc, int UnreadCount);
public sealed record SharedCardDto(Guid? CardId, Guid? BoardId, string Title, string Context, bool CanOpen);
public sealed record ChatMessageDto(long Id, Guid? SenderId, string Text, DateTime CreatedAtUtc, SharedCardDto? SharedCard=null);
public sealed record ChatMessagesDto(IReadOnlyList<ChatMessageDto> Items, bool HasMore);
public sealed class StartChatRequest { [Required] public Guid RecipientId { get; set; } }
public sealed class SendChatMessageRequest : IValidatableObject
{
    public Guid ClientMessageId { get; set; }
    [StringLength(2000)] public string Text { get; set; } = "";
    public Guid? SharedCardId { get; set; }
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (SharedCardId==Guid.Empty || SharedCardId is null && string.IsNullOrWhiteSpace(Text))
            yield return new ValidationResult("Введіть повідомлення або оберіть картку.",[nameof(Text),nameof(SharedCardId)]);
    }
}
public sealed class ChatReadRequest { [Range(1, long.MaxValue)] public long LastReadMessageId { get; set; } }
