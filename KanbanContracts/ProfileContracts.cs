using System.ComponentModel.DataAnnotations;

namespace KanbanContracts;

public sealed class UpdateProfileRequest
{
    [Required, StringLength(FieldLimits.UserName)] public string Name { get; set; } = "";
    [Required, EmailAddress, StringLength(FieldLimits.Email)] public string Email { get; set; } = "";
    public string? CurrentPassword { get; set; }
    [Required, RowVersion] public string ExpectedVersion { get; set; } = "";
}
public sealed class ChangePasswordRequest
{
    [Required] public string CurrentPassword { get; set; } = "";
    [Required, PasswordLimits] public string NewPassword { get; set; } = "";
    [Required, Compare(nameof(NewPassword))] public string ConfirmPassword { get; set; } = "";
    [Required, RowVersion] public string ExpectedVersion { get; set; } = "";
}
public sealed class ProfileVersionQuery
{
    [Required, RowVersion] public string ExpectedVersion { get; set; } = "";
}
public sealed class SetArchiveRequest : BoardMutationRequest { public bool Archived { get; set; } }
public sealed class ArchiveCardRequest : ResourceVersionQuery { public bool Archived { get; set; } }
public sealed class UpdateMemberRoleRequest : BoardMutationRequest
{
    [EnumDataType(typeof(BoardRole))] public BoardRole Role { get; set; }
}
public sealed class UpdateMembersRequest : BoardMutationRequest
{
    [Required, MaxLength(100)] public List<MemberSettings> Members { get; set; } = [];
}
public sealed class MemberSettings
{
    public Guid UserId { get; set; }
    [EnumDataType(typeof(BoardRole))] public BoardRole Role { get; set; }
    public bool CanCreateCards { get; set; }
    public bool CanDeleteCards { get; set; }
}
public sealed class FavoriteRequest { public bool Favorite { get; set; } }
public sealed class UpdateCommentRequest : CreateCommentRequest
{
    [Required, RowVersion] public string ExpectedCommentVersion { get; set; } = "";
}
public sealed class CommentVersionQuery : ResourceVersionQuery
{
    [Required, RowVersion] public string ExpectedCommentVersion { get; set; } = "";
}
public sealed record PersonalCardDto(Guid BoardId, string BoardName, CardSummaryDto Card);

public sealed record AccountDeletionBoardDto(Guid Id, string Name, string? NewOwnerName);
public sealed record AccountDeletionPreviewDto(string Email, string ExpectedVersion, string ConfirmationToken,
    IReadOnlyList<AccountDeletionBoardDto> OwnedBoards, int MembershipsToLeave);
public sealed class DeleteAccountRequest
{
    [Required] public string CurrentPassword { get; set; } = "";
    [Required, EmailAddress] public string ConfirmationEmail { get; set; } = "";
    [Required, RowVersion] public string ExpectedVersion { get; set; } = "";
    [Required, StringLength(64, MinimumLength = 64)] public string ConfirmationToken { get; set; } = "";
}

public sealed record AccountDeletionScheduledDto(DateTime DeletionDueAtUtc);
