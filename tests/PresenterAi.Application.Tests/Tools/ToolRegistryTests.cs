using System.Text.Json;
using System.Text.Json.Nodes;
using PresenterAi.Application.Tools;
using Xunit;

namespace PresenterAi.Application.Tests.Tools;

public sealed class ToolRegistryTests
{
    [Theory]
    [InlineData("valid_tool")]
    [InlineData("ValidTool-123")]
    [InlineData("tool_with_underscores_and-dashes")]
    [InlineData("a")]
    public void Register_valid_tool_succeeds(string name)
    {
        var registry = new ToolRegistry();
        var tool = CreateTestTool(name);

        registry.Register(tool);

        Assert.Equal(1, registry.Count);
        Assert.Single(registry.GetAllTools(), t => t.Name == name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid name with spaces")]
    [InlineData("tool$symbol")]
    [InlineData("tool.with.dots")]
    [InlineData("tool@name")]
    public void Register_invalid_tool_name_throws_ArgumentException(string name)
    {
        var registry = new ToolRegistry();
        var tool = CreateTestTool(name);

        var ex = Assert.Throws<ArgumentException>(() => registry.Register(tool));
        Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_tool_name_exceeding_64_characters_throws_ArgumentException()
    {
        var registry = new ToolRegistry();
        var name = new string('a', 65);
        var tool = CreateTestTool(name);

        var ex = Assert.Throws<ArgumentException>(() => registry.Register(tool));
        Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_duplicate_tool_name_throws_InvalidOperationException()
    {
        var registry = new ToolRegistry();
        var tool1 = CreateTestTool("duplicate_tool");
        var tool2 = CreateTestTool("duplicate_tool");

        registry.Register(tool1);
        var ex = Assert.Throws<InvalidOperationException>(() => registry.Register(tool2));
        Assert.Contains("already registered", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_duplicate_tool_name_case_insensitive_throws_InvalidOperationException()
    {
        var registry = new ToolRegistry();
        var tool1 = CreateTestTool("my_tool");
        var tool2 = CreateTestTool("MY_TOOL");

        registry.Register(tool1);
        var ex = Assert.Throws<InvalidOperationException>(() => registry.Register(tool2));
        Assert.Contains("already registered", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_tool_description_exceeding_1024_characters_throws_ArgumentException()
    {
        var registry = new ToolRegistry();
        var longDesc = new string('x', 1025);
        var tool = CreateTestTool("tool_long_desc", description: longDesc);

        var ex = Assert.Throws<ArgumentException>(() => registry.Register(tool));
        Assert.Contains("description exceeds maximum length", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_tool_parameters_exceeding_4KiB_throws_ArgumentException()
    {
        var registry = new ToolRegistry();
        var bigProps = new JsonObject();
        for (var i = 0; i < 200; i++)
        {
            bigProps[$"prop_{i:D4}"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = $"A somewhat detailed description of property number {i} to consume bytes."
            };
        }

        var oversizedSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = bigProps
        };

        var tool = CreateTestTool("oversized_tool", parameters: oversizedSchema);

        var ex = Assert.Throws<ArgumentException>(() => registry.Register(tool));
        Assert.Contains("exceeds 4096 bytes", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_tool_parameters_with_non_object_root_throws_ArgumentException()
    {
        var registry = new ToolRegistry();
        var arraySchema = new JsonObject
        {
            ["type"] = "array"
        };
        var tool = CreateTestTool("non_object_tool", parameters: arraySchema);

        var ex = Assert.Throws<ArgumentException>(() => registry.Register(tool));
        Assert.Contains("must be 'object'", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_up_to_12_pinned_tools_succeeds()
    {
        var registry = new ToolRegistry();
        for (var i = 1; i <= 12; i++)
        {
            registry.Register(CreateTestTool($"pinned_tool_{i}", pinned: true));
        }

        Assert.Equal(12, registry.PinnedCount);
        Assert.Equal(12, registry.Count);
    }

    [Fact]
    public void Register_more_than_12_pinned_tools_throws_InvalidOperationException()
    {
        var registry = new ToolRegistry();
        for (var i = 1; i <= 12; i++)
        {
            registry.Register(CreateTestTool($"pinned_tool_{i}", pinned: true));
        }

        var thirteenth = CreateTestTool("pinned_tool_13", pinned: true);
        var ex = Assert.Throws<InvalidOperationException>(() => registry.Register(thirteenth));
        Assert.Contains("Cannot register more than 12 pinned tools", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_unpinned_tools_beyond_12_succeeds()
    {
        var registry = new ToolRegistry();
        for (var i = 1; i <= 12; i++)
        {
            registry.Register(CreateTestTool($"pinned_tool_{i}", pinned: true));
        }

        for (var i = 1; i <= 20; i++)
        {
            registry.Register(CreateTestTool($"unpinned_tool_{i}", pinned: false));
        }

        Assert.Equal(12, registry.PinnedCount);
        Assert.Equal(32, registry.Count);
    }

    [Fact]
    public async Task Concurrent_register_and_catalogue_creation_produce_consistent_snapshots()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateTestTool("initial"));
        using var start = new ManualResetEventSlim(false);
        var writer = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < 300; i++) registry.Register(CreateTestTool($"tool_{i}"));
        });
        var reader = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < 300; i++)
            {
                var catalogue = registry.CreateCatalogue();
                Assert.NotNull(catalogue.FindTool("initial"));
            }
        });
        start.Set();
        await Task.WhenAll(writer, reader).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(301, registry.Count);
    }

    internal static TestTool CreateTestTool(
        string name,
        string description = "A test tool",
        JsonObject? parameters = null,
        IReadOnlyList<string>? tags = null,
        bool pinned = false,
        Func<JsonElement, Task<ToolResult>>? handler = null)
    {
        return new TestTool(
            name,
            description,
            parameters ?? new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
                ["additionalProperties"] = false
            },
            tags ?? [],
            pinned,
            handler ?? (_ => Task.FromResult(ToolResult.Success("ok"))));
    }
}

internal sealed class TestTool : ITool
{
    private readonly Func<JsonElement, Task<ToolResult>> _handler;

    public TestTool(
        string name,
        string description,
        JsonObject parameters,
        IReadOnlyList<string> tags,
        bool pinned,
        Func<JsonElement, Task<ToolResult>> handler)
    {
        Name = name;
        Description = description;
        Parameters = parameters;
        Tags = tags;
        Pinned = pinned;
        _handler = handler;
    }

    public string Name { get; }
    public string Description { get; }
    public JsonObject Parameters { get; }
    public IReadOnlyList<string> Tags { get; }
    public bool Pinned { get; }

    public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        _handler(arguments);
}
