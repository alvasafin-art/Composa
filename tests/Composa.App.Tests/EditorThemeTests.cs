using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public class EditorThemeTests
{
    [AvaloniaFact]
    public async Task Settings_preview_cancel_and_save_the_interface_theme()
    {
        var app = Assert.IsType<App>(Application.Current);
        var window = new MainWindow { Width = 1280, Height = 820 };
        window.Show();
        var session = EditorSession.NewCanvas(640, 400, SKColors.White);
        window.AddSession(session);
        session.AddText(new(48, 90), new TextStyle { Text = "Composa", Size = 64 });
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var panelBrush = Palette.Panel;
            var history = session.History.CurrentId;
            async Task Choose(EditorTheme theme, bool save)
            {
                var pending = AiDialogs.SettingsDialog(window, window.Settings, window.AiTasks, page: 3);
                Dispatcher.UIThread.RunJobs();
                var dialog = Assert.Single(window.OwnedWindows);
                var picker = dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "EditorThemePicker");
                Assert.Equal((int)window.Settings.Theme, picker.SelectedIndex);
                var language = dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "EditorLanguagePicker");
                Assert.Equal("Русский", language.Items[1]);
                language.SelectedIndex = 1;
                picker.SelectedIndex = (int)theme;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(theme, app.CurrentTheme);
                Assert.Equal(theme == EditorTheme.LightNeutral ? ThemeVariant.Light : ThemeVariant.Dark, dialog.ActualThemeVariant);
                if (theme == EditorTheme.LightNeutral) Assert.True(Screenshots.Save(dialog, "theme-settings-light"));
                var action = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == (save ? "Save settings" : "Cancel"));
                action.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(save, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            Assert.Equal(EditorTheme.Classic, Settings.FromJson("{}").Theme);
            await Choose(EditorTheme.DarkNeutral, false);
            Assert.Equal(EditorTheme.Classic, app.CurrentTheme);
            Assert.Equal("en", window.Settings.Language);
            foreach (var choice in new[] { EditorTheme.DarkNeutral, EditorTheme.LightNeutral, EditorTheme.Classic })
            {
                await Choose(choice, true);
                Assert.Same(panelBrush, Palette.Panel);
                Assert.Equal(choice, window.Settings.Theme);
                if (choice != EditorTheme.Classic) Assert.True(Palette.Active.Color.R < panelBrush.Color.R);
                window.Settings.SaveTo(path);
                Assert.Equal(choice, Settings.LoadFrom(path).Theme);
                Assert.Equal("ru", Settings.LoadFrom(path).Language);
                Assert.True(Screenshots.Save(window, "workspace-theme-" + choice));
            }
            Assert.Equal(history, session.History.CurrentId);
        }
        finally
        {
            app.ApplyTheme(EditorTheme.Classic);
            session.MarkSaved("theme.cmps", session.History.CurrentId);
            window.Close();
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }
}
