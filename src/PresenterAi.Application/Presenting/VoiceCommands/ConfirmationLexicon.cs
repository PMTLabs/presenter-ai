namespace PresenterAi.Application.Presenting.VoiceCommands;

/// <summary>
/// Yes/No phrases per language (plan 010 §9 Q2). Every registered language is checked, only as a whole-utterance match
/// (after the matcher's normalisation), and the presenter acts on Yes/No only while it waits for a yes/no answer — so the
/// Vietnamese question particle "không" at the end of a question never confirms or declines. Adding a language is one
/// entry here plus its tests in <c>VoiceCommandMatcherTests</c>.
/// Owner decision "allow polite words" (2026-09-24): a Yes/No phrase followed only by polite words ("no thank you",
/// "yes go ahead", "không cảm ơn") is still that answer; anything else after it ("no but what about…") is not a reply.
/// </summary>
public static class ConfirmationLexicon
{
    /// <param name="Fillers">Politeness particles dropped before matching (e.g. Vietnamese "ạ" in "vâng ạ").</param>
    /// <param name="Polite">Polite words allowed after a Yes or No phrase, and only there (with <paramref name="Fillers"/>).</param>
    /// <param name="YesPolite">Polite words allowed after a Yes phrase only ("yes go ahead", never "no go ahead").</param>
    public sealed record Language(string Code, IReadOnlyList<string> Yes, IReadOnlyList<string> No,
        IReadOnlyList<string> Fillers, IReadOnlyList<string> Polite, IReadOnlyList<string> YesPolite);

    public static IReadOnlyList<Language> Languages { get; } =
    [
        new("en", ["yes", "yeah", "yep", "sure", "do it"], ["no", "nope", "not yet", "dont"], [],
            ["thank you", "thanks", "please"], ["go ahead"]),
        new("vi", ["có", "vâng", "ừ", "đúng rồi", "được"], ["không", "thôi", "chưa"], ["ạ"],
            ["cảm ơn", "cám ơn"], [])
    ];
}
