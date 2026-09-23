using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Presenting.Tools;
using PresenterAi.Application.Tools;
using Xunit;

namespace PresenterAi.Application.Tests.Tools;

public sealed class ToolSessionCatalogueTests
{
    [Fact]
    public async Task Default_presenter_registry_plus_session_tools_fits_discovery_inline_budget()
    {
        await using var presenter = new Presenter(
            (_, _) => null,
            (_, _, _) => throw new InvalidOperationException("Not used by this test."));
        var sessionTool = CreateTool("session_tool");

        var catalogue = ToolSessionCatalogue.Build(presenter.ToolRegistry, [sessionTool], maxInlineTools: 0);

        Assert.Contains(catalogue.InlineTools, tool => tool.Name == "find_tools");
        Assert.Contains(catalogue.InlineTools, tool => tool.Name == "call_tool");
        var payload = new JsonArray(catalogue.GetInlineToolDefinitions()
            .Select(definition => (JsonNode)definition.DeepClone()).ToArray()).ToJsonString();
        Assert.True(Encoding.UTF8.GetByteCount(payload) <= ToolSessionCatalogue.MaxInlineToolsPayloadBytes);
    }

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

        var result = catalogue.Resolve("call_tool", ParseJson("{\"name\":\"non_existent\",\"arguments\":{}}" )).Error!;

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

        var result = catalogue.Resolve("call_tool", ParseJson("{\"name\":\"strict_tool\",\"arguments\":{}}" )).Error!;

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

        var result = catalogue.Resolve("call_tool", ParseJson("{\"name\":\"strict_tool\",\"arguments\":{\"slide_number\":\"three\"}}" )).Error!;

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

        var result = catalogue.Resolve("call_tool", ParseJson("{\"name\":\"strict_tool\",\"arguments\":{\"slide_number\":3,\"unwanted_extra\":\"bad\"}}" )).Error!;

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

        var result = await catalogue.InvokeAsync("call_tool", ParseJson("{\"name\":\"calculator\",\"arguments\":{\"a\":17,\"b\":25}}"));

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
        var callRes = catalogue.Resolve("call_tool", ParseJson("{\"name\":\"beta_gizmo\",\"arguments\":{}}" )).Error!;
        Assert.False(callRes.Ok);
        Assert.Contains("Unknown tool: 'beta_gizmo'", callRes.Message);
    }

    [Fact]
    public void Build_adds_session_tools_and_keeps_them_searchable_beyond_inline_threshold()
    {
        var registry = new ToolRegistry();
        for (var i = 1; i <= 6; i++)
        {
            registry.Register(CreateTool($"presenter_{i}", pinned: true));
        }

        var ten = Enumerable.Range(1, 10).Select(i => CreateTool($"session_{i}")).ToArray();
        var inline = ToolSessionCatalogue.Build(registry, ten);
        Assert.Equal(16, inline.InlineTools.Count);
        Assert.Equal(16, inline.AllTools.Count);

        var eleven = Enumerable.Range(1, 11).Select(i => CreateTool($"session_{i}", pinned: i == 1)).ToArray();
        var discovered = ToolSessionCatalogue.Build(registry, eleven);
        Assert.Equal(8, discovered.InlineTools.Count);
        Assert.Equal(17, discovered.AllTools.Count);
        Assert.Contains(discovered.AllTools, tool => tool.Name == "session_11");
        Assert.Empty(discovered.Notes);
    }

    [Fact]
    public void Resolve_targets_the_effective_tool_for_direct_and_call_tool_calls()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateTool("target_tool"));
        var catalogue = ToolSessionCatalogue.Build(registry, maxInlineTools: 0);
        var direct = catalogue.Resolve("target_tool", ParseJson("{}"));
        var wrapped = catalogue.Resolve("call_tool", ParseJson("{\"name\":\"target_tool\",\"arguments\":{}}"));

        Assert.True(direct.IsResolved);
        Assert.Equal("target_tool", direct.Tool!.Name);
        Assert.True(wrapped.IsResolved);
        Assert.Equal("target_tool", wrapped.Tool!.Name);
        Assert.Equal(JsonValueKind.Object, wrapped.Arguments.ValueKind);
    }

    [Fact]
    public void Session_tools_exceeding_budget_are_not_inline_but_are_searchable_with_note()
    {
        var registry = new ToolRegistry();
        var inlinePresenter = CreateTool("presenter", pinned: true);
        registry.Register(inlinePresenter);
        var largeTools = Enumerable.Range(1, 8).Select(i => new TestTool(
            $"session_large_{i}",
            new string('d', 1024),
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { [new string('x', 3900)] = new JsonObject { ["type"] = "string" } } },
            [],
            false,
            _ => Task.FromResult(ToolResult.Success("ok")))).ToArray();

        var catalogue = ToolSessionCatalogue.Build(registry, largeTools);

        Assert.DoesNotContain(catalogue.InlineTools, tool => tool.Name == "session_large_8");
        Assert.Contains(catalogue.InlineTools, tool => tool.Name == "find_tools");
        Assert.Contains(catalogue.InlineTools, tool => tool.Name == "call_tool");
        Assert.Contains(catalogue.AllTools, tool => tool.Name == "session_large_8");
        Assert.True(catalogue.Resolve("session_large_8", ParseJson("{}")).IsResolved);
        Assert.Contains(catalogue.Notes, note => note.Contains("discovery", StringComparison.Ordinal));
    }

    [Fact]
    public void Tool_result_serializes_outcome_and_defaults_success_and_failure()
    {
        var success = ToolResult.Success("done");
        var timeout = ToolResult.Failure("timed out") with { Outcome = "timeout" };

        Assert.Equal("ok", success.Outcome);
        Assert.Equal("error", ToolResult.Failure("failed").Outcome);
        Assert.Equal("timeout", JsonDocument.Parse(timeout.ToJsonString()).RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Mutating_source_and_export_cannot_change_snapshot_or_next_export()
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "integer" } },
            ["required"] = new JsonArray { "value" }, ["additionalProperties"] = false };
        var source = new TestTool("strict", "Original", schema, [], true,
            _ => Task.FromResult(ToolResult.Success("ok")));
        var catalogue = new ToolSessionCatalogue([source]);
        schema["required"] = new JsonArray();
        catalogue.GetInlineToolDefinitions()[0]["parameters"] = new JsonObject { ["type"] = "object" };
        catalogue.FindTool("strict")!.Parameters["required"] = new JsonArray();
        Assert.Equal("Original", catalogue.GetInlineToolDefinitions()[0]["description"]!.GetValue<string>());
        Assert.Equal("value", catalogue.GetInlineToolDefinitions()[0]["parameters"]!["required"]![0]!.GetValue<string>());
        Assert.False((await catalogue.InvokeAsync("strict", ParseJson("{}"))).Ok);
    }

    [Fact]
    public void Snapshot_keeps_confirmation_timeout_and_source_of_session_tools()
    {
        var registry = new ToolRegistry();
        var external = new GatedTool();
        var catalogue = ToolSessionCatalogue.Build(registry, [external], maxInlineTools: 0);

        foreach (var tool in new[]
                 {
                     catalogue.FindTool("gated")!,
                     catalogue.AllTools.Single(t => t.Name == "gated"),
                     catalogue.Resolve("call_tool", ParseJson("{\"name\":\"gated\",\"arguments\":{}}")).Tool!,
                 })
        {
            Assert.True(tool.RequiresConfirmation);
            Assert.Equal(TimeSpan.FromSeconds(17), tool.Timeout);
            Assert.Equal("crm", tool.Source);
            Assert.Equal("Look up a customer", tool.Title);
        }

        var inline = ToolSessionCatalogue.Build(registry, [external]);
        var inlineTool = inline.InlineTools.Single(t => t.Name == "gated");
        Assert.True(inlineTool.RequiresConfirmation);
        Assert.Equal("crm", inline.Resolve("gated", ParseJson("{}")).Tool!.Source);
    }

    private sealed class GatedTool : ITool
    {
        public string Name => "gated";
        public string Description => "Needs a yes";
        public JsonObject Parameters => new() { ["type"] = "object", ["properties"] = new JsonObject() };
        public IReadOnlyList<string> Tags => [];
        public bool Pinned => false;
        public bool RequiresConfirmation => true;
        public TimeSpan Timeout => TimeSpan.FromSeconds(17);
        public string Source => "crm";
        public string Title => "Look up a customer";
        public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
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
