using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json.Nodes;

namespace ChatWithCode.AzureOpenAI;

/// <summary>
/// Azure.AI.OpenAI 2.1 sends the answer-length limit as the legacy "max_tokens" field, which
/// reasoning models (GPT-5 and later, including the GPT-6 family) reject. Its built-in switch
/// for the newer field (SetNewMaxCompletionTokensPropertyEnabled) throws in 2.1, so this policy
/// renames the field to "max_completion_tokens" on chat completion requests instead.
/// </summary>
internal sealed class MaxCompletionTokensPolicy : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Rewrite(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Rewrite(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    internal static void Rewrite(PipelineMessage message)
    {
        var request = message.Request;
        if (request.Content is null || request.Uri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) != true)
        {
            return;
        }

        using var buffer = new MemoryStream();
        request.Content.WriteTo(buffer);
        if (JsonNode.Parse(buffer.ToArray()) is not JsonObject body || !body.TryGetPropertyValue("max_tokens", out var value))
        {
            return;
        }

        body.Remove("max_tokens");
        body["max_completion_tokens"] = value;
        request.Content = BinaryContent.Create(BinaryData.FromString(body.ToJsonString()));
    }
}
