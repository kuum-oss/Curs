using System.ComponentModel.DataAnnotations;

namespace KanbanContracts;

public class CreateBoardRequest
{
    [Required(ErrorMessage = "Вкажіть назву дошки."), StringLength(FieldLimits.BoardName)]
    public string Name { get; set; } = string.Empty;
    [StringLength(FieldLimits.BoardDescription)]
    public string? Description { get; set; }
}
public sealed class UpdateBoardRequest : CreateBoardRequest
{
    [Required, RowVersion]
    public string ExpectedBoardVersion { get; set; } = string.Empty;
}
public sealed class AddMemberRequest
{
    [Required, EmailAddress, StringLength(FieldLimits.Email)]
    public string Email { get; set; } = string.Empty;
    [Required, RowVersion]
    public string ExpectedBoardVersion { get; set; } = string.Empty;
}
public sealed class UpdateMemberPermissionsRequest
{
    public bool CanCreateCards { get; set; }
    public bool CanDeleteCards { get; set; }
    [Required, RowVersion]
    public string ExpectedBoardVersion { get; set; } = string.Empty;
}
public sealed class BoardVersionQuery
{
    [Required, RowVersion]
    public string ExpectedBoardVersion { get; set; } = string.Empty;
}
public sealed class RowVersionAttribute : ValidationAttribute
{
    public RowVersionAttribute() => ErrorMessage = "Потрібна чинна версія дошки.";
    public override bool IsValid(object? value)
    {
        if (value is not string version || version.Length != 12) return false;
        try { return Convert.FromBase64String(version).Length == 8; }
        catch (FormatException) { return false; }
    }
}
