using PresenterAi.Application.Scripts;

namespace PresenterAi.Application.Presenting;

public static class PromptBuilder
{
    public const int ContextCharBudget = 48_000;

    public static string SystemInstructions(
        string title,
        IReadOnlyList<SlideRef> slides,
        string? context = null,
        int maxContextChars = ContextCharBudget,
        Action<string>? onWarn = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(slides);

        var outline = string.Join('\n', slides.Select(slide =>
            $"{slide.Index + 1}. {(slide.Title.Length > 0 ? slide.Title : "(untitled)")}"));

        var ctx = (context ?? string.Empty).Trim();
        if (ctx.Length > maxContextChars)
        {
            onWarn?.Invoke($"context truncated from {ctx.Length} to {maxContextChars} chars");
            ctx = JavaScriptSlice(ctx, maxContextChars) + "\n[context truncated]";
        }

        var parts = new List<string>
        {
            $"You are the presenter delivering a talk titled \"{title}\" to a live audience. A presentation controller shows the slides on screen and, for each slide, sends you an instruction that contains that slide's narration.",
            string.Empty,
            "Delivery rules:",
            "- When you receive a slide's narration, speak it in full and in order, as written, with only minimal natural rephrasing. Do not add content that is not in the narration or the background context. Never read labels, headings, or stage directions such as \"Slide 3\" or \"part 1 of 2\" aloud.",
            "- Keep a steady, natural presenter pace. Do not pause for more than a second or two in the middle of a slide.",
            "- When you finish a slide's narration, stop speaking and wait silently. Do not announce the next slide and do not ask whether to continue; the controller sends the next slide.",
            "- Long narrations arrive in numbered parts. Continue from one part to the next immediately, without a break.",
            string.Empty,
            "Audience interaction (you can hear the audience):",
            "- If someone speaks to you, stop and listen. When the narration or background context covers the answer, answer immediately in one to three sentences; otherwise delegate the question.",
            "- Never say that you checked, looked up, or found something before a result arrives. While waiting, at most say \"One moment.\" Do not resume the narration until the question is answered.",
            "- After answering, stop and stay silent in case there is a follow-up question. You will be told when to continue.",
            "- Never start the next slide on your own.",
            string.Empty,
            "Speak in the same language as the narration.",
            string.Empty,
            "Slide outline:",
            outline,
        };

        if (ctx.Length > 0)
        {
            parts.Add(string.Empty);
            parts.Add("--- Background context (for answering questions; do not recite it) ---");
            parts.Add(ctx);
        }

        return string.Join('\n', parts);
    }

    public static string SystemInstructions(
        string title,
        IReadOnlyList<Slide> slides,
        string? context = null,
        int maxContextChars = ContextCharBudget,
        Action<string>? onWarn = null) =>
        SystemInstructions(
            title,
            slides.Select(slide => new SlideRef(slide.Index, slide.Title)).ToArray(),
            context,
            maxContextChars,
            onWarn);

    public static string SlideInstruction(
        int index,
        int total,
        string title,
        string chunk,
        int part = 1,
        int parts = 1,
        bool interrupt = false)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(chunk);

        var head = new List<string>();
        if (interrupt)
        {
            head.Add("Stop whatever you are saying now.");
        }

        if (parts == 1)
        {
            head.Add($"Present {SlideLabel(index, total, title)} now. Say the following narration in full, in order, with only minimal natural rephrasing, then stop and wait:");
        }
        else if (part == 1)
        {
            head.Add($"Present {SlideLabel(index, total, title)} now. Its narration comes in {parts} parts; this is part 1. Say it in full and in order, then pause briefly; part 2 will follow:");
        }
        else if (part < parts)
        {
            head.Add($"Part {part} of {parts} of {SlideLabel(index, total, title)}. If you have not finished the previous part, finish it first. Then continue with this text, in full and in order, and pause briefly; part {part + 1} will follow:");
        }
        else
        {
            head.Add($"Part {part} of {parts} of {SlideLabel(index, total, title)}, the last part. If you have not finished the previous part, finish it first. Then say this text in full and in order, then stop and wait:");
        }

        return $"{string.Join(' ', head)}\n\n\"\"\"\n{chunk}\n\"\"\"";
    }

    public static string NotesContext(int index, int total, string title, string notes) =>
        $"Speaker notes for {SlideLabel(index, total, title)} (background for questions, do not read aloud):\n{notes}";

    public static string ResumeInstruction(int index, int total, string title) =>
        $"Resume {SlideLabel(index, total, title)} from where you left off, then stop and wait.";

    public static string PauseInstruction() =>
        "Pause now. Stay silent and do not speak until you are told to resume.";

    public static string ClientDelegationAnswerNowInstruction() =>
        "No lookup is available. Answer now in one to three sentences from the narration and background context, or say plainly that the material does not cover it; then stop and stay silent until you are told to continue.";

    public static string ResumeAfterQuestionInstruction() =>
        "No more questions. Say a short bridge such as \"Back to the slide\", then continue this slide's narration by restarting the sentence you were in when you were interrupted, so the audience can follow. If you had already finished this slide's narration, say only the bridge.";

    public static string NudgeInstruction(int index, int total, string title) =>
        $"Begin presenting {SlideLabel(index, total, title)} now, using the narration you were given.";

    public static string WrapUpInstruction() =>
        "That was the last slide. Thank the audience in one or two sentences, then stop speaking.";

    private static string SlideLabel(int index, int total, string title) =>
        $"slide {index + 1} of {total}{(title.Length > 0 ? $" (\"{title}\")" : string.Empty)}";

    private static string JavaScriptSlice(string value, int end)
    {
        var resolvedEnd = end < 0 ? value.Length + end : end;
        resolvedEnd = Math.Clamp(resolvedEnd, 0, value.Length);
        return value[..resolvedEnd];
    }
}
