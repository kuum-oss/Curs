using System.ComponentModel.DataAnnotations;
namespace KanbanContracts;
public sealed class CalendarQuery : PageQuery, IValidatableObject
{
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public Guid? AssigneeId { get; set; }
    public bool Unassigned { get; set; }
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if(To<=From||To.DayNumber-From.DayNumber>42||AssigneeId==Guid.Empty||Unassigned&&AssigneeId is not null)
            yield return new ValidationResult("Оберіть період до 42 днів та одного виконавця.");
    }
}
public sealed record CalendarCardDto(Guid Id,Guid BoardId,string Title,string BoardName,string ColumnName,DateOnly? DueDate,CardPriority Priority,UserDto? Assignee,string Version,string BoardVersion);
public sealed class ChangeDeadlineRequest : ResourceVersionQuery { public DateOnly? DueDate { get; set; } }
