using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Curs.Services;
using Curs.ViewModels;
using KanbanApi.Infrastructure;
using KanbanContracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace KanbanApi.Tests;
[Collection("sql")]
public sealed class DesktopBackupTests(SqlFixture fixture)
{
    private static ClientServices Services(WebApplicationFactory<Program> app,KanbanApi.Domain.User user,HttpMessageHandler? transport=null)
    {var s=new ClientServices(new Uri("http://localhost"),transport:transport??app.Server.CreateHandler());var dto=new UserDto(user.Id,user.Email,user.Name);s.Session.SignIn(app.Services.GetRequiredService<JwtService>().Issue(dto).AccessToken,dto);return s;}
    private sealed class LostReply(HttpMessageHandler inner):DelegatingHandler(inner)
    {
        private bool lost;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t)
        {var response=await base.SendAsync(r,t);if(!lost&&r.RequestUri!.AbsolutePath=="/api/backups/restore"){lost=true;response.Dispose();throw new HttpRequestException("Lost committed restore response");}return response;}
    }
    [SqlFact]
    public async Task FilesPreviewLostReplyRetryAndOpenRestoreWorkWithoutDuplicateBoard()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var a=ChecklistTests.Client(app,g.Owner);await ChecklistTests.Create(a,g.Board.Id,g.Column.Id,ChecklistTests.Items());using var s=Services(app,g.Owner,new LostReply(app.Server.CreateHandler()));Guid? opened=null;using var vm=new BackupViewModel(s,id=>opened=id);await vm.Show();vm.SelectedBoard=vm.Boards.Single(x=>x.Id==g.Board.Id);byte[]? bytes=null;await vm.Save(false,(data,_)=>{bytes=data;return Task.FromResult(true);});Assert.NotNull(bytes);Assert.Contains("збережено",vm.Status);await vm.Load(_=>Task.FromResult<(string,byte[])?>( ("board.kanban.json",bytes!)));Assert.True(vm.HasDocument);Assert.Contains(g.Board.Name,vm.Preview);await vm.Restore();Assert.NotEmpty(vm.Error);Assert.False(vm.HasRestored);await vm.Restore();Assert.Empty(vm.Error);Assert.True(vm.HasRestored);vm.OpenRestoredCommand.Execute(null);Assert.NotNull(opened);Assert.NotEqual(g.Board.Id,opened);var boards=(await a.GetFromJsonAsync<IReadOnlyList<BoardSummaryDto>>("/api/boards"))!;Assert.Equal(2,boards.Count(x=>x.Name==g.Board.Name));await vm.Save(true,(data,_)=>{Assert.True(data.AsSpan().StartsWith(new byte[]{239,187,191}));return Task.FromResult(false);});Assert.Empty(vm.Status);s.Session.Clear();Assert.False(vm.HasDocument);Assert.Empty(vm.Boards);Assert.Empty(vm.Filename);
    }
    [SqlFact]
    public async Task InvalidFileDoesNotOfferRestoreAndLogoutCancelsLateFilePreview()
    {
        using var app=ApiFoundationTests.Factory(fixture.ConnectionString);var g=await new NotificationTests(fixture).Pair();using var s=Services(app,g.Owner);using var vm=new BackupViewModel(s,_=>{});await vm.Show();await vm.Load(_=>Task.FromResult<(string,byte[])?>( ("bad.json","{}"u8.ToArray())));Assert.False(vm.HasDocument);Assert.NotEmpty(vm.Error);Assert.False(vm.RestoreCommand.CanExecute(null));
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var reply=new TaskCompletionSource<(string,byte[])?>(TaskCreationOptions.RunContinuationsAsynchronously);var loading=vm.Load(_=>{reached.SetResult();return reply.Task;});await reached.Task;s.Session.Clear();reply.SetResult(("late.json","{}"u8.ToArray()));await loading;Assert.False(vm.HasDocument);Assert.Empty(vm.Error);Assert.Empty(vm.Filename);
    }
}
