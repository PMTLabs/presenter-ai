using System.Text;
using System.Text.Json;
using PresenterAi.Application.Tools;
using Xunit;

namespace PresenterAi.Application.Tests.Tools;

public sealed class ToolResultBudgetTests
{
    [Theory]
    [InlineData("\\")]
    [InlineData("\"")]
    [InlineData("界")]
    [InlineData("😀")]
    public void Serialised_escaped_and_multibyte_messages_stay_within_budget(string character)
    {
        var original = string.Concat(Enumerable.Repeat(character, 5000));
        var json = ToolResult.Success(original).ToJsonString();
        Assert.InRange(Encoding.UTF8.GetByteCount(json), 1, ToolResult.MaxOutputBytes);
        using var doc = JsonDocument.Parse(json);
        var message = doc.RootElement.GetProperty("message").GetString()!;
        Assert.StartsWith(string.Concat(Enumerable.Repeat(character, 100)), message);
        Assert.True(message.Length < original.Length);
        Assert.DoesNotContain('\uFFFD', message);
    }
}
