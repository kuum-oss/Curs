using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Curs.Services;
using KanbanContracts;

namespace Curs.ViewModels;

public sealed partial class ProfileViewModel : ViewModelBase, IDisposable
{
    private readonly ClientServices services;
    private CancellationTokenSource? operation;
    private UserDto? loaded;
    private AvatarViewModel? avatar;
    private string name="",email="",currentPassword="",newPassword="",confirmation="",error="",status="";
    private bool busy;
    public AvatarViewModel? Avatar{get=>avatar;private set{avatar?.Dispose();avatar=value;Notify();}}
    public string Name{get=>name;set{name=value;Notify();}}
    public string Email{get=>email;set{email=value;Notify();}}
    public string CurrentPassword{get=>currentPassword;set{currentPassword=value;Notify();}}
    public string NewPassword{get=>newPassword;set{newPassword=value;Notify();}}
    public string Confirmation{get=>confirmation;set{confirmation=value;Notify();}}
    public string Error{get=>error;private set{error=value;Notify();Notify(nameof(HasError));}}
    public string Status{get=>status;private set{status=value;Notify();Notify(nameof(HasStatus));}}
    public bool IsBusy{get=>busy;private set{busy=value;Notify();Notify(nameof(CanEdit));ResetCommand.Refresh();SaveCommand.Refresh();PasswordCommand.Refresh();DeleteAvatarCommand.Refresh();RefreshDeletionCommands();}}
    public bool CanEdit=>!IsBusy&&!DeleteAccountVisible;
    public bool HasError=>!string.IsNullOrWhiteSpace(Error);
    public bool HasStatus=>!string.IsNullOrWhiteSpace(Status);
    public Guid? UserId=>loaded?.Id;
    public bool HasChanges=>DeleteAccountVisible||loaded is not null&&(Name!=loaded.Name||Email!=loaded.Email||CurrentPassword.Length>0||NewPassword.Length>0||Confirmation.Length>0);
    public ActionCommand ResetCommand{get;}
    public ActionCommand SaveCommand{get;}
    public ActionCommand PasswordCommand{get;}
    public ActionCommand DeleteAvatarCommand{get;}
    public ProfileViewModel(ClientServices services)
    {
        this.services=services;
        ResetCommand=new(()=>_=Open(),()=>CanEdit);
        SaveCommand=new(()=>_=Save(),()=>CanEdit&&loaded is not null);
        PasswordCommand=new(()=>_=Password(),()=>CanEdit&&loaded is not null);
        DeleteAvatarCommand=new(()=>_=RemoveAvatar(),()=>CanEdit&&loaded?.HasAvatar==true);
        InitializeDeletionCommands();
        services.Session.Changed+=SessionChanged;
    }
    public Task Open()=>Run(async t=>Apply(await services.Profile.Refresh(t)));
    private void Apply(UserDto user)
    {
        ResetDeletion();loaded=user;Name=user.Name;Email=user.Email;Avatar=new(user);_=Avatar.Load(services);ClearPasswords();
    }
    private void ClearPasswords(){CurrentPassword="";NewPassword="";Confirmation="";}
    public Task Save()=>Run(async t=>
    {
        if(loaded is null)return;Apply(await services.Profile.Save(new(){Name=Name,Email=Email,CurrentPassword=CurrentPassword,ExpectedVersion=loaded.Version!},t));Status="Профіль збережено.";
    });
    public Task Password()=>Run(async t=>
    {
        if(loaded is null)return;await services.Profile.ChangePassword(new(){CurrentPassword=CurrentPassword,NewPassword=NewPassword,ConfirmPassword=Confirmation,ExpectedVersion=loaded.Version!},t);
        t.ThrowIfCancellationRequested();Apply(services.Session.User!);Status="Пароль змінено. Інші сеанси завершено.";
    });
    public Task Upload(byte[] bytes,string filename)=>Run(async t=>
    {
        if(loaded is null)return;var changed=await services.Profile.Avatar(bytes,filename,loaded.Version!,t);loaded=changed;Avatar=new(changed);_=Avatar.Load(services);Status="Фото профілю оновлено.";
    });
    public Task RemoveAvatar()=>Run(async t=>
    {
        if(loaded is null)return;var changed=await services.Profile.DeleteAvatar(loaded.Version!,t);loaded=changed;Avatar=new(changed);Status="Фото профілю прибрано.";
    });
    public void ImageError(string message)=>Error=message;
    private async Task Run(Func<CancellationToken,Task> action)
    {
        if(IsBusy||services.Session.AccessToken is null)return;
        using var cancellation=new CancellationTokenSource();operation=cancellation;IsBusy=true;Error="";Status="";
        try{await action(cancellation.Token);}
        catch(ApiClientException ex)
        {
            Error=ex.Problem.Errors is {Count:>0} fields?string.Join("\n",fields.Values.SelectMany(x=>x).Distinct()):ex.Message;
            if(ex.Problem.Code=="deletion_preview_changed")ResetDeletion();
            if(ex.Status==409&&ex.Problem.Code=="concurrency_conflict")
            {
                try{loaded=await services.Profile.Refresh(cancellation.Token);Error+="\nЧернетку збережено. Перевірте дані та повторіть збереження.";}catch(Exception){}
            }
        }
        catch(OperationCanceledException){}catch(HttpRequestException){Error="Не вдалося підключитися до сервера.";}catch(Exception ex){Error=ex is ArgumentException?ex.Message:"Не вдалося зберегти профіль.";}
        finally{if(ReferenceEquals(operation,cancellation)){operation=null;IsBusy=false;}}
    }
    private void SessionChanged(){if(services.Session.AccessToken is null)Clear();}
    public void Clear(){ResetDeletion();operation?.Cancel();operation=null;loaded=null;Avatar=null;Name="";Email="";ClearPasswords();Error="";Status="";IsBusy=false;}
    public void Dispose(){Clear();services.Session.Changed-=SessionChanged;}
}
