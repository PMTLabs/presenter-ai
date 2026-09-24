using PresenterAi.Application.Scripts;
using PresenterAi.Application.Scripts.Revisions;
using Xunit;

namespace PresenterAi.Application.Tests.Scripts;

public sealed class ScriptRevisionComposerTests
{
    [Theory]
    [InlineData("ricoh-delivery-overview.md", "ricoh-delivery-overview", 2)]
    [InlineData("sample.md", "sample", 1)]
    [InlineData("vi-training-sample.md", "vi-training-sample", 2)]
    public void Replaces_only_target_narration_and_round_trips(string fixture, string id, int targetNumber)
    {
        var head = ScriptParser.Parse(ReadFixture(fixture), id);
        var newNarration = id.StartsWith("vi", StringComparison.Ordinal)
            ? "Bước đầu tiên là làm sạch.\n\nNăm 2025, chúng tôi khuyên rửa mặt bằng nước mát."
            : "The programme started in 2020.\n\nIt now covers every region.";

        var result = ScriptRevisionComposer.Compose(head, [new RevisedSlide(targetNumber, newNarration + "  \r\n")]);

        var composed = Assert.IsType<ScriptComposition.Composed>(result);
        Assert.Equal([targetNumber - 1], composed.ChangedSlideIndexes);
        Assert.Equal(head.Meta, composed.Script.Meta);
        Assert.Equal(head.Slides.Count, composed.Script.Slides.Count);
        for (var i = 0; i < head.Slides.Count; i++)
        {
            var before = head.Slides[i];
            var after = composed.Script.Slides[i];
            Assert.Equal(before.Index, after.Index);
            Assert.Equal(before.Number, after.Number);
            Assert.Equal(before.Title, after.Title);
            Assert.Equal(before.Notes, after.Notes);
            Assert.Equal(before.Number == targetNumber ? newNarration : before.Narration, after.Narration);
        }

        var reparsed = ScriptParser.Parse(composed.Markdown, id);
        Assert.Equal(composed.Script.Meta, reparsed.Meta);
        Assert.Equal(composed.Script.Slides, reparsed.Slides);
    }

    [Theory]
    [InlineData("last")]
    [InlineData("middle")]
    public void Rejects_narration_that_adds_a_slide_heading(string where)
    {
        var head = ScriptParser.Parse(ReadFixture("vi-training-sample.md"), "vi-training-sample");
        // On the last slide the extra heading parses cleanly and only the slide-count check catches it; in the middle
        // it breaks numbering and the re-parse itself fails.
        var target = where == "last" ? head.Slides.Count : 2;
        var narration = $"Nội dung mới.\n\n## Slide {head.Slides.Count + 1}: Thêm\nMột slide không được yêu cầu.";

        var result = ScriptRevisionComposer.Compose(head, [new RevisedSlide(target, narration)]);

        Assert.IsType<ScriptComposition.Failed>(result);
    }

    [Fact]
    public void Rejects_narration_that_would_become_notes()
    {
        var head = ScriptParser.Parse(ReadFixture("vi-training-sample.md"), "vi-training-sample");

        var result = ScriptRevisionComposer.Compose(head, [new RevisedSlide(3, "Dưỡng ẩm mỗi tối.\n> notes: chèn ghi chú")]);

        Assert.IsType<ScriptComposition.Failed>(result);
    }

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
