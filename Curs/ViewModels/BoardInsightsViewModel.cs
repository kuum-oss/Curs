using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Curs.Services;
using KanbanContracts;
namespace Curs.ViewModels;
public sealed record CompletionOption(Guid? Id,string Name){public override string ToString()=>Name;}
public sealed record MetricViewModel(string Title,string Value,string Change,string Note,string Background,string Border,string Accent,string Icon,string ChangeColor);
public sealed class WorkloadViewModel : IDisposable
{
    public string Name=>Avatar.Name;
    public AvatarViewModel Avatar{get;}
    public string Count{get;}
    public double Percent{get;}
    public WorkloadViewModel(WorkloadDto row,ClientServices services){Avatar=new(row.User);_=Avatar.Load(services);Count=$"{row.Active} / {row.Total}";Percent=row.Total==0?0:100d*row.Active/row.Total;}
    public void Dispose()=>Avatar.Dispose();
}
public sealed class BoardInsightsViewModel : ViewModelBase,IDisposable
{
    private readonly ClientServices services;
    private readonly Action<ActivityDto> open;
    private CancellationTokenSource? request;
    private Guid? board;
    private BoardStatisticsDto? stats;
    private bool busy,drawer,admin;
    private string mode="board",error="",boardName="";
    private CompletionOption? completion;
    public HistoryViewModel History{get;}
    public ObservableCollection<MetricViewModel> Metrics{get;}=[];
    public ObservableCollection<WorkloadViewModel> Workload{get;}=[];
    public ObservableCollection<ActivityViewModel> Recent{get;}=[];
    public ObservableCollection<CompletionOption> Columns{get;}=[];
    public CompletionOption? Completion{get=>completion;set{completion=value;Notify();}}
    public System.Collections.Generic.IReadOnlyList<DailyCompletionDto> Days=>stats?.Days??[];
    public bool IsBoard=>mode=="board";
    public bool IsHistory=>mode=="history";
    public bool IsOverview=>mode=="overview";
    public bool Drawer{get=>drawer;private set{drawer=value;Notify();}}
    public bool Busy{get=>busy;private set{busy=value;Notify();SaveCompletionCommand.Refresh();}}
    public bool CanConfigure=>admin;
    public string BoardName=>boardName;
    public string Error{get=>error;private set{error=value;Notify();}}
    public bool HasRecent=>Recent.Count>0;
    public bool HasData=>stats is not null;
    public bool NeedsCompletion=>stats is not null&&stats.CompletionColumnId is null;
    public string TrackingNote=>stats is null?"":$"Історія з {stats.TrackingSinceUtc.ToLocalTime():dd.MM.yyyy}. Час виконання — від створення до першого зафіксованого завершення.";
    public ActionCommand BoardCommand{get;}
    public ActionCommand HistoryCommand{get;}
    public ActionCommand OverviewCommand{get;}
    public ActionCommand ToggleDrawerCommand{get;}
    public ActionCommand CloseDrawerCommand{get;}
    public ActionCommand FullOverviewCommand{get;}
    public ActionCommand RefreshCommand{get;}
    public ActionCommand SaveCompletionCommand{get;}
    public BoardStatisticsDto? Snapshot=>stats;
    public BoardInsightsViewModel(ClientServices services,Action<ActivityDto> open)
    {
        this.services=services;this.open=open;History=new(services,open);
        BoardCommand=new(()=>SetMode("board"));HistoryCommand=new(()=>{SetMode("history");_=History.Load();});OverviewCommand=new(()=>{SetMode("overview");_=Refresh();});
        ToggleDrawerCommand=new(()=>{Drawer=!Drawer;if(Drawer)_=Refresh();});CloseDrawerCommand=new(()=>Drawer=false);FullOverviewCommand=new(()=>{Drawer=false;SetMode("overview");_=Refresh();});
        RefreshCommand=new(()=>_=Refresh());SaveCompletionCommand=new(()=>_=SaveCompletion(),()=>admin&&!Busy&&stats is not null);
        services.Session.Changed+=SessionChanged;
    }
    public void SetMode(string value){mode=value;foreach(var n in new[]{nameof(IsBoard),nameof(IsHistory),nameof(IsOverview)})Notify(n);}
    public async Task Open(Guid id,string name,bool isAdmin,System.Collections.Generic.IEnumerable<UserDto> people)
    {Clear();board=id;boardName=name;admin=isAdmin;Notify(nameof(BoardName));Notify(nameof(CanConfigure));await Task.WhenAll(Refresh(),History.Open(id,people));}
    public void SetAdmin(bool value){admin=value;Notify(nameof(CanConfigure));SaveCompletionCommand.Refresh();}
    private void SessionChanged(){if(services.Session.AccessToken is null)Clear();}
    public async Task Refresh()
    {
        if(board is not {} id||services.Session.AccessToken is null)return;
        request?.Cancel();request?.Dispose();var source=request=new();var t=source.Token;var session=services.Session.AccessToken;Busy=true;Error="";
        try{var result=await services.Insights.Statistics(id,t);t.ThrowIfCancellationRequested();if(board!=id||session!=services.Session.AccessToken)return;stats=result;Populate();}
        catch(OperationCanceledException){}
        catch(ApiClientException e){if(!t.IsCancellationRequested){if(e.Status is 401 or 404){stats=null;ClearRows();History.Clear();}Error=e.Message;NotifyAll();}}
        catch(Exception){if(!t.IsCancellationRequested)Error="Не вдалося завантажити статистику. Повторіть спробу.";}
        finally{if(ReferenceEquals(request,source))Busy=false;}
    }
    public async Task SaveCompletion()
    {
        if(!admin||Busy||board is not {} id||stats is null)return;
        var version=stats.BoardVersion;Busy=true;Error="";var session=services.Session.AccessToken;
        using var save=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try{await services.Insights.SetCompletion(id,completion?.Id,version,save.Token);if(board==id&&session==services.Session.AccessToken)await Refresh();}
        catch(ApiClientException e){if(board==id&&session==services.Session.AccessToken){Error=e.Message;if(e.Status is 401 or 404){Clear();Error=e.Message;}else if(e.Status==409){await Refresh();Error="Дані вже змінено. Перевірте актуальну колонку й повторіть збереження.";}}}
        catch(Exception){if(board==id&&session==services.Session.AccessToken)Error="Не вдалося зберегти колонку завершення.";}
        finally{if(board==id)Busy=false;}
    }
    private void Populate()
    {
        ClearRows();if(stats is not {} s)return;
        var uk=CultureInfo.GetCultureInfo("uk-UA");var configured=s.CompletionColumnId is not null;
        string Number(double? n)=>n?.ToString("0.#",uk)??"—";
        string Delta(double? current,double? previous){if(current is null||previous is null)return "—";var d=current.Value-previous.Value;return d==0?"— 0":(d>0?"↑ +":"↓ ")+d.ToString("0.#",uk);}
        string Comparison(double? current,double? previous,bool lowerBetter=false)
        {if(current is null)return "Ще немає завершених задач";if(previous is null)return "Недостатньо історії для порівняння";if(previous==current)return "Без змін порівняно з минулим тижнем";if(previous==0)return "Попередній тиждень: 0";var percent=Math.Abs((current.Value-previous.Value)/previous.Value*100).ToString("0",uk);return $"на {percent}% {(current>previous?lowerBetter?"повільніше":"більше":lowerBetter?"швидше":"менше")}, ніж минулого тижня";}
        string Color(double? current,double? previous,bool lowerBetter)=>current is null||previous is null?"#7B8AA4":current==previous?"#268BE3":(current<previous)==lowerBetter?"#159B72":"#E65264";
        const string check="M2,10 L7,15 18,3 20,5 7,19 0,12 Z";
        const string clock="M10,0 A10,10 0 1 1 9.99,0 M10,2 A8,8 0 1 0 10.01,2 M9,4 L11,4 11,9 15,12 14,14 9,10 Z";
        const string progress="M10,0 L10,3 A7,7 0 1 1 3,10 L0,10 A10,10 0 1 0 10,0 M1,5 L3,2 5,4 3,7 Z";
        const string bars="M1,12 L5,12 5,20 1,20 Z M8,5 L12,5 12,20 8,20 Z M15,0 L19,0 19,20 15,20 Z";
        Metrics.Add(new("Виконано за тиждень",configured?s.Completed.ToString():"—",configured?Delta(s.Completed,s.PreviousCompleted):"—",configured?Comparison(s.Completed,s.PreviousCompleted):"Оберіть колонку завершення","#F0FAF6","#DDEFE7","#37B992",check,Color(s.Completed,s.PreviousCompleted,false)));
        Metrics.Add(new("Прострочено",s.Overdue.ToString(),Delta(s.Overdue,s.PreviousOverdue),$"{s.OverdueOverSevenDays} задач прострочені понад 7 днів","#FFF4F5","#F7E3E6","#EE6578",clock,Color(s.Overdue,s.PreviousOverdue,true)));
        Metrics.Add(new("У роботі",s.InProgress.ToString(),Delta(s.InProgress,s.PreviousInProgress),Comparison(s.InProgress,s.PreviousInProgress),"#F1F7FF","#DFEAFB","#398CE7",progress,Color(s.InProgress,s.PreviousInProgress,true)));
        Metrics.Add(new("Середній час виконання",configured&&s.MeanDays is not null?Number(s.MeanDays)+" дня":"—",configured?Delta(s.MeanDays,s.PreviousMeanDays):"—",configured?Comparison(s.MeanDays,s.PreviousMeanDays,true):"Оберіть колонку завершення","#F7F4FF","#EAE5F6","#8176DB",bars,Color(s.MeanDays,s.PreviousMeanDays,true)));
        foreach(var w in s.Workload)Workload.Add(new(w,services));foreach(var e in s.Recent)Recent.Add(new(e,services,open));
        Columns.Clear();Columns.Add(new(null,"Не обрано"));foreach(var c in s.Columns)Columns.Add(new(c.Id,c.Name));completion=Columns.FirstOrDefault(c=>c.Id==s.CompletionColumnId)??Columns[0];Notify(nameof(Completion));NotifyAll();
    }
    private void NotifyAll(){foreach(var n in new[]{nameof(Days),nameof(HasRecent),nameof(HasData),nameof(NeedsCompletion),nameof(TrackingNote),nameof(Snapshot)})Notify(n);}
    private void ClearRows(){Metrics.Clear();foreach(var w in Workload)w.Dispose();Workload.Clear();foreach(var e in Recent)e.Dispose();Recent.Clear();}
    public void Clear(){request?.Cancel();board=null;stats=null;ClearRows();History.Clear();Columns.Clear();completion=null;Drawer=false;SetMode("board");Error="";Busy=false;NotifyAll();}
    public void Dispose(){Clear();request?.Dispose();History.Dispose();services.Session.Changed-=SessionChanged;}
}
