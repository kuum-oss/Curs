using System.ComponentModel.DataAnnotations;

namespace KanbanContracts;

public sealed record ChecklistItemDto(Guid Id, string Title, bool IsCompleted, int Position);
public sealed class ChecklistItemInput
{
    public Guid Id { get; set; }
    [Required(ErrorMessage = "Введіть текст пункту."), StringLength(300, ErrorMessage = "Пункт чек-листа: до 300 символів.")]
    public string Title { get; set; } = "";
    public bool IsCompleted { get; set; }
}
