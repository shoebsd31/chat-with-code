using ChatWithCode.Core.Indexing;
using ChatWithCode.Core.Indexing.Chunking;

namespace ChatWithCode.Tests.Core;

/// <summary>
/// Structure-aware chunking for non-C# languages. Every sample has a class too big for one chunk,
/// so it must be split by member, and each member must stay whole in one chunk with a qualified name.
/// </summary>
public sealed class StructuredChunkerTests
{
    private static readonly Cl100kTokenCounter Tokens = new();
    private static readonly ChunkingOptions Small = new() { MaxTokens = 200, OverlapLines = 2 };

    private static string Body(string indent, string end = ";") =>
        string.Join('\n', Enumerable.Range(1, 6).Select(i => $"{indent}total = total + {i} * factor - offset{end}"));

    public static TheoryData<string, string, string[]> Samples() => new()
    {
        {
            "web/orderService.ts",
            $$"""
            import { Order } from "./order";
            import type { Logger } from "./log";

            /** Saves orders. */
            export class OrderService {
              private readonly items: string[] = [];

              /** Submits an order. */
              @Traced("submit")
              async submit(order: Order): Promise<void> { // start OrderService.submit
            {{Body("    ")}}
                const brace = "}"; const re = /\{+/g; const t = `${order.id} }`;
              } // end OrderService.submit

              cancel(id: string): boolean { // start OrderService.cancel
            {{Body("    ")}}
                return true;
              } // end OrderService.cancel
            }

            export const helper = async (a: number): Promise<number> => { // start helper
            {{Body("  ")}}
              return a;
            }; // end helper
            """,
            ["OrderService.submit", "OrderService.cancel", "helper"]
        },
        {
            "app/orders.py",
            $$"""
            import os
            from typing import List

            class OrderService:
                '''Saves orders. A } in a docstring.'''

                @traced  # start OrderService.submit
                def submit(self, order):
            {{Body("        ", "")}}
                    brace = "}"  # end OrderService.submit

                def cancel(  # start OrderService.cancel
                    self,
                    order_id,
                ):
            {{Body("        ", "")}}
                    return True  # end OrderService.cancel


            async def helper(a):  # start helper
            {{Body("    ", "")}}
                return a  # end helper
            """,
            ["OrderService.submit", "OrderService.cancel", "helper"]
        },
        {
            "src/main/java/OrderService.java",
            $$"""
            package com.contoso.orders;

            import java.util.List;

            public class OrderService {
                private final List<String> items = new ArrayList<>();

                /** Submits an order. */ // start OrderService.submit
                @PostMapping("/orders/{id}")
                public <T> List<T> submit(@RequestBody Order order, char c) throws IOException {
            {{Body("        ")}}
                    char brace = '{';
                } // end OrderService.submit

                public boolean cancel(String id) { // start OrderService.cancel
            {{Body("        ")}}
                    if (id == null) { return false; }
                    return true;
                } // end OrderService.cancel
            }
            """,
            ["OrderService.submit", "OrderService.cancel"]
        },
        {
            "server/server.go",
            $$"""
            package server

            import "fmt"

            type Server struct { // start Server
                Name string
            } // end Server

            // Start runs the server. // start Server.Start
            func (s *Server) Start(port int) error {
            {{Body("    ", "")}}
                fmt.Println(`raw { string`)
                return nil
            } // end Server.Start

            func helper(a int) int { // start helper
            {{Body("    ", "")}}
                return a
            } // end helper
            """,
            ["Server", "Server.Start", "helper"]
        },
        {
            "src/orders.rs",
            $$"""
            use std::fmt;

            pub struct Order<'a> { pub id: &'a str }

            impl<'a> fmt::Display for Order<'a> {
                fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result { // start Order.fmt
            {{Body("        ")}}
                    let c = '{';
                    write!(f, "{}", self.id)
                } // end Order.fmt

                /// Cancels. // start Order.cancel
                pub fn cancel(&mut self) -> bool {
            {{Body("        ")}}
                    true
                } // end Order.cancel
            }
            """,
            ["Order.fmt", "Order.cancel"]
        },
        {
            "app/OrderService.kt",
            $$"""
            package com.contoso

            import kotlin.math.max

            data class OrderService(val name: String) : Base() {
                fun submit(order: Order): Boolean { // start OrderService.submit
            {{Body("        ", "")}}
                    val s = "${order.id} }"
                    items.forEach { println(it) }
                    return true
                } // end OrderService.submit

                suspend fun cancel(id: String) { // start OrderService.cancel
            {{Body("        ", "")}}
                } // end OrderService.cancel
            }
            """,
            ["OrderService.submit", "OrderService.cancel"]
        },
        {
            "src/orders.cpp",
            $$"""
            #include <string>
            #define OPEN {

            namespace contoso {

            // Submits an order. // start contoso.OrderService.submit
            int OrderService::submit(const Order& order) const {
            {{Body("    ")}}
                const char* s = "}";
                return 0;
            } // end contoso.OrderService.submit

            static int helper(int a) { // start contoso.helper
            {{Body("    ")}}
                for (int i = 0; i < a; i++) { a--; }
                return a;
            } // end contoso.helper

            }
            """,
            ["contoso.OrderService.submit", "contoso.helper"]
        },
        {
            "src/OrderController.php",
            $$"""
            <?php
            namespace App\Http;

            use App\Models\Order;

            class OrderController extends Controller
            {
                # start OrderController.store
                public function store(Request $request): Response
                {
            {{Body("        ")}}
                    $s = '}';
                    return response();
                } // end OrderController.store

                public function destroy($id) { // start OrderController.destroy
            {{Body("        ")}}
                } // end OrderController.destroy
            }
            """,
            ["OrderController.store", "OrderController.destroy"]
        },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Members_stay_whole_with_qualified_names(string path, string code, string[] symbols)
    {
        var language = LanguageDetector.Detect(path);
        Assert.True(StructuredChunker.Supports(language), language);

        var chunks = new ChunkerSelector(Tokens, Small).For(language).Chunk(path, code);

        Assert.True(chunks.Count > 1, "the sample should be too big for one chunk");
        var lines = code.Split('\n');
        foreach (var symbol in symbols)
        {
            var start = Array.FindIndex(lines, l => l.Contains($"start {symbol}", StringComparison.Ordinal)) + 1;
            var end = Array.FindIndex(lines, l => l.Contains($"end {symbol}", StringComparison.Ordinal)) + 1;
            Assert.True(start > 0 && end >= start, $"markers for {symbol}");

            var chunk = chunks.FirstOrDefault(c => c.Symbol == symbol);
            Assert.True(chunk is not null, $"no chunk named {symbol}; got: {string.Join(", ", chunks.Select(c => $"{c.Symbol} {c.StartLine}-{c.EndLine}"))}");
            Assert.True(chunk.StartLine <= start && chunk.EndLine >= end,
                $"{symbol} should cover lines {start}-{end} but covers {chunk.StartLine}-{chunk.EndLine}");
        }

        // Nothing is lost except import lines and lines of only punctuation (a closing "}").
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.Any(char.IsLetterOrDigit) || line.StartsWith("import", StringComparison.Ordinal) || line.StartsWith("from ", StringComparison.Ordinal) ||
                line.StartsWith("use ", StringComparison.Ordinal) || line.StartsWith("package ", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.Contains(chunks, c => c.StartLine <= i + 1 && i + 1 <= c.EndLine);
        }

        Assert.All(chunks, c => Assert.True(Tokens.Count(c.Content) <= Small.MaxTokens + 5, $"{c.Symbol} at {c.StartLine} is too big"));
    }

    [Fact]
    public void Small_file_stays_whole_named_by_its_first_declaration()
    {
        var chunks = new ChunkerSelector(Tokens).For("typescript").Chunk("a.ts", "import x from 'y';\n\nexport function greet(name: string) {\n  return `hi ${name}`;\n}\n");

        var chunk = Assert.Single(chunks);
        Assert.Equal("greet", chunk.Symbol);
    }

    [Fact]
    public void Unreadable_structure_falls_back_to_blank_line_blocks()
    {
        // More closing than opening braces: the structure can't be trusted.
        var text = "}\n\n" + string.Join("\n\n", Enumerable.Range(1, 30).Select(i => $"function f{i}(a) {{\n  return a + {i};\n}}"));

        var structured = new ChunkerSelector(Tokens, Small).For("javascript").Chunk("broken.js", text);
        var generic = new GenericChunker(Tokens, Small).Chunk("broken.js", text);

        Assert.Equal(generic, structured);
    }

    [Fact]
    public void Control_blocks_and_calls_are_not_declarations()
    {
        var text = $$"""
            export function run(items: Item[]) {
            {{Body("  ")}}
              if (items.length > 0) { log("x"); }
              items.forEach((item) => { save(item); });
              describe("suite", () => { it("works", () => { expect(1).toBe(1); }); });
              for (const object of items) { use(object); }
            }

            export function other() {
            {{Body("  ")}}
            }
            """;

        var chunks = new ChunkerSelector(Tokens, Small).For("typescript").Chunk("run.ts", text);

        Assert.Equal(["run", "other"], chunks.Select(c => c.Symbol));
    }

    [Fact]
    public void React_components_with_destructured_props_and_apostrophes_in_text()
    {
        var text = $$"""
            export function MessageView({ item, onRetry }: { item: ChatItem; onRetry?(): void }) {
            {{Body("  ")}}
              return <p>Couldn't load: {item.error}</p>;
            }

            export function Sources({ citations }: { citations: Citation[] }) {
            {{Body("  ")}}
              return <ul>{citations.map((c) => <li key={c.number}>{c.path}</li>)}</ul>;
            }
            """;

        var chunks = new ChunkerSelector(Tokens, Small).For("typescript").Chunk("MessageView.tsx", text);

        Assert.Equal(["MessageView", "Sources"], chunks.Select(c => c.Symbol));
        Assert.Equal((1, 9), (chunks[0].StartLine, chunks[0].EndLine));
    }

    [Theory]
    [InlineData("typescript", 2)]
    [InlineData("python", 2)]
    [InlineData("csharp", 1)]
    [InlineData("markdown", 1)]
    [InlineData("yaml", 1)]
    public void Splitter_version_per_language(string language, int version) =>
        Assert.Equal(version, ChunkerSelector.VersionFor(language));
}
