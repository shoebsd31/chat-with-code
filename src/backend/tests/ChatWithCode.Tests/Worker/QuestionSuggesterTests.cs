using System.Runtime.CompilerServices;
using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;
using ChatWithCode.Core.Indexing;
using ChatWithCode.Core.Options;
using ChatWithCode.Data.Entities;
using ChatWithCode.Data.Settings;
using ChatWithCode.Tests.Infrastructure;
using ChatWithCode.Worker.Indexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChatWithCode.Tests.Worker;

[Collection(PostgresCollection.Name)]
public sealed class QuestionSuggesterTests(PostgresFixture postgres)
{
    private const string Reply = """["How are orders submitted?", "Where is payment handled?", "How do I run the tests?", "Draw a diagram of the order flow.", "What does the worker do?"]""";

    [Theory]
    [InlineData(Reply, 5)]
    [InlineData("```json\n[\"How are orders submitted?\", \"Where is payment handled?\"]\n```", 2)]
    [InlineData("Here you go: [\"How are orders submitted?\", \"how are orders SUBMITTED?\", \"Hi?\"]", 1)]
    [InlineData("I can't help with that.", 0)]
    [InlineData("[not json", 0)]
    public void Reads_the_questions_from_the_reply(string reply, int count) =>
        Assert.Equal(count, QuestionSuggester.Parse(reply).Count);

    [Fact]
    public void Keeps_at_most_five_short_questions()
    {
        var questions = QuestionSuggester.Parse($"[{string.Join(',', Enumerable.Range(1, 8).Select(i => $"\"Question number {i}?\""))}, \"{new string('x', 200)}\"]");

        Assert.Equal(5, questions.Count);
        Assert.All(questions, q => Assert.True(q.Length <= 141));
    }

    [Fact]
    public void Finds_the_root_readme_preferring_markdown()
    {
        AdoTreeEntry[] tree = [new("docs/README.md", "a", 1), new("README.txt", "b", 1), new("readme.md", "c", 1), new("src/app.ts", "d", 1)];

        Assert.Equal("c", QuestionSuggester.FindReadme(tree)!.BlobId);
        Assert.Equal(["README.txt", "docs/", "readme.md", "src/"], QuestionSuggester.TopLevel(tree));
    }

    [Fact]
    public async Task Writes_questions_once_per_readme_version()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var tree = h.Commit("# Orders\n\nSubmits and pays for orders.\n");

        Assert.True(await h.Suggester.EnsureAsync(h.BranchId, tree, CancellationToken.None));
        Assert.Equal(5, (await h.BranchAsync()).SuggestedQuestions!.Count);
        var request = Assert.Single(h.Model.Requests);
        Assert.Contains("Submits and pays for orders.", request.Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("src/", request.Messages[0].Content, StringComparison.Ordinal);

        // Same README: no new model call.
        Assert.False(await h.Suggester.EnsureAsync(h.BranchId, tree, CancellationToken.None));
        Assert.Single(h.Model.Requests);

        // README changed: written again.
        tree = h.Commit("# Orders\n\nNow with refunds.\n");
        Assert.True(await h.Suggester.EnsureAsync(h.BranchId, tree, CancellationToken.None));
        Assert.Equal(2, h.Model.Requests.Count);
    }

    [Fact]
    public async Task A_failing_model_keeps_the_previous_questions_and_never_throws()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var tree = h.Commit("# Orders\n");
        await h.Suggester.EnsureAsync(h.BranchId, tree, CancellationToken.None);
        var before = (await h.BranchAsync()).SuggestedQuestions;

        h.Model.Fail = true;
        tree = h.Commit("# Orders v2\n");
        Assert.False(await h.Suggester.EnsureAsync(h.BranchId, tree, CancellationToken.None));

        Assert.Equal(before, (await h.BranchAsync()).SuggestedQuestions);
    }

    [Fact]
    public async Task Nothing_is_written_without_a_chat_model()
    {
        await using var h = await Harness.CreateAsync(postgres, chatDeployment: null);
        var tree = h.Commit("# Orders\n");

        Assert.False(await h.Suggester.EnsureAsync(h.BranchId, tree, CancellationToken.None));
        Assert.Empty(h.Model.Requests);
        Assert.Null((await h.BranchAsync()).SuggestedQuestions);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(TestDb db) => Db = db;

        public TestDb Db { get; }
        public FakeAdoGitClient Ado { get; } = new();
        public QuestionModel Model { get; } = new();
        public Guid BranchId { get; private set; }
        public QuestionSuggester Suggester { get; private set; } = null!;

        public static async Task<Harness> CreateAsync(PostgresFixture postgres, string? chatDeployment = "gpt-6-luna")
        {
            var h = new Harness(await postgres.CreateMigratedDbAsync());
            var db = h.Db.Context;
            var (org, _, main) = await Seed.RepoWithMainAsync(db);
            db.Add(new ModelSettings
            {
                Id = Guid.NewGuid(),
                OrganizationId = org.Id,
                Scope = SettingScope.Organization,
                AzureOpenAIEndpoint = "https://contoso.openai.azure.com/",
                EmbeddingDeployment = "text-embedding-3-small",
                ChatDeployment = chatDeployment,
                ApiKeyProtected = "enc:secret",
                VectorStore = VectorStoreKind.PgVector,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            h.BranchId = main.Id;
            h.Suggester = new QuestionSuggester(
                db,
                h.Ado,
                new ModelSettingsResolver(db, new PlainSecretProtector(), Options.Create(new AzureDevOpsOptions { Organization = "contoso" })),
                new QuestionModelFactory(h.Model),
                new Cl100kTokenCounter(),
                NullLogger<QuestionSuggester>.Instance);
            return h;
        }

        public IReadOnlyList<AdoTreeEntry> Commit(string readme)
        {
            var commit = Ado.Commit(new() { ["README.md"] = readme, ["src/app.ts"] = "export const a = 1;\n" });
            return Ado.GetFilesAsync(Ado.Repository.Id, commit, CancellationToken.None).GetAwaiter().GetResult();
        }

        public async Task<IndexedBranch> BranchAsync()
        {
            await using var check = Db.NewContext();
            return await check.IndexedBranches.SingleAsync(b => b.Id == BranchId);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class QuestionModelFactory(QuestionModel model) : IChatModelFactory
    {
        public IChatModel Create(EffectiveModelSettings settings) => model;
    }

    private sealed class QuestionModel : IChatModel
    {
        public List<ChatRequest> Requests { get; } = [];
        public bool Fail { get; set; }

        public async IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            await Task.Yield();
            if (Fail)
            {
                throw new InvalidOperationException("The model is unavailable.");
            }

            Assert.Equal(QuestionSuggester.SystemPrompt, request.SystemPrompt);
            yield return new ChatDelta(Reply);
        }
    }
}
