using Avalonia.Controls;
using Composa.App.Dialogs;

namespace Composa.App;

public sealed partial class MainWindow
{
    private async Task NewAlphaChannel(bool fromSelection)
    {
        var target = session; if (target == null || canvas.IsDragging) return;
        if (fromSelection && target.Selection == null) { ShowProblem("Make a selection before saving it as a channel."); return; }
        var number = 1; while (target.Document.AlphaChannels.ContainsKey("Alpha " + number)) number++;
        var name = new TextBox { Text = "Alpha " + number, Width = 280, MaxLength = 200 };
        var selection = new CheckBox { Content = "Use current selection", IsChecked = fromSelection, IsEnabled = target.Selection != null };
        var white = new CheckBox { Content = "Start with white (reveal all)", IsEnabled = !fromSelection };
        selection.IsCheckedChanged += (_, _) => white.IsEnabled = selection.IsChecked != true;
        var dialog = new DialogWindow("New Alpha Channel", Ui.Column(12, Ui.Row(10, Ui.Label("Name"), name), selection, white));
        void Validate() => dialog.CanAccept = !string.IsNullOrWhiteSpace(name.Text) && !target.Document.AlphaChannels.ContainsKey(name.Text.Trim());
        name.TextChanged += (_, _) => Validate(); Validate();
        if (!await dialog.Ask(this) || !sessions.Contains(target)) return;
        try { target.AddAlphaChannel(name.Text ?? "", selection.IsChecked == true, white.IsChecked == true); }
        catch (Exception error) { ShowProblem(error.Message); }
    }

    private async Task RenameAlphaChannel(string original)
    {
        var target = session; if (target == null || canvas.IsDragging) return;
        var name = new TextBox { Text = original, Width = 280, MaxLength = 200 };
        var dialog = new DialogWindow("Rename Alpha Channel", Ui.Row(10, Ui.Label("Name"), name));
        void Validate() => dialog.CanAccept = !string.IsNullOrWhiteSpace(name.Text) &&
            (name.Text.Trim().Equals(original, StringComparison.OrdinalIgnoreCase) || !target.Document.AlphaChannels.ContainsKey(name.Text.Trim()));
        name.TextChanged += (_, _) => Validate(); Validate();
        if (!await dialog.Ask(this) || !sessions.Contains(target)) return;
        try { target.RenameAlphaChannel(original, name.Text ?? ""); } catch (Exception error) { ShowProblem(error.Message); }
    }

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
