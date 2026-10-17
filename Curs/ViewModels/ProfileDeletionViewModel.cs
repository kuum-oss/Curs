using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KanbanContracts;

namespace Curs.ViewModels;

public sealed record AccountDeletionImpactViewModel(AccountDeletionBoardDto Board)
{
    public string Description=>Board.NewOwnerName is null?$"{Board.Name} — видалити назавжди":$"{Board.Name} — новий власник: {Board.NewOwnerName}";
}
public sealed partial class ProfileViewModel
{
    private AccountDeletionPreviewDto? deletion;
    private string deletePassword="",deleteEmail="";
    public bool DeleteAccountVisible=>deletion is not null;
    public string DeleteAccountEmail=>deletion?.Email??"";
    public string DeletionMemberships=>$"Вихід зі спільних дощок: {deletion?.MembershipsToLeave??0}. Призначення на вас буде знято.";
    public IEnumerable<AccountDeletionImpactViewModel> DeletionBoards=>deletion?.OwnedBoards.Select(b=>new AccountDeletionImpactViewModel(b))??[];
    public bool HasDeletionBoards=>deletion?.OwnedBoards.Count>0;
    public string DeletePassword{get=>deletePassword;set{deletePassword=value;Notify();RefreshDeletionCommands();}}
    public string DeleteEmail{get=>deleteEmail;set{deleteEmail=value;Notify();RefreshDeletionCommands();}}
    public ActionCommand BeginDeleteAccountCommand{get;private set;}=null!;
    public ActionCommand CancelDeleteAccountCommand{get;private set;}=null!;
    public ActionCommand ConfirmDeleteAccountCommand{get;private set;}=null!;
    private void InitializeDeletionCommands()
    {
        BeginDeleteAccountCommand=new(()=>_=BeginDeleteAccount(),()=>CanEdit&&loaded is not null);
        CancelDeleteAccountCommand=new(CancelDeleteAccount,()=>!IsBusy);
        ConfirmDeleteAccountCommand=new(()=>_=DeleteAccount(),()=>!IsBusy&&deletion is not null&&DeletePassword.Length>0&&string.Equals(DeleteEmail.Trim(),deletion.Email,StringComparison.OrdinalIgnoreCase));
    }
    private void RefreshDeletionCommands()
    {
        BeginDeleteAccountCommand?.Refresh();CancelDeleteAccountCommand?.Refresh();ConfirmDeleteAccountCommand?.Refresh();
    }
    private void NotifyDeletion()
    {
        Notify(nameof(DeleteAccountVisible));Notify(nameof(DeleteAccountEmail));Notify(nameof(DeletionBoards));Notify(nameof(HasDeletionBoards));Notify(nameof(DeletionMemberships));Notify(nameof(CanEdit));
        SaveCommand.Refresh();PasswordCommand.Refresh();ResetCommand.Refresh();DeleteAvatarCommand.Refresh();RefreshDeletionCommands();
    }
    private void ResetDeletion(){deletion=null;DeletePassword="";DeleteEmail="";NotifyDeletion();}
    public void CancelDeleteAccount(){if(!IsBusy){ResetDeletion();Error="";}}
    public Task BeginDeleteAccount()=>Run(async token=>
    {
        if(loaded is null)return;
        var preview=await services.Profile.DeletionPreview(token);token.ThrowIfCancellationRequested();
        ClearPasswords();deletion=preview;DeletePassword="";DeleteEmail="";NotifyDeletion();
    });
    public Task DeleteAccount()
    {
        if(!ConfirmDeleteAccountCommand.CanExecute(null))return Task.CompletedTask;
        return Run(async token=>
        {
            if(deletion is not {} preview)return;
            try{await services.Profile.DeleteAccount(new(){CurrentPassword=DeletePassword,ConfirmationEmail=DeleteEmail.Trim(),ExpectedVersion=preview.ExpectedVersion,ConfirmationToken=preview.ConfirmationToken},token);}
            finally{if(operation?.Token==token)DeletePassword="";}
        });
    }
}
