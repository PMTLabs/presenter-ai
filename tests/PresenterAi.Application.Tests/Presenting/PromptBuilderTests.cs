using PresenterAi.Application.Presenting;
using PresenterAi.Application.Scripts;
using Xunit;

namespace PresenterAi.Application.Tests.Presenting;

public sealed class PromptBuilderTests
{
    private static readonly IReadOnlyList<SlideRef> Slides =
    [
        new(0, "Cover"),
        new(1, string.Empty),
        new(2, "End"),
    ];

    [Fact]
    public void System_instructions_without_context_match_node_golden()
    {
        var actual = PromptBuilder.SystemInstructions("T", Slides, string.Empty);

        Assert.Equal(ReadGolden("system-instructions.no-context.txt"), actual);
    }

    [Fact]
    public void System_instructions_with_empty_title_and_untitled_slides_match_node_golden()
    {
        var actual = PromptBuilder.SystemInstructions(
            string.Empty,
            [new SlideRef(0, string.Empty), new SlideRef(1, "Named")],
            string.Empty);

        Assert.Equal(ReadGolden("system-instructions.untitled.txt"), actual);
    }

    [Fact]
    public void Context_truncation_and_warning_match_node_golden()
    {
        var warnings = new List<string>();
        var prompt = PromptBuilder.SystemInstructions(
            "T",
            Slides,
            new string('x', 1000),
            100,
            warnings.Add);

        Assert.Single(warnings);
        Assert.Equal(
            ReadGolden("system-instructions.truncated.txt"),
            $"{warnings[0]}\n---\n{prompt}");
    }

    [Fact]
    public void System_instructions_match_node_snapshot()
    {
        var script = ScriptParser.Parse(ReadFixture("sample.md"), "sample");
        var context = ReadFixture("sample-context.md");
        var actual = PromptBuilder.SystemInstructions(script.Meta.Title, script.Slides, context);

        Assert.Equal(ReadGolden("system-instructions.sample.txt"), actual);
    }

    [Fact]
    public void Question_resume_asks_for_a_natural_transition_not_a_canned_bridge()
    {
        var instruction = PromptBuilder.ResumeAfterQuestionInstruction();
        Assert.Contains("natural transition of your own", instruction);
        Assert.Contains("restart the sentence", instruction);
        Assert.DoesNotContain("Back to the slide", instruction);
    }

    [Fact]
    public void Slide_instruction_variants_match_node_goldens()
    {
        Assert.Equal(
            ReadGolden("slide-instruction.single.txt"),
            PromptBuilder.SlideInstruction(1, 3, "Two", "Say this. And that."));
        Assert.Equal(
            ReadGolden("slide-instruction.part-1.txt"),
            PromptBuilder.SlideInstruction(0, 2, string.Empty, "A", 1, 3));
        Assert.Equal(
            ReadGolden("slide-instruction.part-2.txt"),
            PromptBuilder.SlideInstruction(0, 2, string.Empty, "B", 2, 3));
        Assert.Equal(
            ReadGolden("slide-instruction.part-3.txt"),
            PromptBuilder.SlideInstruction(0, 2, string.Empty, "C", 3, 3));
        Assert.Equal(
            ReadGolden("slide-instruction.interrupt.txt"),
            PromptBuilder.SlideInstruction(0, 1, string.Empty, "x", interrupt: true));
    }

    [Fact]
    public void Other_instruction_builders_match_node_goldens()
    {
        Assert.Equal(
            ReadGolden("notes-context.txt"),
            PromptBuilder.NotesContext(0, 2, "A", "n1"));
        Assert.Equal(
            ReadGolden("resume.txt"),
            PromptBuilder.ResumeInstruction(2, 5, string.Empty));
        Assert.Equal(ReadGolden("wrap-up.txt"), PromptBuilder.WrapUpInstruction());
        Assert.Equal(
            ReadGolden("nudge.txt"),
            PromptBuilder.NudgeInstruction(0, 1, string.Empty));
        Assert.Equal(ReadGolden("pause.txt"), PromptBuilder.PauseInstruction());
    }

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string ReadGolden(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", name));
}
