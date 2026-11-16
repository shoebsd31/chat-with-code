using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Chat;

namespace ChatWithCode.VectorStores.AzureAISearch;

/// <summary>
/// Stores chunks in an Azure AI Search index. Search is native hybrid: BM25 keyword search
/// over content, symbol and path plus HNSW (cosine) vector search, merged by the service with
/// reciprocal rank fusion. Every query is pre-filtered to the allowed branches.
/// </summary>
public sealed class AzureAISearchStore(SearchIndexClient indexes, string indexName) : IVectorStore
{
    private const string VectorProfile = "cwc-vector-profile";
    private const string Algorithm = "cwc-hnsw";
    private const int UploadBatchSize = 100;

    private static readonly string[] SelectFields = ["id", "repositoryId", "branch", "path", "language", "startLine", "endLine", "symbol", "content", "commitId"];

    private SearchClient Documents => indexes.GetSearchClient(indexName);

    /// <summary>Create the index if it's missing. An existing index must have the same vector size.</summary>
    public async Task EnsureIndexAsync(IndexSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        try
        {
            var existing = (await indexes.GetIndexAsync(indexName, cancellationToken)).Value;
            var dimensions = existing.Fields.FirstOrDefault(f => f.Name == "embedding")?.VectorSearchDimensions;
            if (dimensions != spec.Dimensions)
            {
                throw new InvalidOperationException(
                    $"The Azure AI Search index '{indexName}' stores {dimensions?.ToString(CultureInfo.InvariantCulture) ?? "no"} vector dimensions, " +
                    $"but the embedding settings use {spec.Dimensions}. An index can't change its vector size: use a new index name in the settings.");
            }

            return;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Not created yet.
        }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            throw AccessError(ex);
        }

        var index = new SearchIndex(indexName)
        {
            Fields =
            {
                new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true, IsSortable = true },
                new SimpleField("target", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("repositoryId", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("branch", SearchFieldDataType.String) { IsFilterable = true },
                new SearchableField("path") { IsFilterable = true },
                new SimpleField("language", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("startLine", SearchFieldDataType.Int32),
                new SimpleField("endLine", SearchFieldDataType.Int32),
                new SearchableField("symbol"),
                new SearchableField("content"),
                new SimpleField("commitId", SearchFieldDataType.String),
                new SearchField("embedding", SearchFieldDataType.Collection(SearchFieldDataType.Single))
                {
                    IsSearchable = true,
                    VectorSearchDimensions = spec.Dimensions,
                    VectorSearchProfileName = VectorProfile,
                },
            },
            VectorSearch = new VectorSearch
            {
                Profiles = { new VectorSearchProfile(VectorProfile, Algorithm) },
                Algorithms = { new HnswAlgorithmConfiguration(Algorithm) { Parameters = new HnswParameters { Metric = VectorSearchAlgorithmMetric.Cosine } } },
            },
        };

        try
        {
            await indexes.CreateOrUpdateIndexAsync(index, cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            throw AccessError(ex);
        }
    }

    public async Task UpsertAsync(IReadOnlyList<CodeChunk> chunks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        foreach (var batch in chunks.Chunk(UploadBatchSize))
        {
            var documents = batch.Select(c => new SearchDocument
            {
                ["id"] = c.Id,
                ["target"] = TargetKey(c.Target),
                ["repositoryId"] = c.Target.RepositoryId.ToString(),
                ["branch"] = c.Target.Branch,
                ["path"] = c.Path,
                ["language"] = c.Language,
                ["startLine"] = c.StartLine,
                ["endLine"] = c.EndLine,
                ["symbol"] = c.Symbol,
                ["content"] = c.Content,
                ["commitId"] = c.CommitId,
                ["embedding"] = c.Embedding,
            });
            await Run(() => Documents.IndexDocumentsAsync(IndexDocumentsBatch.MergeOrUpload(documents), new IndexDocumentsOptions { ThrowOnAnyError = true }, cancellationToken));
        }
    }

    public async Task DeleteFilesAsync(RepoBranch target, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var batch in paths.Chunk(20))
        {
            var filter = $"target eq '{TargetKey(target)}' and ({string.Join(" or ", batch.Select(p => $"path eq '{Escape(p)}'"))})";
            await DeleteMatchingAsync(filter, cancellationToken);
        }
    }

    public Task DeleteBranchAsync(RepoBranch target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        return DeleteMatchingAsync($"target eq '{TargetKey(target)}'", cancellationToken);
    }

    public async Task<IReadOnlyList<SearchHit>> HybridSearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.AllowedTargets.Count == 0)
        {
            return [];
        }

        var options = new SearchOptions
        {
            Filter = AllowedFilter(query.AllowedTargets),
            Size = query.Top,
            SearchMode = SearchMode.Any,
            QueryType = SearchQueryType.Simple,
            VectorSearch = new VectorSearchOptions
            {
                Queries = { new VectorizedQuery(query.Embedding) { KNearestNeighborsCount = Math.Max(query.Top, 40), Fields = { "embedding" } } },
                FilterMode = VectorFilterMode.PreFilter,
            },
        };
        foreach (var field in SelectFields)
        {
            options.Select.Add(field);
        }

        options.SearchFields.Add("content");
        options.SearchFields.Add("symbol");
        options.SearchFields.Add("path");

        // Plain terms only, so nothing in the question is read as query syntax.
        var terms = string.Join(' ', KeywordQuery.Terms(query.Text));
        var response = await Run(() => Documents.SearchAsync<SearchDocument>(terms.Length > 0 ? terms : "*", options, cancellationToken));

        var hits = new List<SearchHit>();
        await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
        {
            var d = result.Document;
            var target = new RepoBranch(Guid.Parse((string)d["repositoryId"]), (string)d["branch"]);
            var chunk = new CodeChunk(
                (string)d["id"], target, (string)d["path"], (string)d["language"],
                Convert.ToInt32(d["startLine"], CultureInfo.InvariantCulture), Convert.ToInt32(d["endLine"], CultureInfo.InvariantCulture),
                d.TryGetValue("symbol", out var symbol) ? symbol as string : null,
                (string)d["content"], (string)d["commitId"], []);
            hits.Add(new SearchHit(chunk, result.Score ?? 0));
        }

        return hits;
    }

    /// <summary>A short, filter-safe key for a repository branch (branch names can contain any character).</summary>
    public static string TargetKey(RepoBranch target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var branchHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(target.Branch)))[..16];
        return $"{target.RepositoryId:N}-{branchHash}";
    }

    public static string AllowedFilter(IReadOnlyList<RepoBranch> targets) =>
        $"search.in(target, '{string.Join(',', targets.Select(TargetKey))}', ',')";

    /// <summary>Find every matching document id (page by page), then delete them in batches.</summary>
    private async Task DeleteMatchingAsync(string filter, CancellationToken cancellationToken)
    {
        const int PageSize = 1000;
        var ids = new List<string>();
        for (var skip = 0; ; skip += PageSize)
        {
            var options = new SearchOptions { Filter = filter, Size = PageSize, Skip = skip };
            options.Select.Add("id");
            options.OrderBy.Add("id");
            var response = await Run(() => Documents.SearchAsync<SearchDocument>("*", options, cancellationToken));
            var page = 0;
            await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
            {
                ids.Add((string)result.Document["id"]);
                page++;
            }

            if (page < PageSize)
            {
                break;
            }
        }

        foreach (var batch in ids.Chunk(PageSize))
        {
            await Run(() => Documents.IndexDocumentsAsync(IndexDocumentsBatch.Delete("id", batch), new IndexDocumentsOptions { ThrowOnAnyError = true }, cancellationToken));
        }
    }

    private async Task<T> Run<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            throw AccessError(ex);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new InvalidOperationException($"The Azure AI Search index '{indexName}' doesn't exist. It's created on the next indexing run.", ex);
        }
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static InvalidOperationException AccessError(RequestFailedException ex) => new(
        "Azure AI Search refused the request. Check the admin API key, or that the service's managed identity has the " +
        "'Search Index Data Contributor' and 'Search Service Contributor' roles on the search service.", ex);
}
