using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Curs.Services;
using KanbanContracts;
namespace Curs.ViewModels;
public sealed record CalendarAssignee(Guid? Id,string Name,bool Unassigned=false){public override string ToString()=>Name;}
public sealed class CalendarTaskViewModel(CalendarCardDto row,CalendarViewModel owner)
{
    public CalendarCardDto Row{get;}=row;
    public string Title=>Row.Title;
    public string Context=>Row.BoardName+" · "+Row.ColumnName;
    public string Assignee=>Row.Assignee?.Name??"Без виконавця";
    public string Color=>Row.Priority switch{CardPriority.Critical=>"#FEE2E2",CardPriority.High=>"#FFF0CC",CardPriority.Low=>"#DDF3E8",_=>"#E8EAFB"};
    public ActionCommand OpenCommand{get;}=new(()=>owner.Open(row),()=>!owner.IsBusy);
}
public sealed class CalendarDayViewModel(DateOnly date,DateOnly anchor,IReadOnlyList<CalendarTaskViewModel> tasks,double height)
{
    public DateOnly Date{get;}=date;
    public double Height{get;}=height;
    public string Label=>Date.Day.ToString();
    public string AccessibleDate=>Date.ToString("dd.MM.yyyy");
    public string Background=>Date==DateOnly.FromDateTime(DateTime.Today)?"#E5F5F4":Date.Month==anchor.Month?"White":"#F6F8FB";
    public IReadOnlyList<CalendarTaskViewModel> Tasks{get;}=tasks;
}
public sealed class CalendarViewModel:ViewModelBase,IDisposable
{
    private readonly ClientServices services;
    private readonly Action<Guid,Guid> open;
    private CancellationTokenSource lifetime=new();
    private DateOnly anchor=DateOnly.FromDateTime(DateTime.Today);
    private bool week,busy,active,disposed;
    private string error="",status="";
    private CalendarAssignee? assignee;
    private List<CalendarCardDto> rows=[];
    public ObservableCollection<CalendarDayViewModel> Days{get;}=[];
    public ObservableCollection<CalendarTaskViewModel> Undated{get;}=[];
    public ObservableCollection<CalendarAssignee> Assignees{get;}=[];
    public string[] Weekdays{get;}=["Пн","Вт","Ср","Чт","Пт","Сб","Нд"];
    public bool Active{get=>active;set{active=value;if(!value){Cancel();IsBusy=false;}}}
    public bool IsBusy{get=>busy;private set{busy=value;Notify();Notify(nameof(CanEdit));foreach(var c in new[]{PreviousCommand,NextCommand,TodayCommand,WeekCommand,MonthCommand,RefreshCommand})c?.Refresh();}}
    public bool CanEdit=>!IsBusy;
    public bool IsWeek=>week;
    public bool IsMonth=>!week;
    public double DayHeight=>week?310:116;
    public DateOnly Start=>week?Monday(anchor):Monday(new(anchor.Year,anchor.Month,1));
    public DateOnly End=>Start.AddDays(week?7:42);
    public string Heading=>week?$"{Start:dd.MM} — {End.AddDays(-1):dd.MM.yyyy}":anchor.ToString("MMMM yyyy",CultureInfo.GetCultureInfo("uk-UA"));
    public bool NoTasks=>rows.Count==0&&!IsBusy;
    public bool HasUndated=>Undated.Count>0;
    public string UndatedHeading=>$"Без дедлайну · {Undated.Count}";
    public string Error{get=>error;private set{error=value;Notify();}}
    public string Status{get=>status;private set{status=value;Notify();}}
    public CalendarAssignee? Assignee{get=>assignee;set{if(assignee==value)return;assignee=value;Notify();if(active&&!busy)_=Refresh();}}
    public ActionCommand PreviousCommand{get;}
    public ActionCommand NextCommand{get;}
    public ActionCommand TodayCommand{get;}
    public ActionCommand WeekCommand{get;}
    public ActionCommand MonthCommand{get;}
    public ActionCommand RefreshCommand{get;}
    public CalendarViewModel(ClientServices services,Action<Guid,Guid> open)
    {
        this.services=services;this.open=open;
        PreviousCommand=new(()=>_=Navigate(-1),()=>CanEdit&&anchor.Year>1900);NextCommand=new(()=>_=Navigate(1),()=>CanEdit&&anchor.Year<9998);
        TodayCommand=new(()=>_=SetAnchor(DateOnly.FromDateTime(DateTime.Today)),()=>CanEdit);WeekCommand=new(()=>_=SetMode(true),()=>CanEdit);MonthCommand=new(()=>_=SetMode(false),()=>CanEdit);RefreshCommand=new(()=>_=Refresh(),()=>CanEdit);
        services.Session.Changed+=SessionChanged;
    }
    public static DateOnly Monday(DateOnly date)=>date.AddDays(-(((int)date.DayOfWeek+6)%7));
    private void SessionChanged(){if(services.Session.User is null)Clear();}
    public async Task Show()
    {
        Active=true;await Run(async t=>{var people=await services.Content.Team(t);t.ThrowIfCancellationRequested();var selected=assignee;Assignees.Clear();Assignees.Add(new(null,"Усі виконавці"));Assignees.Add(new(null,"Без виконавця",true));foreach(var u in people)Assignees.Add(new(u.Id,u.Name));assignee=Assignees.FirstOrDefault(x=>x.Id==selected?.Id&&x.Unassigned==(selected?.Unassigned??false))??Assignees[0];Notify(nameof(Assignee));await Load(t);});
    }
    public Task SetAnchor(DateOnly date){if(busy)return Task.CompletedTask;anchor=date;return Refresh();}
    public Task Navigate(int direction)=>SetAnchor(week?anchor.AddDays(7*direction):anchor.AddMonths(direction));
    public Task SetMode(bool value){if(busy)return Task.CompletedTask;week=value;return Refresh();}
    public Task Refresh()=>Run(Load);
    private async Task Load(CancellationToken t)
    {
        // Clear first: a failed access refresh must not leave another user's stale cards visible.
        rows=[];Days.Clear();Undated.Clear();NotifyCalendar();var loaded=new List<CalendarCardDto>();var offset=0;PagedResult<CalendarCardDto> page;
        do{page=await services.Calendar.List(Start,End,offset,assignee?.Id,assignee?.Unassigned??false,t);t.ThrowIfCancellationRequested();loaded.AddRange(page.Items);offset+=page.Items.Count;}while(page.Items.Count>0&&offset<page.TotalCount);
        rows=loaded.DistinctBy(x=>x.Id).ToList();var tasks=rows.Select(x=>new CalendarTaskViewModel(x,this)).ToList();
        for(var date=Start;date<End;date=date.AddDays(1))Days.Add(new(date,anchor,tasks.Where(x=>x.Row.DueDate==date).ToList(),DayHeight));
        foreach(var task in tasks.Where(x=>x.Row.DueDate is null))Undated.Add(task);NotifyCalendar();
    }
    public void Open(CalendarCardDto row){if(!busy&&active)open(row.BoardId,row.Id);}
    public Task Move(CalendarCardDto row,DateOnly? date)=>Run(async t=>
    {
        if(row.DueDate==date)return;
        try{await services.Calendar.Change(row,date,t);t.ThrowIfCancellationRequested();await Load(t);Status="Дедлайн змінено.";}
        catch(ApiClientException ex) when(ex.Status is 409 or 404){await Load(t);Error=ex.Message;}
    });
    private void NotifyCalendar(){foreach(var n in new[]{nameof(Start),nameof(End),nameof(Heading),nameof(IsWeek),nameof(IsMonth),nameof(DayHeight),nameof(NoTasks),nameof(HasUndated),nameof(UndatedHeading)})Notify(n);}
    private async Task Run(Func<CancellationToken,Task> work)
    {
        if(busy||disposed||!active||services.Session.User is null)return;Cancel();var source=lifetime;IsBusy=true;Error="";Status="";
        try{await work(source.Token);}catch(OperationCanceledException){}catch(ApiClientException ex){if(!source.IsCancellationRequested)Error=ex.Message;}catch(HttpRequestException){if(!source.IsCancellationRequested)Error="Не вдалося підключитися. Оновіть календар і повторіть дію.";}catch(Exception){if(!source.IsCancellationRequested)Error="Не вдалося завантажити календар.";}
        finally{if(ReferenceEquals(source,lifetime)&&!disposed){IsBusy=false;NotifyCalendar();}}
    }
    private void Cancel(){lifetime.Cancel();lifetime.Dispose();lifetime=new();}
    public void Clear(){Active=false;rows=[];Days.Clear();Undated.Clear();Assignees.Clear();assignee=null;Error="";Status="";Notify(nameof(Assignee));NotifyCalendar();}
    public void Dispose(){services.Session.Changed-=SessionChanged;Clear();disposed=true;lifetime.Dispose();}
}
