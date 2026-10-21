using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Curs.ViewModels;
using KanbanContracts;
namespace Curs.Views;
public partial class BackupView:UserControl
{
    public BackupView()=>AvaloniaXamlLoader.Load(this);
    private async void SaveBackup(object? sender,RoutedEventArgs e)=>await Save(false);
    private async void SaveCsv(object? sender,RoutedEventArgs e)=>await Save(true);
    private Task Save(bool csv)=>DataContext is BackupViewModel model?model.Save(csv,async(bytes,t)=>
    {
        if(TopLevel.GetTopLevel(this) is not {} root)return false;
        var file=await root.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions{Title=csv?"Експорт задач":"Зберегти резервну копію",SuggestedFileName=csv?"tasks.csv":"board.kanban.json",DefaultExtension=csv?"csv":"json"});t.ThrowIfCancellationRequested();if(file is null)return false;
        using(file)
        {
            if(file.TryGetLocalPath() is {} path)
            {
                var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                try{await File.WriteAllBytesAsync(temporary,bytes,t);t.ThrowIfCancellationRequested();File.Move(temporary,path,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}
            }
            else{await using var stream=await file.OpenWriteAsync();if(stream.CanSeek)stream.SetLength(0);await stream.WriteAsync(bytes,t);await stream.FlushAsync(t);}
        }
        return true;
    }):Task.CompletedTask;
    private async void ChooseBackup(object? sender,RoutedEventArgs e)
    {
        if(DataContext is not BackupViewModel model)return;await model.Load(async t=>
        {
            if(TopLevel.GetTopLevel(this) is not {} root)return null;var files=await root.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions{Title="Вибрати резервну копію",AllowMultiple=false,FileTypeFilter=[new FilePickerFileType("KanbanBoard JSON"){Patterns=["*.json"]}]});t.ThrowIfCancellationRequested();if(files.Count==0)return null;
            using var file=files[0];await using var input=await file.OpenReadAsync();using var output=new MemoryStream();var buffer=new byte[65536];int read;
            while((read=await input.ReadAsync(buffer,t))>0){if(output.Length+read>BackupLimits.MaxFileBytes)throw new ArgumentException("Копія: не більше 32 МіБ.");await output.WriteAsync(buffer.AsMemory(0,read),t);}return(file.Name,output.ToArray());
        });
    }
}
