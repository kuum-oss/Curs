using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Curs.Services;
using KanbanContracts;

namespace Curs.ViewModels;

public sealed partial class BoardListItemViewModel
{
    private Bitmap? cover;
    private bool loadingMedia;
    private readonly CancellationTokenSource lifetime=new();
    public Bitmap? Cover{get=>cover;private set{cover=value;Notify();Notify(nameof(HasCover));}}
    public bool HasCover=>Cover is not null;
    public string Counts=>$"{Board.CardCount} карток\n{Board.MemberCount} учасників";
    public string FavoriteIcon=>Board.IsFavorite?"★":"☆";
    public string ArchiveLabel=>Board.IsArchived?"Відновити дошку":"Архівувати дошку";
    public bool CanManage=>Board.Role==BoardRole.Admin;
    public ObservableCollection<AvatarViewModel> Avatars{get;}=[];
    public ActionCommand SettingsCommand{get;private set;}=null!;
    public ActionCommand FavoriteCommand{get;private set;}=null!;
    public ActionCommand ArchiveCommand{get;private set;}=null!;
    private void InitializeTile(BoardWorkspaceViewModel workspace)
    {
        foreach(var user in Board.Participants??[])Avatars.Add(new(user));
        SettingsCommand=new(()=>_=workspace.SettingsFor(Board.Id));FavoriteCommand=new(()=>_=workspace.FavoriteTile(Board));ArchiveCommand=new(()=>_=workspace.ArchiveTile(Board),()=>CanManage);
    }
    public async Task LoadMedia(ClientServices services)
    {
        foreach(var avatar in Avatars)_=avatar.Load(services);
        if(!Board.HasCover||Cover is not null||loadingMedia||lifetime.IsCancellationRequested)return;loadingMedia=true;var observed=services.Session.AccessToken;
        try{var bytes=await services.Api.BytesAsync($"api/boards/{Board.Id}/cover/thumbnail",lifetime.Token);lifetime.Token.ThrowIfCancellationRequested();if(observed==services.Session.AccessToken)Cover=new Bitmap(new MemoryStream(bytes));}catch(Exception){}finally{loadingMedia=false;}
    }
    public void Dispose(){lifetime.Cancel();var old=Cover;Cover=null;old?.Dispose();foreach(var avatar in Avatars)avatar.Dispose();}
}
public sealed record RoleOption(BoardRole Value,string Name);
public sealed partial class MemberViewModel
{
    private RoleOption? selectedRole;
    public AvatarViewModel Avatar{get;private set;}=null!;
    public RoleOption[] Roles{get;}=[new(BoardRole.Admin,"Адміністратор"),new(BoardRole.Member,"Учасник")];
    public RoleOption? SelectedRole{get=>selectedRole;set{selectedRole=value;if(value?.Value==BoardRole.Admin){CanCreateCards=true;CanDeleteCards=true;}Notify();Notify(nameof(CanChangeRights));}}
    public bool CanChangeRole=>CanManage&&!workspace.IsBusy;
    public bool CanChangeRights=>CanChangeRole&&SelectedRole?.Value!=BoardRole.Admin;
    private void InitializeMember(){Avatar=new(Member.User);selectedRole=Roles.First(r=>r.Value==Member.Role);}
    public MemberSettings Settings()=>new(){UserId=Member.User.Id,Role=SelectedRole?.Value??Member.Role,CanCreateCards=CanCreateCards,CanDeleteCards=CanDeleteCards};
}
public sealed class OverviewCardViewModel : IDisposable
{
    public PersonalCardDto Row{get;}
    public CardItemViewModel Card{get;}
    public string BoardName=>Row.BoardName;
    public ActionCommand OpenCommand{get;}
    public ActionCommand RestoreCommand{get;}
    public bool CanRestore{get;}
    public OverviewCardViewModel(PersonalCardDto row,BoardWorkspaceViewModel owner)
    {
        Row=row;Card=new(row.Card,owner.Content);OpenCommand=new(()=>_=owner.OpenOverview(row));RestoreCommand=new(()=>_=owner.RestoreCard(row));CanRestore=row.Card.IsArchived;
    }
    public void Dispose()=>Card.Dispose();
}
public sealed partial class BoardWorkspaceViewModel
{
    private string page="boards",boardSearch="";
    private bool membersVisible,settingsVisible,newBoardVisible;
    private Action? pendingNavigation;
    private bool navigationConfirmation;
    private AvatarViewModel? currentAvatar;
    public ProfileViewModel Profile{get;private set;}=null!;
    public UserDto? CurrentUser=>services.Session.User;
    public AvatarViewModel? CurrentAvatar{get=>currentAvatar;private set{currentAvatar?.Dispose();currentAvatar=value;Notify();}}
    public string UserName=>CurrentUser?.Name??"";
    public string Page{get=>page;private set{page=value;NotifyPresentation();}}
    public bool BoardsVisible=>Page is "boards" or "archive" or "favorites";
    public bool BoardVisible=>Page=="board"&&ShowBoard;
    public bool TasksVisible=>Page=="tasks";
    public bool TeamVisible=>Page=="team";
    public bool ProfileVisible=>Page=="profile";
    public bool IsArchive=>Page=="archive";
    public string PageTitle=>Page switch{"archive"=>"Архів","favorites"=>"Обране","tasks"=>"Особисті задачі","team"=>"Команда",_=>"Мої дошки"};
    public string PageSubtitle=>Page switch{"archive"=>"Відновлюйте дошки та картки","favorites"=>"Дошки, які ви позначили зірочкою",_=>"Ваші спільні та особисті дошки"};
    public string BoardSearch{get=>boardSearch;set{boardSearch=value;Notify();RefreshTiles();}}
    public ObservableCollection<BoardListItemViewModel> VisibleBoards{get;}=[];
    public ObservableCollection<BoardListItemViewModel> RecentBoards{get;}=[];
    public ObservableCollection<OverviewCardViewModel> OverviewCards{get;}=[];
    public ObservableCollection<AvatarViewModel> Team{get;}=[];
    public bool NoVisibleBoards=>VisibleBoards.Count==0;
    public bool NoOverviewCards=>OverviewCards.Count==0;
    public bool MembersVisible{get=>membersVisible;private set{membersVisible=value;Notify();}}
    public bool SettingsVisible{get=>settingsVisible;private set{settingsVisible=value;Notify();}}
    public bool NewBoardVisible{get=>newBoardVisible;private set{newBoardVisible=value;Notify();}}
    public bool NavigationConfirmation{get=>navigationConfirmation;private set{navigationConfirmation=value;Notify();}}
    public bool HasDraft=>Content.EditorVisible||Content.HasColumn||SettingsVisible||MembersVisible||NewBoardVisible||ProfileVisible&&Profile.HasChanges;
    public string FavoriteIcon=>selected?.IsFavorite==true?"★":"☆";
    public string ArchiveLabel=>selected?.IsArchived==true?"Відновити дошку":"Архівувати дошку";
    public string MembersLabel=>$"Учасники ({Members.Count})";
    public ActionCommand BoardsCommand{get;private set;}=null!;
    public ActionCommand TasksCommand{get;private set;}=null!;
    public ActionCommand TeamCommand{get;private set;}=null!;
    public ActionCommand ArchiveCommand{get;private set;}=null!;
    public ActionCommand FavoritesCommand{get;private set;}=null!;
    public ActionCommand ProfileCommand{get;private set;}=null!;
    public ActionCommand LogoutCommand{get;private set;}=null!;
    public ActionCommand NewBoardCommand{get;private set;}=null!;
    public ActionCommand MembersCommand{get;private set;}=null!;
    public ActionCommand SettingsCommand{get;private set;}=null!;
    public ActionCommand CloseDialogCommand{get;private set;}=null!;
    public ActionCommand SaveMembersCommand{get;private set;}=null!;
    public ActionCommand FavoriteCommand{get;private set;}=null!;
    public ActionCommand ArchiveBoardCommand{get;private set;}=null!;
    public ActionCommand RemoveBoardCoverCommand{get;private set;}=null!;
    public ActionCommand DiscardNavigationCommand{get;private set;}=null!;
    public ActionCommand StayCommand{get;private set;}=null!;
    private void InitializePresentation()
    {
        Profile=new(services);
        BoardsCommand=new(()=>Navigate(()=>_=ShowPage("boards")));TasksCommand=new(()=>Navigate(()=>_=ShowPage("tasks")));TeamCommand=new(()=>Navigate(()=>_=ShowPage("team")));
        ArchiveCommand=new(()=>Navigate(()=>_=ShowPage("archive")));FavoritesCommand=new(()=>Navigate(()=>_=ShowPage("favorites")));ProfileCommand=new(()=>Navigate(()=>_=ShowProfile()));
        LogoutCommand=new(()=>Navigate(()=>services.Auth.Logout()));NewBoardCommand=new(()=>NewBoardVisible=true);
        MembersCommand=new(()=>_=ShowMembers());SettingsCommand=new(()=>_=ShowSettings());
        CloseDialogCommand=new(()=>{if(!IsBusy)CloseDialogs();});SaveMembersCommand=new(()=>_=SaveMembers(),()=>!IsBusy&&IsAdmin);
        FavoriteCommand=new(()=>_=FavoriteSelected());ArchiveBoardCommand=new(()=>_=ArchiveSelected());RemoveBoardCoverCommand=new(()=>_=RemoveBoardCover());
        StayCommand=new(()=>{pendingNavigation=null;NavigationConfirmation=false;});DiscardNavigationCommand=new(()=>{var next=pendingNavigation;pendingNavigation=null;NavigationConfirmation=false;CloseDialogs();Content.CloseEditor();next?.Invoke();});
        RefreshIdentity();
    }
    private void Navigate(Action next)
    {
        if(IsBusy||Content.IsBusy||Profile.IsBusy)return;
        if(HasDraft){pendingNavigation=next;NavigationConfirmation=true;}else next();
    }
    private async Task ShowProfile(){Content.Clear();selected=null;Page="profile";await Profile.Open();}
    private async Task ShowPage(string next)
    {
        Content.Clear();selected=null;CloseDialogs();Page=next;NotifyBoard();
        await LoadAsync();
        if(next is "tasks" or "archive")await LoadOverview(next=="archive");
        if(next=="team")await RunAsync(async t=>{foreach(var old in Team)old.Dispose();Team.Clear();foreach(var user in await services.Content.Team(t)){var avatar=new AvatarViewModel(user);Team.Add(avatar);_=avatar.Load(services);}});
    }
    public Task OpenVisualAsync(Guid id)
    {
        if(HasDraft){Navigate(()=>_=OpenVisualCore(id));return Task.CompletedTask;}return OpenVisualCore(id);
    }
    private async Task OpenVisualCore(Guid id)
    {
        await OpenAsync(id);if(selected?.Id!=id)return;Page="board";CloseDialogs();await Content.OpenAsync(id);
        if(Content.BoardId!=id)return;
        try{await services.Boards.Visit(id,CancellationToken.None);}catch(Exception){}
        foreach(var member in Members)_=member.Avatar.Load(services);
        await LoadAsync();
    }
    public async Task SettingsFor(Guid id){await OpenVisualAsync(id);if(selected?.Id==id&&IsAdmin)SettingsVisible=true;}
    private async Task ShowSettings(){if(selected is null||!IsAdmin)return;await RefreshAsync();SettingsVisible=true;}
    private async Task ShowMembers()
    {
        if(selected is null)return;await RefreshAsync();foreach(var member in Members)_=member.Avatar.Load(services);MembersVisible=true;Notify(nameof(MembersLabel));
    }
    private void CloseDialogs(){MembersVisible=false;SettingsVisible=false;NewBoardVisible=false;NewName="";NewDescription="";if(selected is not null){EditName=selected.Name;EditDescription=selected.Description??"";}MemberEmail="";}
    private Dictionary<Guid,MemberSettings> MemberDraft()=>Members.Where(m=>m.SelectedRole?.Value!=m.Member.Role||m.CanCreateCards!=m.Member.CanCreateCards||m.CanDeleteCards!=m.Member.CanDeleteCards).ToDictionary(m=>m.Member.User.Id,m=>m.Settings());
    private void RestoreMemberDraft(Dictionary<Guid,MemberSettings> draft)
    {
        foreach(var row in Members)if(draft.TryGetValue(row.Member.User.Id,out var old)){row.SelectedRole=row.Roles.Single(r=>r.Value==old.Role);row.CanCreateCards=old.CanCreateCards;row.CanDeleteCards=old.CanDeleteCards;}
    }
    public Task SaveMembers()=>RunAsync(async t=>
    {
        if(selected is null||!IsAdmin)return;
        await services.Boards.Settings(selected.Id,new(){ExpectedBoardVersion=selected.Version,Members=Members.Select(m=>m.Settings()).ToList()},t);
        await OpenCoreAsync(selected.Id,t);MembersVisible=false;await Content.RefreshAsync();await LoadCoreAsync(t);Status="Права учасників збережено.";
    });
    public Task FavoriteTile(BoardSummaryDto board)=>RunAsync(async t=>{await services.Boards.Favorite(board.Id,!board.IsFavorite,t);if(selected?.Id==board.Id)selected=await services.Boards.GetAsync(board.Id,t);await LoadCoreAsync(t);NotifyPresentation();});
    private Task FavoriteSelected()=>selected is null?Task.CompletedTask:RunAsync(async t=>{await services.Boards.Favorite(selected.Id,!selected.IsFavorite,t);selected=await services.Boards.GetAsync(selected.Id,t);await LoadCoreAsync(t);NotifyPresentation();});
    public Task ArchiveTile(BoardSummaryDto board)=>RunAsync(async t=>{var current=await services.Boards.GetAsync(board.Id,t);await services.Boards.Archive(board.Id,!board.IsArchived,current.Version,t);await LoadCoreAsync(t);});
    private Task ArchiveSelected()=>RunAsync(async t=>{if(selected is null||!IsAdmin)return;selected=await services.Boards.GetAsync(selected.Id,t);selected=await services.Boards.Archive(selected.Id,!selected.IsArchived,selected.Version,t);await Content.RefreshAsync();await LoadCoreAsync(t);NotifyPresentation();Status=selected?.IsArchived==true?"Дошку архівовано.":"Дошку відновлено.";});
    public Task UploadBoardCover(byte[] bytes,string filename)=>RunAsync(async t=>{if(selected is null||!IsAdmin)return;selected=await services.Boards.Cover(selected.Id,bytes,filename,selected.Version,t);await LoadCoreAsync(t);await Content.RefreshAsync();NotifyBoard();});
    private Task RemoveBoardCover()=>RunAsync(async t=>{if(selected is null||!IsAdmin)return;selected=await services.Boards.RemoveCover(selected.Id,selected.Version,t);await LoadCoreAsync(t);await Content.RefreshAsync();NotifyBoard();});
    public void ImageError(string text)=>Error=text;
    private Task LoadOverview(bool archived)=>RunAsync(async t=>
    {
        foreach(var old in OverviewCards)old.Dispose();OverviewCards.Clear();var offset=0;PagedResult<PersonalCardDto> result;
        do{result=await services.Content.Overview(offset,archived,!archived,t);foreach(var row in result.Items)OverviewCards.Add(new(row,this));offset+=result.Items.Count;}while(offset<result.TotalCount&&result.Items.Count>0);
        Notify(nameof(NoOverviewCards));
    });
    public async Task OpenOverview(PersonalCardDto row){await OpenVisualAsync(row.BoardId);if(Content.BoardId==row.BoardId)await Content.OpenCardAsync(row.Card.Id);}
    public Task RestoreCard(PersonalCardDto row)=>RunAsync(async t=>{var board=await services.Boards.GetAsync(row.BoardId,t);var card=await services.Content.Card(row.Card.Id,t);await services.Content.Archive(row.Card.Id,false,new(){ExpectedBoardVersion=board.Version,ExpectedVersion=card.Card.Version},t);var removed=OverviewCards.FirstOrDefault(x=>x.Row.Card.Id==row.Card.Id);if(removed is not null){OverviewCards.Remove(removed);removed.Dispose();}Notify(nameof(NoOverviewCards));Status="Картку відновлено.";});
    private void RefreshIdentity(){Notify(nameof(CurrentUser));Notify(nameof(UserName));CurrentAvatar=CurrentUser is {} user?new(user):null;if(CurrentAvatar is {} avatar)_=avatar.Load(services);}
    private void RefreshTiles()
    {
        VisibleBoards.Clear();foreach(var tile in Boards.Where(b=>b.Board.IsArchived==IsArchive&&(Page!="favorites"||b.Board.IsFavorite)&&(BoardSearch.Length==0||b.Name.Contains(BoardSearch,StringComparison.CurrentCultureIgnoreCase)))){VisibleBoards.Add(tile);_=tile.LoadMedia(services);}
        RecentBoards.Clear();foreach(var tile in Boards.Where(b=>!b.Board.IsArchived).OrderByDescending(b=>b.Board.LastOpenedAtUtc).Take(5))RecentBoards.Add(tile);
        Notify(nameof(NoVisibleBoards));
    }
    private void NotifyPresentation(){foreach(var name in new[]{nameof(BoardsVisible),nameof(BoardVisible),nameof(TasksVisible),nameof(TeamVisible),nameof(ProfileVisible),nameof(IsArchive),nameof(PageTitle),nameof(PageSubtitle),nameof(FavoriteIcon),nameof(ArchiveLabel),nameof(MembersLabel)})Notify(name);RefreshTiles();}
    private void ResetPresentation(){CancelPresentation();CloseDialogs();Page="boards";foreach(var old in OverviewCards)old.Dispose();OverviewCards.Clear();foreach(var old in Team)old.Dispose();Team.Clear();Profile.Clear();CurrentAvatar=null;NavigationConfirmation=false;pendingNavigation=null;}
    private void DisposePresentation(){Profile.Dispose();CurrentAvatar=null;foreach(var old in OverviewCards)old.Dispose();foreach(var old in Team)old.Dispose();}
}
