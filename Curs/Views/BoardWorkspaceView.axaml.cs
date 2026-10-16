using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Curs.ViewModels;
namespace Curs.Views;
public partial class BoardWorkspaceView:UserControl
{
    private readonly DispatcherTimer poll=new(){Interval=TimeSpan.FromSeconds(5)};
    public BoardWorkspaceView()
    {
        AvaloniaXamlLoader.Load(this);poll.Tick+=async(_,_)=>{if(DataContext is BoardWorkspaceViewModel model)await model.PollPresentation();};
        AttachedToVisualTree+=(_,_)=>poll.Start();DetachedFromVisualTree+=(_,_)=>poll.Stop();
    }
    private void OpenTile(object? sender,TappedEventArgs e){if(sender is Control{DataContext:BoardListItemViewModel row})row.OpenCommand.Execute(null);}
    private async void ChooseBoardCover(object? sender,RoutedEventArgs e)
    {
        if(DataContext is not BoardWorkspaceViewModel model||!model.IsAdmin||!model.CanEdit)return;var id=model.SelectedBoard?.Id;
        try{if(await MediaPicker.Pick(this,"Обкладинка дошки") is {} file&&model.SelectedBoard?.Id==id&&model.CanEdit)await model.UploadBoardCover(file.Bytes,file.Name);}catch(Exception ex){model.ImageError(ex is ArgumentException?ex.Message:"Не вдалося відкрити фото.");}
    }
}
