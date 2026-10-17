using System.Net;
using System.Net.Http.Json;
using Curs.Services;
using Curs.ViewModels;
using KanbanContracts;
using Xunit;

namespace KanbanApi.Tests;

public sealed class DesktopAccountDeletionTests
{
    private sealed class Transport(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request,token); }
    private static UserDto User()=>new(Guid.NewGuid(),"self@example.test","Власник",Convert.ToBase64String(new byte[8]));
    private static HttpResponseMessage Ok(object value)=>new(HttpStatusCode.OK){Content=JsonContent.Create(value)};
    private static AccountDeletionPreviewDto Preview(UserDto user)=>new(user.Email,user.Version!,new string('A',64),[new(Guid.NewGuid(),"Моя дошка",null)],2);
    [Fact]
    public async Task ConfirmationNeedsPreviewOwnEmailAndPasswordCancelClearsSensitiveFields()
    {
        var user=User();var deletes=0;
        using var services=new ClientServices(new Uri("http://localhost/"),transport:new Transport((r,_)=>{if(r.Method==HttpMethod.Delete){deletes++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted){Content=JsonContent.Create(new AccountDeletionScheduledDto(DateTime.UtcNow.AddDays(7)))});}return Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("deletion-preview")?Ok(Preview(user)):Ok(user));}));
        services.Session.SignIn("token",user);using var view=new ProfileViewModel(services);await view.Open();await view.DeleteAccount();Assert.Equal(0,deletes);
        await view.BeginDeleteAccount();Assert.True(view.DeleteAccountVisible);Assert.False(view.CanEdit);Assert.False(view.ConfirmDeleteAccountCommand.CanExecute(null));view.DeletePassword="password";view.DeleteEmail="wrong@example.test";await view.DeleteAccount();Assert.Equal(0,deletes);
        view.DeleteEmail=user.Email;Assert.True(view.ConfirmDeleteAccountCommand.CanExecute(null));view.CancelDeleteAccount();Assert.False(view.DeleteAccountVisible);Assert.Empty(view.DeletePassword);Assert.Empty(view.DeleteEmail);Assert.NotNull(services.Session.User);
        await view.BeginDeleteAccount();view.DeletePassword="password";view.DeleteEmail=user.Email;await view.DeleteAccount();Assert.Equal(1,deletes);Assert.Null(services.Session.User);Assert.Contains("7 днів",services.Session.PublicNotice);Assert.Null(view.UserId);Assert.False(view.DeleteAccountVisible);Assert.Empty(view.DeletePassword);
    }
    [Fact]
    public async Task FailedOrChangedPreviewCannotLogOutOrAutomaticallyRetryDeletion()
    {
        var user=User();var deletes=0;
        using var services=new ClientServices(new Uri("http://localhost/"),transport:new Transport((r,_)=>
        {
            if(r.Method==HttpMethod.Delete){deletes++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict){Content=JsonContent.Create(new ApiProblem{Code="deletion_preview_changed",Title="Дошки змінилися"})});}
            return Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("deletion-preview")?Ok(Preview(user)):Ok(user));
        }));
        services.Session.SignIn("token",user);using var view=new ProfileViewModel(services);await view.Open();await view.BeginDeleteAccount();view.DeletePassword="password";view.DeleteEmail=user.Email;await view.DeleteAccount();Assert.Equal(1,deletes);Assert.NotNull(services.Session.User);Assert.False(view.DeleteAccountVisible);Assert.Empty(view.DeletePassword);Assert.Contains("змінилися",view.Error);await view.DeleteAccount();Assert.Equal(1,deletes);
    }
    [Fact]
    public async Task LateDeletionResponseFromOldSessionCannotClearNewSession()
    {
        var user=User();var pending=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var services=new ClientServices(new Uri("http://localhost/"),transport:new Transport((r,_)=>
        {
            if(r.Method==HttpMethod.Delete){reached.SetResult();return pending.Task;}
            return Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("deletion-preview")?Ok(Preview(user)):Ok(user));
        }));
        services.Session.SignIn("old-token",user);using var view=new ProfileViewModel(services);await view.Open();await view.BeginDeleteAccount();view.DeleteEmail=user.Email;view.DeletePassword="password";var deleting=view.DeleteAccount();await reached.Task;
        services.Session.Clear();var next=User();services.Session.SignIn("new-token",next);view.DeletePassword="new-session-draft";pending.SetResult(new(HttpStatusCode.Accepted){Content=JsonContent.Create(new AccountDeletionScheduledDto(DateTime.UtcNow.AddDays(7)))});await deleting;Assert.Equal("new-token",services.Session.AccessToken);Assert.Equal(next,services.Session.User);Assert.Equal("new-session-draft",view.DeletePassword);Assert.Null(view.UserId);
    }
    [Fact]
    public async Task DemoProfileCannotOpenOrSubmitDeletionAndScheduleShowsRecoveryNotice()
    {
        var user=User() with{IsDeletionProtected=true};var writes=0;
        using var services=new ClientServices(new Uri("http://localhost/"),transport:new Transport((r,_)=>{if(r.RequestUri!.AbsolutePath!="/api/auth/me")writes++;return Task.FromResult(Ok(user));}));
        services.Session.SignIn("token",user);using var view=new ProfileViewModel(services);await view.Open();Assert.True(view.IsDeletionProtected);Assert.False(view.BeginDeleteAccountCommand.CanExecute(null));await view.BeginDeleteAccount();await view.DeleteAccount();Assert.False(view.DeleteAccountVisible);Assert.Equal(0,writes);
    }

}
