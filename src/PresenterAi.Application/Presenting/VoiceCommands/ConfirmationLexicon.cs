namespace PresenterAi.Application.Presenting.VoiceCommands;

/// <summary>
/// Yes/No phrases per language (plan 010 §9 Q2). Every registered language is checked, only as a whole-utterance match
/// (after the matcher's normalisation), and the presenter acts on Yes/No only while it waits for a yes/no answer — so the
/// Vietnamese question particle "không" at the end of a question never confirms or declines. Adding a language is one
/// entry here plus its tests in <c>VoiceCommandMatcherTests</c>.
/// </summary>
public static class ConfirmationLexicon
{
    /// <param name="Fillers">Politeness particles dropped before matching (e.g. Vietnamese "ạ" in "vâng ạ").</param>
    public sealed record Language(string Code, IReadOnlyList<string> Yes, IReadOnlyList<string> No,
        IReadOnlyList<string> Fillers);

    public static IReadOnlyList<Language> Languages { get; } =
    [
        new("en", ["yes", "yeah", "yep", "sure", "do it"], ["no", "nope", "not yet", "dont"], []),
        new("vi", ["có", "vâng", "ừ", "đúng rồi", "được"], ["không", "thôi", "chưa"], ["ạ"])
    ];
}
