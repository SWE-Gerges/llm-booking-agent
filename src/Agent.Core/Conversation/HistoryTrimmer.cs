using Agent.Abstractions.Llm;
using Agent.Abstractions.Sessions;

namespace Agent.Core.Conversation;

/// <summary>
/// Pure history trimming. Messages are dropped in fixed-size chunks from the front so the prompt prefix only changes
/// every <c>chunk</c> messages, and the new first message is always a real user message so no tool_result is left
/// without its tool_use.
/// </summary>
public static class HistoryTrimmer
{
    public static IReadOnlyList<ConversationMessage> Trim(IReadOnlyList<ConversationMessage> messages, int window, int chunk)
    {
        var start = 0;
        if (messages.Count > window)
        {
            var excess = messages.Count - window;
            start = (int)Math.Ceiling(excess / (double)chunk) * chunk;
        }

        start = Math.Min(start, messages.Count);
        while (start < messages.Count && !IsRealUserMessage(messages[start]))
        {
            start++;
        }

        return start == 0 ? messages : messages.Skip(start).ToList();
    }

    private static bool IsRealUserMessage(ConversationMessage message) =>
        message.Role == StoredMessageRole.User && !message.Blocks.OfType<ToolResultBlock>().Any();
}
