using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Curs.Services;
using KanbanContracts;
namespace Curs.ViewModels;
public sealed record HistoryOption(string? Field,string Name){public override string ToString()=>Name;}
public sealed record HistoryPerson(Guid? Id,string Name){public override string ToString()=>Name;}
public sealed record HistoryPeriod(int Days,string Name){public override string ToString()=>Name;}
public sealed class ActivityViewModel : IDisposable
{
    public ActivityDto Row { get; }
    public AvatarViewModel Avatar { get; }
    public bool ShowDay { get; init; } = true;
    public string Actor=>Row.Actor.Name;
    public string Title=>Row.CardTitle;
    public string Day=>Row.CreatedAtUtc.ToLocalTime().ToString("dd MMMM yyyy",CultureInfo.GetCultureInfo("uk-UA"));
    public string Time=>Row.CreatedAtUtc.ToLocalTime().ToString("HH:mm");
    public string When=>Row.CreatedAtUtc.ToLocalTime().ToString("dd MMM, HH:mm",CultureInfo.GetCultureInfo("uk-UA"));
    public string Action=>Row.Field switch{"created"=>"· картку створено","deleted"=>"· картку видалено","column"=>"· картку переміщено","deadline"=>"· дедлайн змінено","priority"=>"· пріоритет змінено","assignee"=>"· виконавця змінено","title"=>"· назву змінено","description"=>"· опис змінено",_=>"· архів змінено"};
    public string Field=>Row.Field switch{"created"=>"Створення","deleted"=>"Видалення","column"=>"Колонка","deadline"=>"Дедлайн","priority"=>"Пріоритет","assignee"=>"Виконавець","title"=>"Назва","description"=>"Опис",_=>"Архів"};
    private string Format(string? value)=>Row.Field=="deadline"&&DateOnly.TryParse(value,out var date)?date.ToString("d MMM",CultureInfo.GetCultureInfo("uk-UA")):value??"—";
    public string Before=>Format(Row.Before);
    public string After=>Format(Row.After);
    public string Accent=>Row.Field switch{"priority"=>"#E5A522","column"=>"#2196F3","assignee"=>"#A595EE",_=>"#0095A5"};
    public string AfterBackground=>Row.Field switch{"priority"=>"#FFE2E6","column"=>"#E8E8FE","deadline"=>"#FFE7EB",_=>"#E2F4F2"};
    public ActionCommand OpenCommand { get; }
    public ActivityViewModel(ActivityDto row,ClientServices services,Action<ActivityDto> open)
    {Row=row;Avatar=new(row.Actor);_=Avatar.Load(services);OpenCommand=new(()=>open(row),()=>row.CanOpen);}
    public void Dispose()=>Avatar.Dispose();
}
public sealed class HistoryViewModel : ViewModelBase, IDisposable
{
    private readonly ClientServices services;
    private readonly Action<ActivityDto> open;
    private CancellationTokenSource? request;
    private Guid? board,card;
    private string search="",error="";
    private bool busy;
    private int total;
    private HistoryPerson? person;
    private HistoryOption? kind;
    private HistoryPeriod? period;
    public ObservableCollection<ActivityViewModel> Events { get; }=[];
    public ObservableCollection<HistoryPerson> People { get; }=[];
    public HistoryOption[] Kinds { get; }=[new(null,"Усі зміни"),new("deadline","Дедлайн"),new("assignee","Виконавець"),new("priority","Пріоритет"),new("column","Колонка"),new("title","Назва"),new("description","Опис"),new("created","Створення"),new("deleted","Видалення"),new("archive","Архів")];
    public HistoryPeriod[] Periods { get; }=[new(7,"За 7 днів"),new(30,"За 30 днів"),new(90,"За 90 днів"),new(0,"Уся історія")];
    public string Search{get=>search;set{search=value;Notify();_=Load();}}
    public HistoryPerson? Person{get=>person;set{person=value;Notify();_=Load();}}
    public HistoryOption? Kind{get=>kind;set{kind=value;Notify();_=Load();}}
    public HistoryPeriod? Period{get=>period;set{period=value;Notify();_=Load();}}
    public bool Busy{get=>busy;private set{busy=value;Notify();Notify(nameof(Empty));MoreCommand.Refresh();}}
    public bool Empty=>!Busy&&Events.Count==0&&Error.Length==0;
    public bool HasMore=>Events.Count<total;
    public string Error{get=>error;private set{error=value;Notify();Notify(nameof(Empty));}}
    public ActionCommand MoreCommand{get;}
    public ActionCommand RefreshCommand{get;}
    public HistoryViewModel(ClientServices services,Action<ActivityDto> open){this.services=services;this.open=open;kind=Kinds[0];period=Periods[0];MoreCommand=new(()=>_=Load(true),()=>!Busy&&HasMore);RefreshCommand=new(()=>_=Load());services.Session.Changed+=SessionChanged;}
    private void SessionChanged(){if(services.Session.AccessToken is null)Clear();}
    public async Task Open(Guid id,IEnumerable<UserDto> people,Guid? cardId=null)
    {
        Clear();People.Add(new(null,"Усі учасники"));foreach(var u in people)People.Add(new(u.Id,u.Name));person=People[0];board=id;card=cardId;Notify(nameof(Person));await Load();
    }
    public async Task Load(bool more=false)
    {
        if(board is not {} id||services.Session.AccessToken is null)return;
        request?.Cancel();request?.Dispose();var source=request=new();var token=source.Token;var session=services.Session.AccessToken;
        var offset=more?Events.Count:0;Error="";Busy=true;
        try
        {
            var result=await services.Insights.History(id,new(){CardId=card,ActorId=person?.Id,Field=kind?.Field,Days=period?.Days??7,Search=search,Offset=offset,Limit=50},token);
            token.ThrowIfCancellationRequested();if(board!=id||session!=services.Session.AccessToken)return;
            if(!more)ClearRows();foreach(var row in result.Items){var day=row.CreatedAtUtc.ToLocalTime().ToString("dd MMMM yyyy",CultureInfo.GetCultureInfo("uk-UA"));Events.Add(new ActivityViewModel(row,services,open){ShowDay=Events.LastOrDefault()?.Day!=day});}total=result.TotalCount;Notify(nameof(HasMore));Notify(nameof(Empty));
        }
        catch(OperationCanceledException){}
        catch(ApiClientException e){if(!token.IsCancellationRequested){if(e.Status is 401 or 404)ClearRows();Error=e.Message;}}
        catch(Exception){if(!token.IsCancellationRequested)Error="Не вдалося завантажити історію. Повторіть спробу.";}
        finally{if(ReferenceEquals(request,source))Busy=false;}
    }
    private void ClearRows(){foreach(var e in Events)e.Dispose();Events.Clear();total=0;Notify(nameof(HasMore));}
    public void Clear(){request?.Cancel();board=null;card=null;ClearRows();People.Clear();search="";person=null;kind=Kinds[0];period=Periods[0];Error="";Busy=false;Notify(nameof(Search));Notify(nameof(Person));Notify(nameof(Kind));Notify(nameof(Period));}
    public void Dispose(){Clear();request?.Dispose();services.Session.Changed-=SessionChanged;}
}
