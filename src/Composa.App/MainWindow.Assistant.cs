using Composa.App.Assistant;

namespace Composa.App;

public sealed partial class MainWindow
{
    private AssistantWindow? assistantWindow;
    private readonly AssistantConversation assistantConversation = new();

    private void ShowAssistant()
    {
        if (assistantWindow != null)
        {
            assistantWindow.Activate();
            return;
        }
        assistantWindow = new AssistantWindow(this, () => session, settings, assistantServer, scriptRuntime, aiTasks, assistantConversation);
        assistantWindow.Closed += (_, _) => assistantWindow = null;
        assistantWindow.Show(this);
    }
}
