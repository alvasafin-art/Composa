using Avalonia.Controls;
using Composa.App.Dialogs;

namespace Composa.App;

public sealed partial class MainWindow
{
    private async Task SaveSelectionChannel()
    {
        var target = session!;
        var name = new TextBox { Text = "Selection " + (target.Document.AlphaChannels.Count + 1), Width = 280, MaxLength = 200 };
        var dialog = new DialogWindow("Save Selection", Ui.Row(10, Ui.Label("Name"), name));
        if (await dialog.Ask(this)) target.SaveSelection(name.Text ?? "");
    }
    private async Task LoadSelectionChannel()
    {
        var target = session!; var names = target.Document.AlphaChannels.Keys.ToArray();
        if (names.Length == 0) return;
        var selected = names[0];
        var choices = Ui.Combo(names, selected, n => n, n => selected = n, 280);
        var mode = Composa.Selections.SelectionMode.Replace;
        var operation = Ui.Combo(Enum.GetValues<Composa.Selections.SelectionMode>(), mode, m => m.ToString(), m => mode = m, 150);
        var body = Ui.Column(10, Ui.Row(10, Ui.Label("Channel"), choices), Ui.Row(10, Ui.Label("Operation"), operation));
        var dialog = new DialogWindow("Load Selection", body);
        body.Children.Add(Ui.TextButton("Delete saved channel", () => { target.DeleteSelectionChannel(selected); dialog.Close(false); }));
        if (await dialog.Ask(this)) target.LoadSelection(selected, mode);
    }
}
