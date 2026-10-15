using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using KanbanContracts;
using Xunit;

namespace KanbanApi.Tests;

[Collection("sql")]
public sealed class SketchWorkspaceTests(SqlFixture fixture)
{
    private static async Task<AuthResponse> Register(HttpClient c)
    {
        var r=await c.PostAsJsonAsync("/api/auth/register",new RegisterRequest{Name="Учасник",Email=Guid.NewGuid()+"@example.test",Password="workspace-password123!"});r.EnsureSuccessStatusCode();
        var a=(await r.Content.ReadFromJsonAsync<AuthResponse>())!;c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",a.AccessToken);return a;
    }
    private static async Task<BoardDetailsDto> Board(HttpClient c,Guid id)=> (await c.GetFromJsonAsync<BoardDetailsDto>($"/api/boards/{id}"))!;
    private static async Task<BoardDetailsDto> Create(HttpClient c)=>(await (await c.PostAsJsonAsync("/api/boards",new CreateBoardRequest{Name="Ескіз"})).Content.ReadFromJsonAsync<BoardDetailsDto>())!;
    [SqlFact]
    public async Task RolesAreAtomicOwnerProtectedAndPersonalFavoritesAreIsolated()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var owner=app.CreateClient();using var member=app.CreateClient();var a=await Register(owner);var b=await Register(member);var board=await Create(owner);
        (await owner.PostAsJsonAsync($"/api/boards/{board.Id}/members",new AddMemberRequest{Email=b.User.Email,ExpectedBoardVersion=board.Version})).EnsureSuccessStatusCode();board=await Board(owner,board.Id);
        var settings=new UpdateMembersRequest{ExpectedBoardVersion=board.Version,Members=[new(){UserId=b.User.Id,Role=BoardRole.Admin},new(){UserId=a.User.Id,Role=BoardRole.Member}]};
        Assert.Equal(HttpStatusCode.Conflict,(await owner.PutAsJsonAsync($"/api/boards/{board.Id}/members",settings)).StatusCode);
        Assert.Equal(BoardRole.Member,(await Board(member,board.Id)).Role);Assert.Equal(board.Version,(await Board(owner,board.Id)).Version);
        settings.Members.RemoveAt(1);(await owner.PutAsJsonAsync($"/api/boards/{board.Id}/members",settings)).EnsureSuccessStatusCode();Assert.Equal(BoardRole.Admin,(await Board(member,board.Id)).Role);
        Assert.Equal(HttpStatusCode.Conflict,(await owner.PutAsJsonAsync($"/api/boards/{board.Id}/members",settings)).StatusCode);
        (await member.PutAsJsonAsync($"/api/boards/{board.Id}/favorite",new FavoriteRequest{Favorite=true})).EnsureSuccessStatusCode();Assert.True((await Board(member,board.Id)).IsFavorite);Assert.False((await Board(owner,board.Id)).IsFavorite);
        (await member.PostAsync($"/api/boards/{board.Id}/visit",null)).EnsureSuccessStatusCode();Assert.NotNull((await member.GetFromJsonAsync<List<BoardSummaryDto>>("/api/boards"))!.Single(x=>x.Id==board.Id).LastOpenedAtUtc);
        board=await Board(owner,board.Id);Assert.Equal(HttpStatusCode.Conflict,(await member.DeleteAsync($"/api/boards/{board.Id}/members/{a.User.Id}?expectedBoardVersion="+Uri.EscapeDataString(board.Version))).StatusCode);
    }
    [SqlFact]
    public async Task BoardCoverValidatesAccessVersionAndSurvivesArchiveRestore()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var c=app.CreateClient();using var other=app.CreateClient();await Register(c);await Register(other);var board=await Create(c);
        async Task<HttpResponseMessage> Upload(byte[] bytes,string version){using var form=new MultipartFormDataContent();form.Add(new StringContent(version),"ExpectedBoardVersion");form.Add(new ByteArrayContent(bytes),"File","cover.png");return await c.PutAsync($"/api/boards/{board.Id}/cover",form);}
        using var image=new ImageMagick.MagickImage(ImageMagick.MagickColors.Teal,240,160);var bytes=image.ToByteArray(ImageMagick.MagickFormat.Png);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType,(await Upload([1,2,3],board.Version)).StatusCode);
        (await Upload(bytes,board.Version)).EnsureSuccessStatusCode();Assert.Equal(HttpStatusCode.Conflict,(await Upload(bytes,board.Version)).StatusCode);board=await Board(c,board.Id);Assert.True(board.HasCover);
        var cover=await c.GetAsync($"/api/boards/{board.Id}/cover/thumbnail");cover.EnsureSuccessStatusCode();Assert.Equal("image/png",cover.Content.Headers.ContentType!.MediaType);Assert.Contains("no-store",cover.Headers.CacheControl!.ToString());Assert.Equal(HttpStatusCode.NotFound,(await other.GetAsync($"/api/boards/{board.Id}/cover")).StatusCode);
        (await c.PutAsJsonAsync($"/api/boards/{board.Id}/archive",new SetArchiveRequest{Archived=true,ExpectedBoardVersion=board.Version})).EnsureSuccessStatusCode();board=await Board(c,board.Id);Assert.True(board.IsArchived);Assert.True(board.HasCover);
        (await c.PutAsJsonAsync($"/api/boards/{board.Id}/archive",new SetArchiveRequest{Archived=false,ExpectedBoardVersion=board.Version})).EnsureSuccessStatusCode();board=await Board(c,board.Id);
        (await c.DeleteAsync($"/api/boards/{board.Id}/cover?expectedBoardVersion="+Uri.EscapeDataString(board.Version))).EnsureSuccessStatusCode();Assert.False((await Board(c,board.Id)).HasCover);
    }
    [SqlFact]
    public async Task CommentsArchiveOverviewAndProfileRefreshRespectVersionsAndMembership()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var c=app.CreateClient();using var peer=app.CreateClient();var a=await Register(c);var b=await Register(peer);var board=await Create(c);
        (await c.PostAsJsonAsync($"/api/boards/{board.Id}/members",new AddMemberRequest{Email=b.User.Email,ExpectedBoardVersion=board.Version})).EnsureSuccessStatusCode();board=await Board(c,board.Id);
        var col=(await (await c.PostAsJsonAsync($"/api/boards/{board.Id}/columns",new CreateColumnRequest{Name="Робота",ExpectedBoardVersion=board.Version})).Content.ReadFromJsonAsync<ColumnDto>())!;board=await Board(c,board.Id);
        var card=(await (await c.PostAsJsonAsync($"/api/boards/{board.Id}/cards",new CreateCardRequest{ColumnId=col.Id,Title="Картка",Description="Опис на прев’ю",AssigneeId=a.User.Id,ExpectedBoardVersion=board.Version})).Content.ReadFromJsonAsync<CardDetailsDto>())!;board=await Board(c,board.Id);
        var comment=(await (await c.PostAsJsonAsync($"/api/cards/{card.Card.Id}/comments",new CreateCommentRequest{Text="Чернетка",ExpectedBoardVersion=board.Version,ExpectedVersion=card.Card.Version})).Content.ReadFromJsonAsync<CommentDto>())!;board=await Board(c,board.Id);card=(await c.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{card.Card.Id}"))!;
        var edit=new UpdateCommentRequest{Text="Уточнення",ExpectedBoardVersion=board.Version,ExpectedVersion=card.Card.Version,ExpectedCommentVersion=comment.Version!};
        Assert.Equal(HttpStatusCode.Forbidden,(await peer.PutAsJsonAsync($"/api/cards/{card.Card.Id}/comments/{comment.Id}",edit)).StatusCode);
        var edited=await c.PutAsJsonAsync($"/api/cards/{card.Card.Id}/comments/{comment.Id}",edit);edited.EnsureSuccessStatusCode();Assert.NotNull((await edited.Content.ReadFromJsonAsync<CommentDto>())!.EditedAtUtc);
        var overview=(await c.GetFromJsonAsync<PagedResult<PersonalCardDto>>("/api/me/cards"))!;var row=overview.Items.Single(x=>x.Card.Id==card.Card.Id);Assert.Equal(1,row.Card.CommentCount);Assert.Equal("Опис на прев’ю",row.Card.Description);Assert.DoesNotContain((await peer.GetFromJsonAsync<PagedResult<PersonalCardDto>>("/api/me/cards"))!.Items,x=>x.Card.Id==card.Card.Id);
        board=await Board(c,board.Id);card=(await c.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{card.Card.Id}"))!;
        (await c.PutAsJsonAsync($"/api/cards/{card.Card.Id}/archive",new ArchiveCardRequest{Archived=true,ExpectedBoardVersion=board.Version,ExpectedVersion=card.Card.Version})).EnsureSuccessStatusCode();Assert.DoesNotContain((await c.GetFromJsonAsync<PagedResult<PersonalCardDto>>("/api/me/cards"))!.Items,x=>x.Card.Id==card.Card.Id);Assert.Contains((await c.GetFromJsonAsync<PagedResult<PersonalCardDto>>("/api/me/cards?archived=true&assignedOnly=false"))!.Items,x=>x.Card.Id==card.Card.Id);
        board=await Board(c,board.Id);var before=board.Version;(await c.PutAsJsonAsync("/api/profile",new UpdateProfileRequest{Name="Новий автор",Email=a.User.Email,ExpectedVersion=a.User.Version!})).EnsureSuccessStatusCode();Assert.NotEqual(before,(await Board(c,board.Id)).Version);
        Assert.Contains((await peer.GetFromJsonAsync<List<UserDto>>("/api/me/team"))!,x=>x.Id==a.User.Id&&x.Name=="Новий автор");board=await Board(c,board.Id);(await c.DeleteAsync($"/api/boards/{board.Id}/members/{b.User.Id}?expectedBoardVersion="+Uri.EscapeDataString(board.Version))).EnsureSuccessStatusCode();Assert.DoesNotContain((await peer.GetFromJsonAsync<List<UserDto>>("/api/me/team"))!,x=>x.Id==a.User.Id);
    }
}
