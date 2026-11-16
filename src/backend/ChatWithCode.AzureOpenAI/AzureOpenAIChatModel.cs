using System.ClientModel;
using System.Runtime.CompilerServices;
using Azure.AI.OpenAI;
using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;
using OpenAI.Chat;

namespace ChatWithCode.AzureOpenAI;

/// <summary>Streams answers from an Azure OpenAI chat deployment.</summary>
public sealed class AzureOpenAIChatModel(AzureOpenAIClient client, string deployment) : IChatModel
{
    private readonly ChatClient _chat = client.GetChatClient(deployment);

    public async IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var messages = new List<OpenAI.Chat.ChatMessage> { new SystemChatMessage(request.SystemPrompt) };
        messages.AddRange(request.Messages.Select(m => m.Role == MessageRole.User
            ? (OpenAI.Chat.ChatMessage)new UserChatMessage(m.Content)
            : new AssistantChatMessage(m.Content)));

        // No temperature: reasoning models (GPT-5 and later) reject it.
        var options = new ChatCompletionOptions();
        if (request.MaxOutputTokens is { } max)
        {
            // Sent as max_completion_tokens (see MaxCompletionTokensPolicy).
            options.MaxOutputTokenCount = max;
        }

        AsyncCollectionResult<StreamingChatCompletionUpdate> updates;
        try
        {
            updates = _chat.CompleteChatStreamingAsync(messages, options, cancellationToken);
        }
        catch (ClientResultException ex)
        {
            throw Translate(ex);
        }

        await using var enumerator = updates.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            StreamingChatCompletionUpdate update;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }

                update = enumerator.Current;
            }
            catch (ClientResultException ex)
            {
                throw Translate(ex);
            }

            var text = string.Concat(update.ContentUpdate.Select(p => p.Text));
            if (update.Usage is { } usage)
            {
                yield return new ChatDelta(text, usage.InputTokenCount, usage.OutputTokenCount);
            }
            else if (text.Length > 0)
            {
                yield return new ChatDelta(text);
            }
        }
    }

    private static InvalidOperationException Translate(ClientResultException ex) => ex.Status switch
    {
        401 or 403 => new InvalidOperationException(
            "Azure OpenAI refused the request. Check the API key, or that the backend's managed identity has the " +
            "'Cognitive Services OpenAI User' role on the Azure OpenAI resource.", ex),
        404 => new InvalidOperationException("Azure OpenAI couldn't find the chat deployment. Check the deployment name.", ex),
        429 => new InvalidOperationException("Azure OpenAI is busy (rate limit reached). Try again in a minute.", ex),
        400 when ex.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase) =>
            new InvalidOperationException("Azure OpenAI's content filter blocked this request. Try rephrasing the question.", ex),
        _ => new InvalidOperationException($"Azure OpenAI returned an error ({ex.Status}).", ex),
    };
}

/// <summary>Creates the chat model from the effective settings.</summary>
public sealed class AzureOpenAIChatModelFactory(HttpClient? transport = null) : IChatModelFactory
{
    public IChatModel Create(EffectiveModelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Provider != ModelProviderKind.AzureOpenAI)
        {
            throw new NotSupportedException($"Model provider {settings.Provider} isn't supported yet.");
        }

        if (string.IsNullOrWhiteSpace(settings.ChatDeployment))
        {
            throw SettingsIncompleteException.For(["chat deployment"]);
        }

        return new AzureOpenAIChatModel(AzureOpenAIEmbedderFactory.CreateClient(settings, transport), settings.ChatDeployment);
    }
}
