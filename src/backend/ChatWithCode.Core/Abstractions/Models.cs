using ChatWithCode.Core.Domain;

namespace ChatWithCode.Core.Abstractions;

/// <summary>A chat model that streams its answer. v1 implementation: Azure OpenAI.</summary>
public interface IChatModel
{
    IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, CancellationToken cancellationToken);
}

/// <summary>Turns text into vectors. Every index records which model and size built it.</summary>
public interface IEmbedder
{
    string ModelId { get; }
    int Dimensions { get; }
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

public sealed record ChatMessage(MessageRole Role, string Content);

public sealed record ChatRequest(string SystemPrompt, IReadOnlyList<ChatMessage> Messages, int? MaxOutputTokens = null);

/// <summary>One streamed piece of an answer. Token counts arrive on the final delta.</summary>
public sealed record ChatDelta(string Text, int? PromptTokens = null, int? CompletionTokens = null);

public static class ChatModelExtensions
{
    /// <summary>Run a request to completion and return the whole text (for short internal calls).</summary>
    public static async Task<string> CompleteAsync(this IChatModel model, ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        var text = new System.Text.StringBuilder();
        await foreach (var delta in model.StreamAsync(request, cancellationToken))
        {
            text.Append(delta.Text);
        }

        return text.ToString();
    }
}
