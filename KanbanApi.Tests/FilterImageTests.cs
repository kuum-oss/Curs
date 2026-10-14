using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ImageMagick;
using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KanbanApi.Tests;

[Collection("sql")]
public sealed class FilterImageTests(SqlFixture fixture)
{
    private async Task<(User User, Board Board, BoardColumn Column, Card Card)> Seed()
    {
        await using var db=fixture.CreateContext();var user=new User{Email=Guid.NewGuid()+"@example.test",Name="Фото",PasswordHash="unused"};user.NormalizedEmail=user.Email.ToUpperInvariant();var board=new Board{Name="Фільтри та фото"};var col=new BoardColumn{BoardId=board.Id,Name="Колонка"};var card=new Card{ColumnId=col.Id,CreatedById=user.Id,Title="Завдання_100%",Description="Опис",Priority=CardPriority.High,DueDate=new(2026,10,14),AssigneeId=user.Id};
        db.AddRange(user,board,col,card,new BoardMember{BoardId=board.Id,UserId=user.Id,Role=BoardRole.Admin});await db.SaveChangesAsync();return(user,board,col,card);
    }
    private static HttpClient Client(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app,User user)
    {var c=app.CreateClient();c.DefaultRequestHeaders.Authorization=new("Bearer",app.Services.GetRequiredService<JwtService>().Issue(new(user.Id,user.Email,user.Name)).AccessToken);return c;}
    private static byte[] Image(MagickFormat format,uint w=900,uint h=600)
    {using var image=new MagickImage(MagickColors.CornflowerBlue,w,h);image.Strip();return image.ToByteArray(format);}
    private static async Task<string> Version(HttpClient c,Guid b)=>(await c.GetFromJsonAsync<BoardDetailsDto>($"/api/boards/{b}"))!.Version;
    private static async Task<CardDetailsDto> Card(HttpClient c,Guid id)=>(await c.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{id}"))!;
    private static async Task<HttpResponseMessage> Put(HttpClient c,Guid board,Guid card,byte[] bytes,string? version=null,string? boardVersion=null,string name="cover.bin")
    {
        using var form=new MultipartFormDataContent();form.Add(new StringContent(boardVersion??await Version(c,board)),"ExpectedBoardVersion");form.Add(new StringContent(version??(await Card(c,card)).Card.Version),"ExpectedVersion");var file=new ByteArrayContent(bytes);file.Headers.ContentType=new("application/octet-stream");form.Add(file,"File",name);return await c.PutAsync($"/api/cards/{card}/image",form);
    }
    private static async Task<PagedResult<CardSummaryDto>> Page(HttpClient c,Guid b,string query="")=>(await c.GetFromJsonAsync<PagedResult<CardSummaryDto>>($"/api/boards/{b}/cards?"+query))!;
    [SqlFact]
    public async Task CombinedFiltersAreCaseInsensitiveLiteralAndPreservePositionsAcrossPages()
    {
        var s=await Seed();using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var c=Client(app,s.User);await using var db=fixture.CreateContext();var tag=new Tag{BoardId=s.Board.Id,Name="Тег",NormalizedName="ТЕГ"};db.Tags.Add(tag);db.CardTags.Add(new(){CardId=s.Card.Id,TagId=tag.Id});
        var match=new Card{ColumnId=s.Column.Id,CreatedById=s.User.Id,AssigneeId=s.User.Id,Title="Інше ЗАВДАННЯ_100%",Priority=CardPriority.High,DueDate=new(2026,10,14),Position=1};db.Cards.Add(match);db.CardTags.Add(new(){CardId=match.Id,TagId=tag.Id});db.Cards.Add(new(){ColumnId=s.Column.Id,CreatedById=s.User.Id,Title="Завданняx100z",Position=2,Priority=CardPriority.High,DueDate=new(2026,10,14)});await db.SaveChangesAsync();
        var q=$"search={Uri.EscapeDataString("завдання_100%")}&priority=High&tagId={tag.Id}&assigneeId={s.User.Id}&deadline=Today&today=2026-10-14&limit=1";var a=await Page(c,s.Board.Id,q);var b=await Page(c,s.Board.Id,q+"&offset=1");Assert.Equal(2,a.TotalCount);Assert.Equal(s.Card.Id,Assert.Single(a.Items).Id);Assert.Equal(match.Id,Assert.Single(b.Items).Id);Assert.Equal(a.BoardVersion,b.BoardVersion);Assert.Equal(1,b.Items[0].Position);Assert.Equal(3,(await Page(c,s.Board.Id)).TotalCount);
        Assert.Empty((await Page(c,s.Board.Id,q.Replace("High","Critical"))).Items);Assert.Empty((await Page(c,s.Board.Id,"tagId="+Guid.NewGuid())).Items);
    }
    [SqlFact]
    public async Task DeadlineBoundariesExcludeUndatedCardsAndInvalidQueriesFail()
    {
        var s=await Seed();using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var c=Client(app,s.User);await using var db=fixture.CreateContext();
        foreach(var pair in new[]{("Минуле",(DateOnly?)new(2026,10,13)),("Майбутнє",(DateOnly?)new(2026,10,15)),("Без дати",(DateOnly?)null)})db.Cards.Add(new(){ColumnId=s.Column.Id,CreatedById=s.User.Id,Title=pair.Item1,DueDate=pair.Item2,Position=db.Cards.Local.Count+1});await db.SaveChangesAsync();
        Assert.Equal("Минуле",Assert.Single((await Page(c,s.Board.Id,"deadline=Overdue&today=2026-10-14")).Items).Title);Assert.Equal(s.Card.Id,Assert.Single((await Page(c,s.Board.Id,"deadline=Today&today=2026-10-14")).Items).Id);Assert.Equal("Майбутнє",Assert.Single((await Page(c,s.Board.Id,"deadline=Upcoming&today=2026-10-14")).Items).Title);
        foreach(var q in new[]{"deadline=Today","deadline=8&today=2026-10-14","priority=8","today=invalid","search="+new string('x',201),"offset=-1"})Assert.Equal(HttpStatusCode.BadRequest,(await c.GetAsync($"/api/boards/{s.Board.Id}/cards?"+q)).StatusCode);
        using var anonymous=app.CreateClient();Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync($"/api/boards/{s.Board.Id}/cards?search=secret")).StatusCode);
    }
    [SqlFact]
    public async Task AllFormatsReplacePersistThumbnailAndDeleteWithoutChangingCardFields()
    {
        var s=await Seed();using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var c=Client(app,s.User);Assert.Equal(HttpStatusCode.NotFound,(await c.GetAsync($"/api/cards/{s.Card.Id}/image")).StatusCode);
        foreach(var format in new[]{MagickFormat.Jpeg,MagickFormat.Png,MagickFormat.WebP})
        {
            var before=await Card(c,s.Card.Id);var bv=await Version(c,s.Board.Id);var data=Image(format);var response=await Put(c,s.Board.Id,s.Card.Id,data,name:"incorrect.txt");Assert.Equal(HttpStatusCode.OK,response.StatusCode);var versions=(await response.Content.ReadFromJsonAsync<ImageMutationDto>())!;Assert.NotEqual(before.Card.Version,versions.Version);Assert.NotEqual(bv,versions.BoardVersion);
            var original=await c.GetAsync($"/api/cards/{s.Card.Id}/image");Assert.Equal(data,await original.Content.ReadAsByteArrayAsync());Assert.Contains("no-store",original.Headers.CacheControl!.ToString());var thumb=await c.GetAsync($"/api/cards/{s.Card.Id}/image/thumbnail");Assert.Equal("image/png",thumb.Content.Headers.ContentType!.MediaType);using var decoded=new MagickImage(await thumb.Content.ReadAsByteArrayAsync());Assert.Equal(480u,decoded.Width);Assert.Equal(320u,decoded.Height);
            var card=await Card(c,s.Card.Id);Assert.True(card.Card.HasImage);Assert.Equal(before.Description,card.Description);Assert.Equal(before.Card.Title,card.Card.Title);Assert.Equal(before.Card.Assignee,card.Card.Assignee);Assert.Equal(versions.Version,card.Card.Version);
            await using var db=fixture.CreateContext();Assert.Single(await db.CardImages.Where(i=>i.CardId==s.Card.Id).ToListAsync());
        }
        var current=await Card(c,s.Card.Id);Assert.Equal(HttpStatusCode.NoContent,(await c.DeleteAsync($"/api/cards/{s.Card.Id}/image?expectedVersion={Uri.EscapeDataString(current.Card.Version)}&expectedBoardVersion={Uri.EscapeDataString(await Version(c,s.Board.Id))}")).StatusCode);Assert.False((await Card(c,s.Card.Id)).Card.HasImage);Assert.Equal(HttpStatusCode.NotFound,(await c.GetAsync($"/api/cards/{s.Card.Id}/image/thumbnail")).StatusCode);
    }
    [SqlFact]
    public async Task BadOversizedOrStaleUploadPreservesPreviousCoverAndVersions()
    {
        var s=await Seed();using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var c=Client(app,s.User);var good=Image(MagickFormat.Png);Assert.Equal(HttpStatusCode.OK,(await Put(c,s.Board.Id,s.Card.Id,good)).StatusCode);var card=await Card(c,s.Card.Id);var board=await Version(c,s.Board.Id);
        foreach(var bad in new[]{(new byte[0],HttpStatusCode.BadRequest),("not image"u8.ToArray(),HttpStatusCode.UnsupportedMediaType),(good[..^1],HttpStatusCode.BadRequest),(Image(MagickFormat.Gif),HttpStatusCode.UnsupportedMediaType),(new byte[ImageLimits.MaxBytes+1],HttpStatusCode.RequestEntityTooLarge)})
        {Assert.Equal(bad.Item2,(await Put(c,s.Board.Id,s.Card.Id,bad.Item1)).StatusCode);Assert.Equal(board,await Version(c,s.Board.Id));Assert.Equal(card.Card.Version,(await Card(c,s.Card.Id)).Card.Version);Assert.Equal(good,await c.GetByteArrayAsync($"/api/cards/{s.Card.Id}/image"));}
        Assert.Equal(HttpStatusCode.BadRequest,(await Put(c,s.Board.Id,s.Card.Id,Image(MagickFormat.Png,6000,4000))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await Put(c,s.Board.Id,s.Card.Id,good,Convert.ToBase64String(new byte[8]),board)).StatusCode);Assert.Equal(board,await Version(c,s.Board.Id));
        using var invalid=new MultipartFormDataContent();invalid.Add(new ByteArrayContent(good),"File","x.png");Assert.Equal(HttpStatusCode.BadRequest,(await c.PutAsync($"/api/cards/{s.Card.Id}/image",invalid)).StatusCode);
    }
    [SqlFact]
    public async Task ExactFiveMiBValidPngIsAcceptedAndOneExtraByteIsRejected()
    {
        var s=await Seed();using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var c=Client(app,s.User);
        var png=Image(MagickFormat.Png,10,10);var length=ImageLimits.MaxBytes-png.Length-12;var chunk=new byte[length+12];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(chunk,length);"tEXt"u8.CopyTo(chunk.AsSpan(4));"Comment\0"u8.CopyTo(chunk.AsSpan(8));chunk.AsSpan(16,length-8).Fill((byte)'x');
        uint crc=uint.MaxValue;foreach(var value in chunk.AsSpan(4,length+4)){crc^=value;for(var bit=0;bit<8;bit++)crc=(crc&1)!=0?(crc>>1)^0xedb88320u:crc>>1;}
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(length+8),~crc);
        var bytes=png[..^12].Concat(chunk).Concat(png[^12..]).ToArray();Assert.Equal(ImageLimits.MaxBytes,bytes.Length);
        Assert.Equal(HttpStatusCode.OK,(await Put(c,s.Board.Id,s.Card.Id,bytes)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,(await Put(c,s.Board.Id,s.Card.Id,bytes.Concat(new byte[]{0}).ToArray())).StatusCode);
        Assert.Equal(bytes,await c.GetByteArrayAsync($"/api/cards/{s.Card.Id}/image"));
    }
    [SqlFact]
    public async Task ImageRoutesHideForeignCardsAndOldTokensAfterRevocation()
    {
        var s=await Seed();var outsider=await Seed();using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var owner=Client(app,s.User);using var other=Client(app,outsider.User);using var anonymous=app.CreateClient();var image=Image(MagickFormat.WebP);await Put(owner,s.Board.Id,s.Card.Id,image);
        foreach(var route in new[]{"image","image/thumbnail"}){Assert.Equal(HttpStatusCode.NotFound,(await other.GetAsync($"/api/cards/{s.Card.Id}/{route}")).StatusCode);Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync($"/api/cards/{s.Card.Id}/{route}")).StatusCode);}
        var card=await Card(owner,s.Card.Id);var board=await Version(owner,s.Board.Id);Assert.Equal(HttpStatusCode.NotFound,(await Put(other,s.Board.Id,s.Card.Id,image,card.Card.Version,board)).StatusCode);
        var query=$"?expectedVersion={Uri.EscapeDataString(card.Card.Version)}&expectedBoardVersion={Uri.EscapeDataString(board)}";Assert.Equal(HttpStatusCode.NotFound,(await other.DeleteAsync($"/api/cards/{s.Card.Id}/image"+query)).StatusCode);
        await owner.PostAsJsonAsync($"/api/boards/{s.Board.Id}/members",new AddMemberRequest{Email=outsider.User.Email,ExpectedBoardVersion=board});await owner.PatchAsJsonAsync($"/api/boards/{s.Board.Id}/members/{outsider.User.Id}",new UpdateMemberPermissionsRequest{CanCreateCards=false,CanDeleteCards=false,ExpectedBoardVersion=await Version(owner,s.Board.Id)});Assert.Equal(HttpStatusCode.OK,(await Put(other,s.Board.Id,s.Card.Id,image)).StatusCode);
        await owner.DeleteAsync($"/api/boards/{s.Board.Id}/members/{outsider.User.Id}?expectedBoardVersion="+Uri.EscapeDataString(await Version(owner,s.Board.Id)));Assert.Equal(HttpStatusCode.NotFound,(await other.GetAsync($"/api/cards/{s.Card.Id}/image")).StatusCode);Assert.Equal(HttpStatusCode.NotFound,(await other.GetAsync($"/api/boards/{s.Board.Id}/cards?search=завдання")).StatusCode);
    }
    [SqlFact]
    public async Task LateImageFailureRollsBackCoverCardAndBoardVersion()
    {
        var s=await Seed();using var app=ApiFoundationTests.Factory(fixture.ConnectionString);using var c=Client(app,s.User);var png=Image(MagickFormat.Png);await Put(c,s.Board.Id,s.Card.Id,png);var before=await Card(c,s.Card.Id);var version=await Version(c,s.Board.Id);
        await using var db=fixture.CreateContext();await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER Ph06ImageFailure ON [Cards] AFTER UPDATE AS BEGIN THROW 51000, 'Synthetic PH06 image failure', 1; END;");
        try{Assert.Equal(HttpStatusCode.InternalServerError,(await Put(c,s.Board.Id,s.Card.Id,Image(MagickFormat.Jpeg))).StatusCode);Assert.Equal(version,await Version(c,s.Board.Id));Assert.Equal(before.Card.Version,(await Card(c,s.Card.Id)).Card.Version);Assert.Equal(png,await c.GetByteArrayAsync($"/api/cards/{s.Card.Id}/image"));}
        finally{await db.Database.ExecuteSqlRawAsync("DROP TRIGGER Ph06ImageFailure;");}
    }
}
