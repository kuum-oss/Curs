using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KanbanContracts;

namespace Curs.Services;

public sealed class ProfileService(ApiClient api, SessionService session)
{
    public async Task<UserDto> Refresh(CancellationToken token)
    {
        var user=await api.SendAsync<UserDto>(HttpMethod.Get,"api/auth/me",cancellationToken:token)??throw new InvalidOperationException();
        token.ThrowIfCancellationRequested();session.UpdateUser(user);return user;
    }
    public async Task<UserDto> Save(UpdateProfileRequest request,CancellationToken token)
    {
        var user=await api.SendAsync<UserDto>(HttpMethod.Put,"api/profile",request,token)??throw new InvalidOperationException();token.ThrowIfCancellationRequested();session.UpdateUser(user);return user;
    }
    public async Task ChangePassword(ChangePasswordRequest request,CancellationToken token)
    {
        var auth=await api.SendAsync<AuthResponse>(HttpMethod.Put,"api/profile/password",request,token)??throw new InvalidOperationException();token.ThrowIfCancellationRequested();session.SignIn(auth.AccessToken,auth.User);
    }
    public async Task<UserDto> Avatar(byte[] bytes,string filename,string version,CancellationToken token)
    {
        using var form=MediaForm.Create(bytes,filename,"ExpectedVersion",version);
        var user=await api.MultipartAsync<UserDto>("api/profile/avatar",form,token)??throw new InvalidOperationException();token.ThrowIfCancellationRequested();session.UpdateUser(user);return user;
    }
    public async Task<UserDto> DeleteAvatar(string version,CancellationToken token)
    {
        var user=await api.SendAsync<UserDto>(HttpMethod.Delete,"api/profile/avatar?expectedVersion="+Uri.EscapeDataString(version),cancellationToken:token)??throw new InvalidOperationException();token.ThrowIfCancellationRequested();session.UpdateUser(user);return user;
    }
}
internal static class MediaForm
{
    public static MultipartFormDataContent Create(byte[] bytes,string filename,string field,string version)
    {
        if(bytes.Length>ImageLimits.MaxBytes)throw new ArgumentException("Фото: не більше 5 МіБ.");
        var form=new MultipartFormDataContent();form.Add(new StringContent(version),field);form.Add(new ByteArrayContent(bytes),"File",System.IO.Path.GetFileName(filename));return form;
    }
}
