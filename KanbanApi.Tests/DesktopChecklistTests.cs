using System.Net;
using System.Net.Http.Json;
using Curs.Services;
using Curs.ViewModels;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace KanbanApi.Tests;
[Collection("sql")]
public sealed class DesktopChecklistTests(SqlFixture fixture)
{
    private static ClientServices Services(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app,KanbanApi.Domain.User user,HttpMessageHandler? transport=null)
    {var s=new ClientServices(new Uri("http://localhost/"),transport:transport??app.Server.CreateHandler());var u=new UserDto(user.Id,user.Email,user.Name);s.Session.SignIn(app.Services.GetRequiredService<JwtService>().Issue(u).AccessToken,u);return s;}
    [SqlFact]
    public async Task NewCardChecklistSavesProgressOrderAndDiscardDoesNotPersistDraft()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new ChecklistTests(fixture).Seed();using var services=Services(app,g.User);using var vm=new BoardContentViewModel(services);await vm.OpenAsync(g.Board.Id);vm.NewCard(vm.Columns.Single());vm.Title="Чек-лист UI";
        foreach(var text in new[]{"Перший","Другий","Третій"}){vm.NewChecklistText=text;vm.AddChecklistItemCommand.Execute(null);}
        vm.ChecklistItems[0].IsCompleted=true;vm.ChecklistItems[2].IsCompleted=true;vm.MoveChecklistItem(vm.ChecklistItems[2].Id,vm.ChecklistItems[0].Id,false);Assert.Equal("2 з 3",vm.ChecklistLabel);await vm.SaveCardAsync();Assert.Empty(vm.Error);var id=vm.EditingCardId!.Value;var tile=vm.Columns.Single().Cards.Single();Assert.True(tile.HasChecklist);Assert.Equal("2 з 3",tile.ChecklistLabel);
        vm.ChecklistItems[0].Title="Не зберігати";vm.ChecklistItems[1].DeleteCommand.Execute(null);vm.CloseEditorCommand.Execute(null);Assert.True(vm.DiscardChangesVisible);vm.DiscardEditorCommand.Execute(null);Assert.Empty(vm.ChecklistItems);
        await vm.OpenCardAsync(id);Assert.Equal(new[]{"Третій","Перший","Другий"},vm.ChecklistItems.Select(i=>i.Title));Assert.Equal("2 з 3",vm.ChecklistLabel);
    }
    [SqlFact]
    public async Task RemoteConflictKeepsLocalChecklistAndDoesNotSilentlyOverwriteServer()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new ChecklistTests(fixture).Seed();using var c=ChecklistTests.Client(app,g.User);var card=await ChecklistTests.Create(c,g.Board.Id,g.Col.Id,ChecklistTests.Items());using var s=Services(app,g.User);using var vm=new BoardContentViewModel(s);await vm.OpenAsync(g.Board.Id);await vm.OpenCardAsync(card.Card.Id);vm.ChecklistItems[0].Title="Моя чернетка";vm.ChecklistItems[0].IsCompleted=false;
        var remote=ChecklistTests.Items(1);var r=await c.PutAsJsonAsync($"/api/cards/{card.Card.Id}",await ChecklistTests.Body(c,g.Board.Id,card,remote));r.EnsureSuccessStatusCode();await vm.PollAsync();Assert.True(vm.RemoteChangesPending);await vm.SaveCardAsync();Assert.Contains("змін",vm.Error,StringComparison.OrdinalIgnoreCase);Assert.Equal(5,vm.ChecklistItems.Count);Assert.Equal("Моя чернетка",vm.ChecklistItems[0].Title);var stored=(await c.GetFromJsonAsync<CardDetailsDto>($"/api/cards/{card.Card.Id}"))!;Assert.Single(stored.Checklist!);Assert.Equal(remote[0].Id,stored.Checklist![0].Id);
    }
    private sealed class Delayed(HttpMessageHandler inner):DelegatingHandler(inner)
    {
        public TaskCompletionSource Reached{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Reply{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t)
        {if(r.Method==HttpMethod.Put&&r.RequestUri!.AbsolutePath.StartsWith("/api/cards/")){Reached.TrySetResult();return Reply.Task;}return base.SendAsync(r,t);}
    }
    [SqlFact]
    public async Task LogoutDuringSaveErasesChecklistAndLateReplyCannotRestoreIt()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new ChecklistTests(fixture).Seed();using var c=ChecklistTests.Client(app,g.User);var card=await ChecklistTests.Create(c,g.Board.Id,g.Col.Id,ChecklistTests.Items());var transport=new Delayed(app.Server.CreateHandler());using var s=Services(app,g.User,transport);using var vm=new BoardContentViewModel(s);await vm.OpenAsync(g.Board.Id);await vm.OpenCardAsync(card.Card.Id);vm.ChecklistItems[0].Title="Приватна чернетка";var saving=vm.SaveCardAsync();await transport.Reached.Task;s.Session.Clear();transport.Reply.SetResult(new(HttpStatusCode.OK){Content=JsonContent.Create(card)});await saving;Assert.Empty(vm.ChecklistItems);Assert.Empty(vm.NewChecklistText);Assert.Empty(vm.Columns);Assert.False(vm.EditorVisible);Assert.Null(s.Session.User);
    }
}
