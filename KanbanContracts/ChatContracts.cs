using System.ComponentModel.DataAnnotations;

namespace KanbanContracts;

public sealed record ChatDto(Guid Id, UserDto Peer, bool CanSend, string? LastMessage, DateTime? LastMessageAtUtc, int UnreadCount);
public sealed record ChatMessageDto(long Id, Guid? SenderId, string Text, DateTime CreatedAtUtc);
public sealed record ChatMessagesDto(IReadOnlyList<ChatMessageDto> Items, bool HasMore);
public sealed class StartChatRequest { [Required] public Guid RecipientId { get; set; } }
public sealed class SendChatMessageRequest
{
    public Guid ClientMessageId { get; set; }
    [Required, StringLength(2000)] public string Text { get; set; } = "";
}
public sealed class ChatReadRequest { [Range(1, long.MaxValue)] public long LastReadMessageId { get; set; } }
