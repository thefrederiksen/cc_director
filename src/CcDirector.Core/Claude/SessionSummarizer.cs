using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Generates natural language summaries of completed turns using Claude haiku.
/// </summary>
public static class SessionSummarizer
{
    /// <summary>
    /// Generate a natural language summary of a completed turn using Claude haiku.
    /// </summary>
    public static async Task<string> SummarizeTurnAsync(
        ClaudeClient client, TurnData turn, int turnNumber, CancellationToken ct = default)
    {
        FileLog.Write($"[SessionSummarizer] SummarizeTurnAsync: turn={turnNumber}, prompt={turn.UserPrompt.Length} chars, tools={turn.ToolsUsed.Count}");

        // Simple prompt with no tool use — just show the prompt directly, no AI needed
        if (turn.ToolsUsed.Count == 0 && turn.UserPrompt.Length < 50)
        {
            FileLog.Write($"[SessionSummarizer] SummarizeTurnAsync: skipping AI for simple prompt");
            return $"Asked: {turn.UserPrompt}";
        }

        var prompt = BuildSummarizationPrompt(turn, turnNumber);
        var response = await client.ChatAsync(prompt, new ClaudeOptions
        {
            Model = "haiku",
            MaxTurns = 1,
            SkipPermissions = true,
            SystemPrompt = "You are a concise summarizer. Summarize the coding turn in 1-2 sentences. "
                         + "Maximum 200 characters. "
                         + "Focus on WHAT was done, not HOW. No markdown, no bullet points, just plain text.",
        }, ct);

        var summary = response.Result.Trim();
        if (summary.Length > 200)
            summary = summary[..197] + "...";
        FileLog.Write($"[SessionSummarizer] SummarizeTurnAsync completed: turn={turnNumber}, summaryLen={summary.Length}");
        return summary;
    }

    /// <summary>
    /// Build a prompt describing the turn data for summarization.
    /// </summary>
    internal static string BuildSummarizationPrompt(TurnData turn, int turnNumber)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Summarize this coding session turn #{turnNumber}:");
        sb.AppendLine($"User asked: {Truncate(turn.UserPrompt, 500)}");

        if (turn.ToolsUsed.Count > 0)
            sb.AppendLine($"Tools used: {string.Join(", ", turn.ToolsUsed)}");

        if (turn.FilesTouched.Count > 0)
            sb.AppendLine($"Files touched: {string.Join(", ", turn.FilesTouched.Select(Path.GetFileName))}");

        if (turn.BashCommands.Count > 0)
            sb.AppendLine($"Commands run: {string.Join(", ", turn.BashCommands.Take(5).Select(c => Truncate(c, 80)))}");

        return sb.ToString();
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text;
        return text[..maxLength] + "...";
    }
}
