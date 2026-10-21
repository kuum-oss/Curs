using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Curs.Services;
using KanbanContracts;
namespace Curs.ViewModels;
public sealed record BackupBoardOption(Guid Id,string Name){public override string ToString()=>Name;}
public sealed class BackupViewModel:ViewModelBase,IDisposable
{
    private readonly ClientServices services;
    private Guid? userId;
    private readonly Action<Guid> open;
    private CancellationTokenSource lifetime=new();
    private bool busy,active,disposed;
    private string error="",status="",filename="";
    private Guid operation=Guid.NewGuid();
    private Guid? restored;
    private BoardBackup? document;
    private BackupBoardOption? board;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){MaxDepth=32,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    public ObservableCollection<BackupBoardOption> Boards{get;}=[];
    public bool Active{get=>active;set{active=value;if(!value){Cancel();IsBusy=false;}}}
    public bool IsBusy{get=>busy;private set{busy=value;Notify();RefreshState();}}
    public bool CanEdit=>!busy;
    public bool CanExport=>CanEdit&&SelectedBoard is not null;
    public BackupBoardOption? SelectedBoard{get=>board;set{board=value;Notify();RefreshState();}}
    public string Error{get=>error;private set{error=value;Notify();}}
    public string Status{get=>status;private set{status=value;Notify();}}
    public string Filename=>filename;
    public string Preview=>document is {} d?$"Назва: {d.Data.Name}\n{d.Data.Columns.Count} колонок · {d.Data.Cards.Count} карток · {d.Data.Cards.Count(c=>c.Image is not null)+(d.Data.Cover is null?0:1)} фото":"Оберіть файл копії для відновлення.";
    public bool HasDocument=>document is not null;
    public ActionCommand RestoreCommand{get;}
    public ActionCommand ClearFileCommand{get;}
    public ActionCommand OpenRestoredCommand{get;}
    public bool HasRestored=>restored is not null;
    public BackupViewModel(ClientServices services,Action<Guid> open)
    {
        this.services=services;this.open=open;userId=services.Session.User?.Id;RestoreCommand=new(()=>_=Restore(),()=>CanEdit&&HasDocument);ClearFileCommand=new(ClearFile,()=>CanEdit);OpenRestoredCommand=new(()=>{if(restored is {} id)open(id);},()=>CanEdit&&HasRestored);services.Session.Changed+=SessionChanged;
    }
    private void SessionChanged(){var next=services.Session.User?.Id;if(next!=userId){Clear();userId=next;}}
    public Task Show(){Active=true;return Run(async t=>{var selected=board?.Id;Boards.Clear();foreach(var row in await services.Boards.ListAsync(t)){t.ThrowIfCancellationRequested();Boards.Add(new(row.Id,row.Name));}SelectedBoard=Boards.FirstOrDefault(x=>x.Id==selected)??Boards.FirstOrDefault();});}
    public Task Save(bool csv,Func<byte[],CancellationToken,Task<bool>> save)=>Run(async t=>
    {
        if(board is not {} selected)return;var bytes=csv?await services.Backups.Csv(selected.Id,t):await services.Backups.Export(selected.Id,t);t.ThrowIfCancellationRequested();if(await save(bytes,t)){t.ThrowIfCancellationRequested();Status=csv?"CSV збережено.":"Резервну копію збережено.";}
    });
    public Task Load(Func<CancellationToken,Task<(string Name,byte[] Bytes)?>> pick)=>Run(async t=>
    {
        var file=await pick(t);t.ThrowIfCancellationRequested();if(file is null)return;ClearFile();
        if(file.Value.Bytes.Length>BackupLimits.MaxFileBytes)throw new ArgumentException("Копія: не більше 32 МіБ.");
        BoardBackup? data;try{data=JsonSerializer.Deserialize<BoardBackup>(file.Value.Bytes,Json);}catch(JsonException){throw new ArgumentException("Не вдалося прочитати файл копії.");}
        if(data is not{Format:"KanbanBoard",Version:1,Data:{} b}||b.Columns is null||b.Cards is null||b.Tags is null||b.Columns.Any(x=>x is null)||b.Cards.Any(x=>x is null)||b.Cards.Count>5000||data.Checksum is null||!string.Equals(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(b,Json))),data.Checksum,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Копія пошкоджена або має непідтримувану версію.");
        document=data;filename=file.Value.Name;operation=Guid.NewGuid();Notify(nameof(Filename));Notify(nameof(Preview));RefreshState();
    });
    public Task Restore()=>Run(async t=>
    {
        if(document is null)return;var result=await services.Backups.Restore(operation,document,t);t.ThrowIfCancellationRequested();restored=result.BoardId;Status=result.AlreadyRestored?"Цю копію вже відновлено цією дією. Повторну дошку не створено.":"Нову дошку відновлено. Учасників можна запросити знову.";RefreshState();
    });
    public void ClearFile(){document=null;restored=null;filename="";operation=Guid.NewGuid();Notify(nameof(Filename));Notify(nameof(Preview));RefreshState();}
    private void RefreshState(){foreach(var n in new[]{nameof(CanEdit),nameof(CanExport),nameof(HasDocument),nameof(HasRestored)})Notify(n);RestoreCommand?.Refresh();ClearFileCommand?.Refresh();OpenRestoredCommand?.Refresh();}
    private async Task Run(Func<CancellationToken,Task> work)
    {
        if(busy||!active||disposed||services.Session.User is null)return;Cancel();var source=lifetime;IsBusy=true;Error="";Status="";
        try{await work(source.Token);}catch(OperationCanceledException){if(!source.IsCancellationRequested)Error="Час очікування вичерпано. Повторіть дію.";}catch(ArgumentException ex){if(!source.IsCancellationRequested)Error=ex.Message;}catch(ApiClientException ex){if(!source.IsCancellationRequested)Error=ex.Message;}catch(HttpRequestException){if(!source.IsCancellationRequested)Error="Не вдалося підключитися. Повторіть дію; повтор відновлення не дублює дошку.";}catch(Exception){if(!source.IsCancellationRequested)Error="Не вдалося виконати дію з файлом. Перевірте доступ до нього.";}
        finally{if(ReferenceEquals(source,lifetime)&&!disposed)IsBusy=false;}
    }
    private void Cancel(){lifetime.Cancel();lifetime.Dispose();lifetime=new();}
    public void Clear(){Active=false;Boards.Clear();board=null;ClearFile();Error="";Status="";Notify(nameof(SelectedBoard));}
    public void Dispose(){services.Session.Changed-=SessionChanged;Clear();disposed=true;lifetime.Dispose();}
}
