using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PresenterAi.Application.Tools;
using Xunit;

namespace PresenterAi.Application.Tests.Tools;

public sealed class ToolSessionCatalogueTests
{
    [Fact]
    public void Threshold_16_with_16_tools_inlines_all_tools_without_meta_tools()
    {
        var registry = new ToolRegistry();
        for (var i = 1; i <= 16; i++)
        {
            registry.Register(CreateTool($"tool_{i:D2}", pinned: i <= 2));
        }

        var catalogue = registry.CreateCatalogue(maxInlineTools: 16);

        Assert.Equal(16, catalogue.InlineTools.Count);
        Assert.DoesNotContain(catalogue.InlineTools, t => t.Name == "find_tools");
        Assert.DoesNotContain(catalogue.InlineTools, t => t.Name == "call_tool");
    }

    [Fact]
    public void Threshold_16_with_17_tools_inlines_only_pinned_plus_meta_tools()
    {
        var registry = new ToolRegistry();
        for (var i = 1; i <= 17; i++)
        {
            registry.Register(CreateTool($"tool_{i:D2}", pinned: i <= 5));
        }

        var catalogue = registry.CreateCatalogue(maxInlineTools: 16);

        // 5 pinned + find_tools + call_tool = 7 inline tools
        Assert.Equal(7, catalogue.InlineTools.Count);
        Assert.Contains(catalogue.InlineTools, t => t.Name == "find_tools");
        Assert.Contains(catalogue.InlineTools, t => t.Name == "call_tool");
        Assert.Equal(7, catalogue.InlineTools.Count(t => t.Pinned));
    }

    [Fact]
    public void Threshold_0_with_5_tools_inlines_pinned_plus_meta_tools()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateTool("pinned_1", pinned: true));
        registry.Register(CreateTool("pinned_2", pinned: true));
        registry.Register(CreateTool("unpinned_1", pinned: false));
        registry.Register(CreateTool("unpinned_2", pinned: false));
        registry.Register(CreateTool("unpinned_3", pinned: false));

        var catalogue = registry.CreateCatalogue(maxInlineTools: 0);

        // 2 pinned + 2 meta = 4 inline
        Assert.Equal(4, catalogue.InlineTools.Count);
        Assert.Contains(catalogue.InlineTools, t => t.Name == "pinned_1");
        Assert.Contains(catalogue.InlineTools, t => t.Name == "pinned_2");
        Assert.Contains(catalogue.InlineTools, t => t.Name == "find_tools");
        Assert.Contains(catalogue.InlineTools, t => t.Name == "call_tool");
    }

    [Fact]
    public void Threshold_1_with_2_pinned_tools_inlines_both_pinned_plus_meta_tools()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateTool("pinned_1", pinned: true));
        registry.Register(CreateTool("pinned_2", pinned: true));

        var catalogue = registry.CreateCatalogue(maxInlineTools: 1);

        // 2 pinned tools registered > threshold 1 => 2 pinned + 2 meta = 4
        Assert.Equal(4, catalogue.InlineTools.Count);
        Assert.Contains(catalogue.InlineTools, t => t.Name == "pinned_1");
        Assert.Contains(catalogue.InlineTools, t => t.Name == "pinned_2");
        Assert.Contains(catalogue.InlineTools, t => t.Name == "find_tools");
        Assert.Contains(catalogue.InlineTools, t => t.Name == "call_tool");
    }

    [Fact]
    public void All_pinned_fixture_with_12_pinned_tools_above_threshold_inlines_all_pinned_plus_meta_tools()
    {
        var registry = new ToolRegistry();
        for (var i = 1; i <= 12; i++)
        {
            registry.Register(CreateTool($"pinned_{i:D2}", pinned: true));
        }

        var catalogue = registry.CreateCatalogue(maxInlineTools: 5);

        // 12 pinned + 2 meta = 14 inline tools
        Assert.Equal(14, catalogue.InlineTools.Count);
        for (var i = 1; i <= 12; i++)
        {
            var expectedName = $"pinned_{i:D2}";
            Assert.Contains(catalogue.InlineTools, t => t.Name == expectedName);
        }
        Assert.Contains(catalogue.InlineTools, t => t.Name == "find_tools");
        Assert.Contains(catalogue.InlineTools, t => t.Name == "call_tool");
    }

    [Fact]
    public async Task Unicode_fixture_handles_non_ascii_names_descriptions_tags_and_results()
    {
        var registry = new ToolRegistry();
        var unicodeTool = new TestTool(
            "unicode_tool_test",
            "日本語の説明とギリシャ文字 αβγδ and emojis 🚀",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["greeting"] = new JsonObject { ["type"] = "string" }
                },
                ["required"] = new JsonArray { "greeting" },
                ["additionalProperties"] = false
            },
            ["東京", "café", "naïve"],
            pinned: true,
            handler: args =>
            {
                var val = args.GetProperty("greeting").GetString();
                return Task.FromResult(ToolResult.Success($"こんにちは {val} 🌟"));
            });

        registry.Register(unicodeTool);
        var catalogue = registry.CreateCatalogue(maxInlineTools: 16);

        Assert.Single(catalogue.InlineTools, t => t.Name == "unicode_tool_test");
        var res = await catalogue.InvokeAsync("unicode_tool_test", ParseJson("{\"greeting\":\"世界\"}"));

        Assert.True(res.Ok);
        Assert.Equal("こんにちは 世界 🌟", res.Message);
    }

    [Fact]
    public void One_hundred_tools_inlines_only_pinned_and_meta_tools_under_32KiB_budget()
    {
        var registry = new ToolRegistry();

        // 6 pinned tools, 94 unpinned tools = 100 tools total
        for (var i = 1; i <= 6; i++)
        {
            registry.Register(CreateTool($"pinned_tool_{i:D2}", description: $"Pinned tool description {i}", pinned: true));
        }

        for (var i = 7; i <= 100; i++)
        {
            registry.Register(CreateTool(
                $"searchable_tool_{i:D3}",
                description: $"Detailed description for searchable tool number {i} to realistically simulate catalogue entries.",
                tags: [$"tag_{i}", "general", "utility"],
                pinned: false));
        }

        Assert.Equal(100, registry.Count);

        var catalogue = registry.CreateCatalogue(maxInlineTools: 16);

        // 6 pinned + find_tools + call_tool = 8 inline tools
        Assert.Equal(8, catalogue.InlineTools.Count);
        var expectedNames = new[]
        {
            "pinned_tool_01", "pinned_tool_02", "pinned_tool_03",
            "pinned_tool_04", "pinned_tool_05", "pinned_tool_06",
            "find_tools", "call_tool"
        };
        foreach (var expectedName in expectedNames)
        {
            Assert.Contains(catalogue.InlineTools, t => t.Name == expectedName);
        }

        var definitions = catalogue.GetInlineToolDefinitions();
        Assert.Equal(8, definitions.Count);

        var jsonArray = new JsonArray(definitions.Select(d => (JsonNode)d.DeepClone()).ToArray());
        var serializedPayload = jsonArray.ToJsonString();
        var payloadBytes = Encoding.UTF8.GetByteCount(serializedPayload);

        // Assert strictly under the 32 KiB budget (32,768 bytes)
        Assert.True(payloadBytes < 32 * 1024, $"Payload size was {payloadBytes} bytes, expected < 32 KiB");
        Assert.True(payloadBytes < 4096, "8 tools definitions should be well under 4 KiB");
    }

    [Fact]
    public void Exceeding_32KiB_budget_throws_InvalidOperationException()
    {
        var hugeTools = new List<ITool>();
        // 12 pinned tools with ~3 KiB schemas each = ~36 KiB > 32 KiB
        for (var i = 1; i <= 12; i++)
        {
            var bigProps = new JsonObject();
            for (var p = 0; p < 80; p++)
            {
                bigProps[$"field_{p:D3}"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "A lengthy parameter explanation that consumes bytes in the serialized JSON schema."
                };
            }

            hugeTools.Add(new TestTool(
                $"huge_tool_{i:D2}",
                new string('d', 500),
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = bigProps
                },
                ["huge"],
                pinned: true,
                _ => Task.FromResult(ToolResult.Success("ok"))));
        }

        var ex = Assert.Throws<InvalidOperationException>(() => new ToolSessionCatalogue(hugeTools, maxInlineTools: 16));
        Assert.Contains("exceeds the 32 KiB budget", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("controller", "nav_controller", "slide_overview", "voice_settings")]
    [InlineData("slides", "slide_overview", "slide_notes", null)]
    [InlineData("navigation", "nav_controller", null, null)]
    [InlineData("timer", "timer_widget", null, null)]
    [InlineData("speaker", "slide_notes", null, null)]
    public async Task Find_tools_ranking_table_verifies_keyword_weights_and_plural_stripping(
        string query,
        string expectedFirst,
        string? expectedSecond,
        string? expectedThird)
    {
        var registry = new ToolRegistry();

        // Fixture tools where terms appear in name (x3), tags (x2), or description (x1)
        registry.Register(CreateTool(
            name: "nav_controller",
            description: "Move between parts of the deck",
            tags: ["navigation", "direction"]));

        registry.Register(CreateTool(
            name: "slide_overview",
            description: "Show slides list",
            tags: ["controller", "summary"]));

        registry.Register(CreateTool(
            name: "voice_settings",
            description: "Adjust volume and audio controller settings",
            tags: ["audio", "sound"]));

        registry.Register(CreateTool(
            name: "timer_widget",
            description: "Presentation countdown timer",
            tags: ["time", "clock"]));

        registry.Register(CreateTool(
            name: "slide_notes",
            description: "Display notes for current slide",
            tags: ["speaker", "script"]));

        // Above threshold so find_tools is active
        var catalogue = registry.CreateCatalogue(maxInlineTools: 2);
        var findTools = catalogue.FindTool("find_tools");
        Assert.NotNull(findTools);

        var result = await findTools!.InvokeAsync(ParseJson($"{{\"query\":\"{query}\"}}"));

        Assert.True(result.Ok);
        Assert.NotNull(result.Data);

        var matches = result.Data!.AsArray();
        Assert.NotEmpty(matches);

        Assert.Equal(expectedFirst, matches[0]!["name"]!.GetValue<string>());

        if (expectedSecond is not null)
        {
            Assert.True(matches.Count >= 2);
            Assert.Equal(expectedSecond, matches[1]!["name"]!.GetValue<string>());
        }

        if (expectedThird is not null)
        {
            Assert.True(matches.Count >= 3);
            Assert.Equal(expectedThird, matches[2]!["name"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Call_tool_with_unknown_name_returns_ok_false_with_reason()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateTool("real_tool"));
        var catalogue = registry.CreateCatalogue(maxInlineTools: 0);

        var callTool = catalogue.FindTool("call_tool");
        Assert.NotNull(callTool);

        var result = await callTool!.InvokeAsync(ParseJson("{\"name\":\"non_existent\",\"arguments\":{}}"));

        Assert.False(result.Ok);
        Assert.Contains("Unknown tool: 'non_existent'", result.Message);
    }

    [Fact]
    public async Task Call_tool_with_missing_required_argument_returns_ok_false_with_reason()
    {
        var registry = new ToolRegistry();
        registry.Register(new TestTool(
            "strict_tool",
            "Tool with required arg",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["slide_number"] = new JsonObject { ["type"] = "integer" }
                },
                ["required"] = new JsonArray { "slide_number" },
                ["additionalProperties"] = false
            },
            [],
            pinned: false,
            _ => Task.FromResult(ToolResult.Success("ok"))));

        var catalogue = registry.CreateCatalogue(maxInlineTools: 0);
        var callTool = catalogue.FindTool("call_tool");
        Assert.NotNull(callTool);

        var result = await callTool!.InvokeAsync(ParseJson("{\"name\":\"strict_tool\",\"arguments\":{}}"));

        Assert.False(result.Ok);
        Assert.Contains("Missing required property 'slide_number'", result.Message);
    }

    [Fact]
    public async Task Call_tool_with_wrong_argument_type_returns_ok_false_with_reason()
    {
        var registry = new ToolRegistry();
        registry.Register(new TestTool(
            "strict_tool",
            "Tool with integer arg",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["slide_number"] = new JsonObject { ["type"] = "integer" }
                },
                ["required"] = new JsonArray { "slide_number" },
                ["additionalProperties"] = false
            },
            [],
            pinned: false,
            _ => Task.FromResult(ToolResult.Success("ok"))));

        var catalogue = registry.CreateCatalogue(maxInlineTools: 0);
        var callTool = catalogue.FindTool("call_tool");
        Assert.NotNull(callTool);

        var result = await callTool!.InvokeAsync(ParseJson("{\"name\":\"strict_tool\",\"arguments\":{\"slide_number\":\"three\"}}"));

        Assert.False(result.Ok);
        Assert.Contains("expected type 'integer'", result.Message);
    }

    [Fact]
    public async Task Call_tool_with_extra_property_when_additionalProperties_false_returns_ok_false_with_reason()
    {
        var registry = new ToolRegistry();
        registry.Register(new TestTool(
            "strict_tool",
            "Tool with additionalProperties false",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["slide_number"] = new JsonObject { ["type"] = "integer" }
                },
                ["required"] = new JsonArray { "slide_number" },
                ["additionalProperties"] = false
            },
            [],
            pinned: false,
            _ => Task.FromResult(ToolResult.Success("ok"))));

        var catalogue = registry.CreateCatalogue(maxInlineTools: 0);
        var callTool = catalogue.FindTool("call_tool");
        Assert.NotNull(callTool);

        var result = await callTool!.InvokeAsync(ParseJson("{\"name\":\"strict_tool\",\"arguments\":{\"slide_number\":3,\"unwanted_extra\":\"bad\"}}"));

        Assert.False(result.Ok);
        Assert.Contains("Unknown property 'unwanted_extra' is not allowed", result.Message);
    }

    [Fact]
    public async Task Call_tool_with_valid_arguments_invokes_tool_and_returns_success()
    {
        var registry = new ToolRegistry();
        registry.Register(new TestTool(
            "calculator",
            "Adds numbers",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["a"] = new JsonObject { ["type"] = "integer" },
                    ["b"] = new JsonObject { ["type"] = "integer" }
                },
                ["required"] = new JsonArray { "a", "b" },
                ["additionalProperties"] = false
            },
            ["math"],
            pinned: false,
            args =>
            {
                var a = args.GetProperty("a").GetInt64();
                var b = args.GetProperty("b").GetInt64();
                return Task.FromResult(ToolResult.Success($"Sum: {a + b}"));
            }));

        var catalogue = registry.CreateCatalogue(maxInlineTools: 0);
        var callTool = catalogue.FindTool("call_tool");
        Assert.NotNull(callTool);

        var result = await callTool!.InvokeAsync(ParseJson("{\"name\":\"calculator\",\"arguments\":{\"a\":17,\"b\":25}}"));

        Assert.True(result.Ok);
        Assert.Equal("Sum: 42", result.Message);
    }

    [Fact]
    public async Task Snapshot_isolation_tool_registered_after_snapshot_is_invisible_to_catalogue()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateTool("alpha_widget", description: "Initial tool"));

        var catalogue = registry.CreateCatalogue(maxInlineTools: 0);

        // Register a new tool after catalogue was built
        registry.Register(CreateTool("beta_gizmo", description: "Late tool"));

        // Catalogue must not see beta_gizmo
        Assert.Null(catalogue.FindTool("beta_gizmo"));

        // find_tools must not find beta_gizmo
        var findTools = catalogue.FindTool("find_tools");
        Assert.NotNull(findTools);
        var searchRes = await findTools!.InvokeAsync(ParseJson("{\"query\":\"beta_gizmo\"}"));
        Assert.Empty(searchRes.Data!.AsArray());

        // call_tool must refuse beta_gizmo
        var callTool = catalogue.FindTool("call_tool");
        Assert.NotNull(callTool);
        var callRes = await callTool!.InvokeAsync(ParseJson("{\"name\":\"beta_gizmo\",\"arguments\":{}}"));
        Assert.False(callRes.Ok);
        Assert.Contains("Unknown tool: 'beta_gizmo'", callRes.Message);
    }

    private static ITool CreateTool(
        string name,
        string description = "Test tool description",
        IReadOnlyList<string>? tags = null,
        bool pinned = false)
    {
        return new TestTool(
            name,
            description,
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
                ["additionalProperties"] = false
            },
            tags ?? [],
            pinned,
            _ => Task.FromResult(ToolResult.Success("ok")));
    }

    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
