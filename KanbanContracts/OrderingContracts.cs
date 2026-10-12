using System.ComponentModel.DataAnnotations;

namespace KanbanContracts;

public sealed class ReorderColumnsRequest : BoardMutationRequest
{
    [Required(ErrorMessage = "Передайте повний порядок колонок.")]
    public List<Guid> ColumnIds { get; set; } = [];
}
public sealed class MoveCardRequest : ResourceVersionQuery
{
    public Guid TargetColumnId { get; set; }
    public Guid? BeforeCardId { get; set; }
}
public sealed record OrderResultDto(string BoardVersion);
public sealed record MoveResultDto(Guid CardId, Guid ColumnId, string Version, string BoardVersion);
