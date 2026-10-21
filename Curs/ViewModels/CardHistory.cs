using System.Threading.Tasks;
using System.Linq;
namespace Curs.ViewModels;
public sealed partial class BoardContentViewModel
{
    private HistoryViewModel? cardHistory;
    private bool historyTab;
    public HistoryViewModel CardHistory=>cardHistory??=new(services,_=>CardDetailsCommand.Execute(null));
    public bool ShowCardDetails=>!historyTab;
    public bool ShowCardHistory=>historyTab;
    public ActionCommand CardDetailsCommand=>new(()=>{historyTab=false;Notify(nameof(ShowCardDetails));Notify(nameof(ShowCardHistory));});
    public ActionCommand CardHistoryCommand=>new(()=>_=ShowHistory());
    private async Task ShowHistory()
    {
        if(board is null||selectedCard is null)return;
        historyTab=true;Notify(nameof(ShowCardDetails));Notify(nameof(ShowCardHistory));
        await CardHistory.Open(board.Id,Assignees.Where(a=>a.Avatar is not null).Select(a=>a.Avatar!.User),selectedCard.Card.Id);
    }
    private void ResetCardHistory(){historyTab=false;cardHistory?.Clear();Notify(nameof(ShowCardDetails));Notify(nameof(ShowCardHistory));}
}
