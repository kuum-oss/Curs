using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using KanbanContracts;
using Xunit;

namespace KanbanApi.Tests;

[Collection("sql")]
public sealed class ProfileTests(SqlFixture fixture)
{
    private static async Task<AuthResponse> Register(HttpClient client, string name = "Профіль") => (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest { Name = name, Email = Guid.NewGuid()+"@example.test", Password = "profile-password123!" })).Content.ReadFromJsonAsync<AuthResponse>())!;
    private static void SignIn(HttpClient client, AuthResponse auth) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
    [SqlFact]
    public async Task ProfileValidatesCurrentPasswordUniqueEmailAndConcurrentVersions()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString); using var c=app.CreateClient(); var auth=await Register(c); var other=await Register(c); SignIn(c,auth);
        var request=new UpdateProfileRequest{Name="Нове ім’я",Email=auth.User.Email,ExpectedVersion=auth.User.Version!};
        var saved=(await (await c.PutAsJsonAsync("/api/profile",request)).Content.ReadFromJsonAsync<UserDto>())!;
        Assert.Equal("Нове ім’я",saved.Name); Assert.NotEqual(auth.User.Version,saved.Version);
        Assert.Equal(HttpStatusCode.Conflict,(await c.PutAsJsonAsync("/api/profile",request)).StatusCode);
        request.ExpectedVersion=saved.Version!; request.Email="changed-"+auth.User.Email;
        Assert.Equal(HttpStatusCode.BadRequest,(await c.PutAsJsonAsync("/api/profile",request)).StatusCode);
        request.CurrentPassword="profile-password123!"; request.Email=other.User.Email;
        Assert.Equal(HttpStatusCode.Conflict,(await c.PutAsJsonAsync("/api/profile",request)).StatusCode);
        request.Email="changed-"+auth.User.Email; var response=await c.PutAsJsonAsync("/api/profile",request);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var login=await c.PostAsJsonAsync("/api/auth/login",new LoginRequest{Email=request.Email,Password="profile-password123!"});Assert.Equal(HttpStatusCode.OK,login.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await c.PutAsJsonAsync("/api/profile/"+other.User.Id,request)).StatusCode);
    }
    [SqlFact]
    public async Task PasswordChangeRevokesOldSessionsAndReturnsUsableNewSession()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var c=app.CreateClient();var auth=await Register(c);SignIn(c,auth);
        var request=new ChangePasswordRequest{CurrentPassword="wrong",NewPassword="new-password123!",ConfirmPassword="new-password123!",ExpectedVersion=auth.User.Version!};
        Assert.Equal(HttpStatusCode.BadRequest,(await c.PutAsJsonAsync("/api/profile/password",request)).StatusCode);
        request.CurrentPassword="profile-password123!";request.ConfirmPassword="mismatch";Assert.Equal(HttpStatusCode.BadRequest,(await c.PutAsJsonAsync("/api/profile/password",request)).StatusCode);
        request.ConfirmPassword=request.NewPassword;var response=await c.PutAsJsonAsync("/api/profile/password",request);Assert.Equal(HttpStatusCode.OK,response.StatusCode);var updated=(await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.Equal(HttpStatusCode.Unauthorized,(await c.GetAsync("/api/auth/me")).StatusCode);SignIn(c,updated);Assert.Equal(HttpStatusCode.OK,(await c.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await c.PostAsJsonAsync("/api/auth/login",new LoginRequest{Email=auth.User.Email,Password="profile-password123!"})).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await c.PostAsJsonAsync("/api/auth/login",new LoginRequest{Email=auth.User.Email,Password=request.NewPassword})).StatusCode);
    }
    [SqlFact]
    public async Task AvatarRequiresOwnershipSharedMembershipAndCurrentVersion()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var owner=app.CreateClient();using var peer=app.CreateClient();using var stranger=app.CreateClient();var a=await Register(owner);var b=await Register(peer);var z=await Register(stranger);SignIn(owner,a);SignIn(peer,b);SignIn(stranger,z);
        using var image=new ImageMagick.MagickImage(ImageMagick.MagickColors.Teal,120,120);var bytes=image.ToByteArray(ImageMagick.MagickFormat.Png);
        async Task<HttpResponseMessage> Upload(byte[] data,string version){using var form=new MultipartFormDataContent();form.Add(new StringContent(version),"ExpectedVersion");form.Add(new ByteArrayContent(data),"File","photo.png");return await owner.PutAsync("/api/profile/avatar",form);}
        var saved=await Upload(bytes,a.User.Version!);Assert.Equal(HttpStatusCode.OK,saved.StatusCode);var user=(await saved.Content.ReadFromJsonAsync<UserDto>())!;Assert.True(user.HasAvatar);
        Assert.Equal(HttpStatusCode.Conflict,(await Upload(bytes,a.User.Version!)).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType,(await Upload([1,2,3],user.Version!)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await peer.GetAsync($"/api/users/{a.User.Id}/avatar")).StatusCode);
        var board=(await (await owner.PostAsJsonAsync("/api/boards",new CreateBoardRequest{Name="Аватари"})).Content.ReadFromJsonAsync<BoardDetailsDto>())!;
        Assert.Equal(HttpStatusCode.Created,(await owner.PostAsJsonAsync($"/api/boards/{board.Id}/members",new AddMemberRequest{Email=b.User.Email,ExpectedBoardVersion=board.Version})).StatusCode);
        var photo=await peer.GetAsync($"/api/users/{a.User.Id}/avatar");Assert.Equal(HttpStatusCode.OK,photo.StatusCode);Assert.Contains("no-store",photo.Headers.CacheControl!.ToString());Assert.Equal("image/png",photo.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound,(await stranger.GetAsync($"/api/users/{a.User.Id}/avatar")).StatusCode);
        var current=(await owner.GetFromJsonAsync<BoardDetailsDto>($"/api/boards/{board.Id}"))!;await owner.DeleteAsync($"/api/boards/{board.Id}/members/{b.User.Id}?expectedBoardVersion="+Uri.EscapeDataString(current.Version));Assert.Equal(HttpStatusCode.NotFound,(await peer.GetAsync($"/api/users/{a.User.Id}/avatar")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await owner.DeleteAsync("/api/profile/avatar?expectedVersion="+Uri.EscapeDataString(user.Version!))).StatusCode);Assert.Equal(HttpStatusCode.NotFound,(await owner.GetAsync($"/api/users/{a.User.Id}/avatar")).StatusCode);
    }
}
