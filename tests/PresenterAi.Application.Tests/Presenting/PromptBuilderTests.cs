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
    public void Script_change_requests_are_delegated_so_they_reach_revise_script()
    {
        // T13 live run: without this rule the realtime model spoke the requested change instead of delegating it.
        foreach (var managed in new[] { false, true })
        {
            var prompt = PromptBuilder.SystemInstructions("T", Slides, string.Empty, managedMode: managed);

            Assert.Contains("asks for the script itself to change", prompt);
            Assert.Contains("delegate it in their words; do not just say the change aloud", prompt);
        }
    }

    [Fact]
    public void Edit_pending_notice_is_spoken_in_the_language_of_the_talk()
    {
        // T13 live run: the quoted English phrase was spoken verbatim in a Vietnamese talk.
        Assert.Contains("in the language of the talk", PromptBuilder.ScriptEditPendingInstruction());
    }

    [Fact]
    public void Cut_off_nudge_answers_in_the_language_of_the_talk_without_a_phrase_to_read_out()
    {
        // T8 regression fix: a question cut off at the cap; a quoted phrase would be spoken verbatim (T13 live run).
        var instruction = PromptBuilder.AskCutOffInstruction();

        Assert.Contains("in the language of the talk", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("repeat the question", instruction);
        // T8 re-run: after the Ask's PauseInstruction only an explicit "now" made the model speak.
        Assert.Contains("Answer it now", instruction);
        Assert.EndsWith("Then stop and wait.", instruction);
        Assert.DoesNotContain("\"", instruction);
        Assert.DoesNotContain("Say:", instruction);
    }

    [Fact]
    public void Answer_now_nudge_answers_in_the_language_of_the_talk_without_a_phrase_to_read_out()
    {
        // T8 regression run: a complete question left unanswered after the Ask's PauseInstruction.
        var instruction = PromptBuilder.AskAnswerNowInstruction();

        Assert.Contains("in the language of the talk", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Answer it now", instruction);
        Assert.Contains("repeat it", instruction);
        Assert.EndsWith("Then stop and wait.", instruction);
        Assert.DoesNotContain("cut off", instruction);
        Assert.DoesNotContain("\"", instruction);
        Assert.DoesNotContain("Say:", instruction);
    }

    [Fact]
    public void Question_resume_asks_for_a_natural_transition_not_a_canned_bridge()
    {
        var instruction = PromptBuilder.ResumeAfterQuestionInstruction();
        Assert.Contains("natural transition of your own", instruction);
        Assert.Contains("restart the sentence", instruction);
        Assert.DoesNotContain("Back to the slide", instruction);
    }

    [Theory]
    [InlineData(false, "then restart the sentence you were in when the question came")]
    [InlineData(true, "then restart the narration sentence you were in before the first question, not an earlier answer")]
    public void Ask_resume_names_the_slide_ends_the_pause_and_keeps_the_transition(bool followUp, string restart)
    {
        var instruction = PromptBuilder.ResumeAfterAskInstruction(1, 5, "Plan", followUp);
        Assert.StartsWith("The pause is over. Resume slide 2 of 5 (\"Plan\") now:", instruction);
        Assert.Contains("natural transition of your own", instruction);
        Assert.Contains(restart, instruction);
        Assert.Contains("if the slide was finished, say only the transition", instruction);
        Assert.EndsWith("Then stop and wait.", instruction);
        Assert.DoesNotContain("Back to the slide", instruction);
        // T8 regression fix: after several Asks the model left plain speech unanswered.
        Assert.Contains("If someone speaks to you afterwards, answer them or pass on their request as usual.", instruction);
    }

    [Fact]
    public void Ask_pause_announces_the_question_and_asks_for_the_answer()
    {
        var instruction = PromptBuilder.AskPauseInstruction();

        Assert.StartsWith("Pause now", instruction);
        Assert.Contains("a listener is asking a question", instruction);
        Assert.Contains("then answer it", instruction);
        Assert.Contains("Do not continue the narration until told.", instruction);
        Assert.DoesNotContain("you may answer", instruction);
        Assert.DoesNotContain("\"", instruction);
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
