namespace Composa.App;

internal enum EditorLayoutPreset { Classic, Modern }
internal enum AiPanelPlacement { Sidebar, UnderSelection }

internal readonly record struct EditorSetup(EditorTheme Theme, EditorLayoutPreset Layout, AiPanelPlacement AiPanel)
{
    internal static string ThemeLabel(EditorTheme theme) => theme switch
    {
        EditorTheme.DarkNeutral => "Dark neutral",
        EditorTheme.LightNeutral => "Light neutral",
        _ => "Classic blue"
    };

    internal static string LayoutLabel(EditorLayoutPreset preset) => preset == EditorLayoutPreset.Modern
        ? "Modern layout" : "Classic layout";

    internal static string AiPanelLabel(AiPanelPlacement placement) => placement == AiPanelPlacement.Sidebar
        ? "Side panel" : "Under selection";
}
