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
    public void System_instructions_contain_title_outline_and_context()
    {
        var text = PromptBuilder.SystemInstructions("My Talk", Slides, "Facts here.");

        Assert.Matches("titled \\\"My Talk\\\"", text);
        Assert.Matches("1\\. Cover\\n2\\. \\(untitled\\)\\n3\\. End", text);
        Assert.Matches("Background context[\\s\\S]*Facts here\\.", text);
        Assert.Contains("Back to the slide", text);
    }

    [Fact]
    public void System_instructions_omit_context_section_when_empty()
    {
        var text = PromptBuilder.SystemInstructions("T", Slides, string.Empty);

        Assert.DoesNotMatch("Background context", text);
    }

    [Fact]
    public void Context_is_truncated_to_the_budget_with_a_warning()
    {
        var warnings = new List<string>();
        var text = PromptBuilder.SystemInstructions(
            "T",
            Slides,
            new string('x', 1000),
            100,
            warnings.Add);

        Assert.Single(warnings);
        Assert.Matches("truncated from 1000 to 100", warnings[0]);
        Assert.Contains("[context truncated]", text);
        Assert.Contains(new string('x', 100), text);
        Assert.DoesNotContain(new string('x', 101), text);
    }

    [Fact]
    public void Single_part_slide_instruction_contains_narration_verbatim()
    {
        var text = PromptBuilder.SlideInstruction(1, 3, "Two", "Say this. And that.");

        Assert.Contains("Present slide 2 of 3 (\"Two\") now", text);
        Assert.Contains("\"\"\"\nSay this. And that.\n\"\"\"", text);
        Assert.DoesNotContain("Stop whatever", text);
    }

    [Fact]
    public void Multi_part_slide_instructions_carry_part_k_of_k_labels()
    {
        var p1 = PromptBuilder.SlideInstruction(0, 2, string.Empty, "A", 1, 3);
        var p2 = PromptBuilder.SlideInstruction(0, 2, string.Empty, "B", 2, 3);
        var p3 = PromptBuilder.SlideInstruction(0, 2, string.Empty, "C", 3, 3);

        Assert.Matches("comes in 3 parts; this is part 1", p1);
        Assert.Matches("Part 2 of 3 of slide 1 of 2\\.", p2);
        Assert.Matches("finish it first", p2);
        Assert.Matches("part 3 will follow", p2);
        Assert.Matches("Part 3 of 3 of slide 1 of 2, the last part", p3);
        Assert.Matches("then stop and wait", p3);
    }

    [Fact]
    public void Interrupt_flag_prefixes_a_stop_instruction()
    {
        var text = PromptBuilder.SlideInstruction(0, 1, string.Empty, "x", interrupt: true);

        Assert.Matches("^Stop whatever you are saying now\\. Present slide 1 of 1 now", text);
    }

    [Fact]
    public void Other_instruction_builders()
    {
        Assert.Contains(
            "Speaker notes for slide 1 of 2 (\"A\")",
            PromptBuilder.NotesContext(0, 2, "A", "n1"));
        Assert.Contains("n1", PromptBuilder.NotesContext(0, 2, "A", "n1"));
        Assert.Matches("Resume slide 3 of 5 from where you left off", PromptBuilder.ResumeInstruction(2, 5, string.Empty));
        Assert.Matches("last slide", PromptBuilder.WrapUpInstruction());
        Assert.Matches("Begin presenting slide 1 of 1 now", PromptBuilder.NudgeInstruction(0, 1, string.Empty));
        Assert.Matches("Pause now", PromptBuilder.PauseInstruction());
    }

    [Fact]
    public void System_instructions_match_node_snapshot()
    {
        var script = ScriptParser.Parse(ReadFixture("sample.md"), "sample");
        var context = ReadFixture("sample-context.md");
        var actual = PromptBuilder.SystemInstructions(script.Meta.Title, script.Slides, context);
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "system-instructions.sample.txt"));
        var firstDifference = FirstDifference(expected, actual);

        Assert.True(actual == expected, $"because first differing index: {firstDifference}");
    }

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static int FirstDifference(string expected, string actual)
    {
        var limit = Math.Min(expected.Length, actual.Length);
        for (var i = 0; i < limit; i++)
        {
            if (expected[i] != actual[i])
            {
                return i;
            }
        }

        return expected.Length == actual.Length ? -1 : limit;
    }
}
