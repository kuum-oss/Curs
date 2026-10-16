using Curs.Services;
using Curs.ViewModels;
using KanbanApi.Domain;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KanbanApi.Tests;

[Collection("sql")]
public sealed class DesktopImageImportTests(SqlFixture fixture)
{
    private async Task<(User User,Board Board,Card First,Card Second)> Seed()
    {
        var email=Guid.NewGuid()+"@example.test";
        var user=new User{Email=email,NormalizedEmail=email.ToUpperInvariant(),Name="Фото",PasswordHash="unused"};
        var board=new Board{Name="Файли",OwnerId=user.Id};var column=new BoardColumn{BoardId=board.Id,Name="План"};
        var first=new Card{ColumnId=column.Id,CreatedById=user.Id,Title="Перша"};
        var second=new Card{ColumnId=column.Id,CreatedById=user.Id,Title="Друга",Position=1};
        await using var db=fixture.CreateContext();db.AddRange(user,board,column,first,second,new BoardMember{BoardId=board.Id,UserId=user.Id,Role=BoardRole.Admin});await db.SaveChangesAsync();
        return(user,board,first,second);
    }
    private static void SignIn(ClientServices services,JwtService jwt,User user)
    {
        var dto=new UserDto(user.Id,user.Email,user.Name);
        services.Session.SignIn(jwt.Issue(dto).AccessToken,dto);
    }
    [SqlFact]
    public async Task BoardFileDropChangesOnlyTargetCoverAndRejectsOversizeWithoutOpeningEditor()
    {
        var s=await Seed();using var app=ApiFoundationTests.Factory(fixture.ConnectionString);
        using var services=new ClientServices(new Uri("http://localhost/"),transport:app.Server.CreateHandler());SignIn(services,app.Services.GetRequiredService<JwtService>(),s.User);
        using var view=new BoardContentViewModel(services);await view.OpenAsync(s.Board.Id);
        var bytes=ImageProcessorTests.ScreenshotLikePng();await view.ImportCoverAsync(s.First.Id,_=>Task.FromResult((bytes,"screenshot.png")));
        Assert.Empty(view.Error);Assert.False(view.EditorVisible);
        Assert.True(view.Columns[0].Cards.Single(c=>c.Card.Id==s.First.Id).HasImage);
        Assert.False(view.Columns[0].Cards.Single(c=>c.Card.Id==s.Second.Id).HasImage);
        await view.ImportCoverAsync(s.First.Id,_=>Task.FromResult((new byte[ImageLimits.MaxBytes+1],"large.png")));
        Assert.Contains("5 МіБ",view.Error);
        await using var db=fixture.CreateContext();var image=await db.CardImages.SingleAsync(i=>i.CardId==s.First.Id);
        Assert.Equal(bytes,image.OriginalData);Assert.False(await db.CardImages.AnyAsync(i=>i.CardId==s.Second.Id));
    }
    [SqlFact]
    public async Task EditorDropKeepsUnsavedFieldsAndNeverRedirectsToAnotherCard()
    {
        Avalonia.Skia.SkiaPlatform.Initialize();
        var s=await Seed();using var app=ApiFoundationTests.Factory(fixture.ConnectionString);
        using var services=new ClientServices(new Uri("http://localhost/"),transport:app.Server.CreateHandler());SignIn(services,app.Services.GetRequiredService<JwtService>(),s.User);
        using var view=new BoardContentViewModel(services);await view.OpenAsync(s.Board.Id);await view.OpenCardAsync(s.First.Id);
        view.Title="Незбережена назва";view.Description="Чернетка опису";var wrongTargetRead=false;
        await view.ImportCoverAsync(s.Second.Id,_=>{wrongTargetRead=true;return Task.FromResult((ImageProcessorTests.ScreenshotLikePng(),"wrong.png"));});
        Assert.False(wrongTargetRead);
        var bytes=ImageProcessorTests.ScreenshotLikePng();await view.ImportCoverAsync(s.First.Id,_=>Task.FromResult((bytes,"correct.png")));
        Assert.Empty(view.Error);Assert.True(view.HasCover);Assert.True(view.EditorVisible);Assert.Equal("Незбережена назва",view.Title);Assert.Equal("Чернетка опису",view.Description);
        await using var db=fixture.CreateContext();Assert.Equal("Перша",(await db.Cards.FindAsync(s.First.Id))!.Title);
        Assert.Equal(bytes,(await db.CardImages.SingleAsync(i=>i.CardId==s.First.Id)).OriginalData);Assert.False(await db.CardImages.AnyAsync(i=>i.CardId==s.Second.Id));
    }
    [SqlFact]
    public async Task LogoutAndNewSessionDuringFileReadCannotUploadOldSelection()
    {
        var s=await Seed();using var app=ApiFoundationTests.Factory(fixture.ConnectionString);
        using var services=new ClientServices(new Uri("http://localhost/"),transport:app.Server.CreateHandler());var jwt=app.Services.GetRequiredService<JwtService>();SignIn(services,jwt,s.User);
        using var view=new BoardContentViewModel(services);await view.OpenAsync(s.Board.Id);
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish=new TaskCompletionSource<(byte[] Bytes,string Name)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending=view.ImportCoverAsync(s.First.Id,_=>{reached.SetResult();return finish.Task;});await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        services.Session.Clear();SignIn(services,jwt,s.User);await view.OpenAsync(s.Board.Id);
        finish.SetResult((ImageProcessorTests.ScreenshotLikePng(),"late.png"));await pending;
        Assert.Empty(view.Error);Assert.Equal(s.Board.Id,view.BoardId);Assert.False(view.IsBusy);
        await using var db=fixture.CreateContext();Assert.False(await db.CardImages.AnyAsync(i=>i.CardId==s.First.Id));
    }
}
