using System.Text.RegularExpressions;
using Composa.AI;

namespace Composa.App.Assistant;

/// <summary>Explicit requests for a code artifact take precedence over the auto-apply preference.</summary>
internal static class AssistantIntent
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static bool RequestsScript(string text)
    {
        if (!Regex.IsMatch(text, @"\b(?:скрипт\p{L}*|script\p{L}*|код(?:а|ом)?|code)\b", Options)) return false;
        if (Regex.IsMatch(text, @"\b(?:не\s+(?:запускай|выполняй|применяй)|do\s+not\s+(?:run|execute|apply)|don't\s+(?:run|execute|apply))\b", Options)) return true;
        // 'Write a script that draws…' is not permission to run it. Only an explicit second action is.
        if (Regex.IsMatch(text, @"\b(?:и|затем|потом|and|then)\s+(?:сразу\s+)?(?:запусти(?:те)?|выполни(?:те)?|примени(?:те)?|run|execute|apply)\b", Options)) return false;
        if (Regex.IsMatch(text, @"\b(?:нужен|нужны|хочу|need|want)\s+(?:(?:новый|повторно\s+используемый|a|new|reusable)\s+){0,3}(?:скрипт\p{L}*|script\p{L}*|код|code)\b", Options)) return true;
        return Regex.IsMatch(text, @"\b(?:напиш\p{L}*|созда\p{L}*|сгенер\p{L}*|пришл\p{L}*|вышл\p{L}*|просил\p{L}*|предостав\p{L}*|выда\p{L}*|покаж\p{L}*|дай|верни|отправ\p{L}*|исправ\p{L}*|доработ\p{L}*|write|create|generate|send|provide|show|give|return|fix|revise)\b(?:(?!\b(?:сло\p{L}*|квадрат\p{L}*|прямоугольник\p{L}*|изображени\p{L}*|layer\p{L}*|rectangle\p{L}*|image\p{L}*)\b)[^\n.!?]){0,80}\b(?:скрипт\p{L}*|script\p{L}*|код(?:а|ом)?|code)\b", Options);
    }

    public static AssistantPlan AsScriptDraft(AssistantPlan plan)
    {
        if (!string.IsNullOrWhiteSpace(plan.Script)) return plan with { Calls = [] };
        // Some tool-tuned models still reply with execute_script even though no tools were offered.
        // Treat that code as an artifact, NEVER as a command to execute.
        if (plan.Calls is [{ Name: "execute_script", Arguments: var args }] &&
            args.ValueKind == System.Text.Json.JsonValueKind.Object && args.TryGetProperty("code", out var code) &&
            code.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(code.GetString()))
            return new(plan.Summary, code.GetString()!);
        throw new InvalidDataException("The Assistant did not return a script. No operations were executed; ask it to return JavaScript code.");
    }
}
