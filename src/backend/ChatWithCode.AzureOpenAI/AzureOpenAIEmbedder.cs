using System.ClientModel;
using Azure.AI.OpenAI;
using ChatWithCode.Core.Abstractions;
using OpenAI.Embeddings;

namespace ChatWithCode.AzureOpenAI;

/// <summary>
/// Embeddings from an Azure OpenAI deployment (text-embedding-3-small/large). Inputs are sent in
/// batches; the SDK retries throttled (429) and transient failures with backoff.
/// </summary>
public sealed class AzureOpenAIEmbedder : IEmbedder
{
    /// <summary>Inputs per request. Chunks are at most ~1,500 tokens, so a batch stays well under request limits.</summary>
    public const int BatchSize = 64;

    private readonly EmbeddingClient _client;

    public AzureOpenAIEmbedder(AzureOpenAIClient client, string deployment, int dimensions, string modelId)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client.GetEmbeddingClient(deployment);
        Dimensions = dimensions;
        ModelId = modelId;
    }

    public string ModelId { get; }

    public int Dimensions { get; }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var results = new List<float[]>(texts.Count);
        var options = new EmbeddingGenerationOptions { Dimensions = Dimensions };

        for (var offset = 0; offset < texts.Count; offset += BatchSize)
        {
            var batch = texts.Skip(offset).Take(BatchSize).ToList();
            ClientResult<OpenAIEmbeddingCollection> response;
            try
            {
                response = await _client.GenerateEmbeddingsAsync(batch, options, cancellationToken);
            }
            catch (ClientResultException ex) when (ex.Status is 401 or 403)
            {
                throw new InvalidOperationException(
                    "Azure OpenAI refused the request. Check the API key, or that the backend's managed identity has the " +
                    "'Cognitive Services OpenAI User' role on the Azure OpenAI resource.", ex);
            }
            catch (ClientResultException ex) when (ex.Status == 404)
            {
                throw new InvalidOperationException(
                    "Azure OpenAI couldn't find the embedding deployment. Check the endpoint and the deployment name.", ex);
            }

            // The service returns items with an index; order by it so vectors line up with inputs.
            foreach (var embedding in response.Value.OrderBy(e => e.Index))
            {
                var vector = embedding.ToFloats().ToArray();
                if (vector.Length != Dimensions)
                {
                    throw new InvalidOperationException(
                        $"The embedding deployment returned {vector.Length} dimensions, but {Dimensions} were configured. " +
                        "Use a text-embedding-3 model, which supports choosing the dimension count.");
                }

                results.Add(vector);
            }
        }

        return results;
    }
}
