using System.Text;
using Xunit;
using PresenterAi.Application.Presenting.VoiceCommands;

namespace PresenterAi.Application.Tests.Presenting;

public class AskDoneLexiconTests
{
    [Theory]
    [InlineData("ask done")]
    [InlineData("done asking")]
    [InlineData("that's my question")]
    [InlineData("I'm done")]
    [InlineData("over to you")]
    public void English_phrases_match_only_at_the_end_and_are_stripped(string phrase)
    {
        // At the end of a question, the phrase matches and is stripped.
        var withQuestion = AskDoneLexicon.Match($"Can you explain this architecture, {phrase}?");
        Assert.NotNull(withQuestion);
        Assert.Equal("en", withQuestion.Language);
        Assert.Equal(phrase, withQuestion.Phrase);
        Assert.Equal("Can you explain this architecture", withQuestion.Question);

        // When not at the end of an utterance, the phrase returns null.
        Assert.Null(AskDoneLexicon.Match($"{phrase}, what next"));
        Assert.Null(AskDoneLexicon.Match($"Could you {phrase} and continue"));
    }

    [Theory]
    [InlineData("hỏi xong")]
    [InlineData("tôi hỏi xong rồi")]
    [InlineData("xong rồi")]
    [InlineData("đó là câu hỏi của tôi")]
    public void Vietnamese_phrases_match_in_composed_and_decomposed_form(string phrase)
    {
        var questionPrefix = "Dự án này triển khai như thế nào, ";
        var inputComposed = (questionPrefix + phrase).Normalize(NormalizationForm.FormC);
        var inputDecomposed = (questionPrefix + phrase).Normalize(NormalizationForm.FormD);

        var matchComposed = AskDoneLexicon.Match(inputComposed);
        var matchDecomposed = AskDoneLexicon.Match(inputDecomposed);

        Assert.NotNull(matchComposed);
        Assert.NotNull(matchDecomposed);

        Assert.Equal("vi", matchComposed.Language);
        Assert.Equal("vi", matchDecomposed.Language);

        Assert.Equal(phrase, matchComposed.Phrase);
        Assert.Equal(phrase, matchDecomposed.Phrase);

        Assert.Equal("Dự án này triển khai như thế nào", matchComposed.Question);
        Assert.Equal("Dự án này triển khai như thế nào", matchDecomposed.Question);

        // Standalone phrase in both forms also matches with empty question
        var aloneComposed = AskDoneLexicon.Match(phrase.Normalize(NormalizationForm.FormC));
        var aloneDecomposed = AskDoneLexicon.Match(phrase.Normalize(NormalizationForm.FormD));

        Assert.NotNull(aloneComposed);
        Assert.NotNull(aloneDecomposed);
        Assert.Equal(string.Empty, aloneComposed.Question);
        Assert.Equal(string.Empty, aloneDecomposed.Question);
    }

    [Fact]
    public void Longest_phrase_wins_toi_hoi_xong_roi_is_stripped_whole()
    {
        // "xong rồi" is a suffix of "tôi hỏi xong rồi". The longer phrase must win
        // so that the entire phrase is stripped, rather than leaving "tôi hỏi" in the question.
        var match = AskDoneLexicon.Match("Dự án này triển khai như thế nào tôi hỏi xong rồi");
        Assert.NotNull(match);
        Assert.Equal("vi", match.Language);
        Assert.Equal("tôi hỏi xong rồi", match.Phrase);
        Assert.Equal("Dự án này triển khai như thế nào", match.Question);

        // Also when spoken alone
        var alone = AskDoneLexicon.Match("tôi hỏi xong rồi");
        Assert.NotNull(alone);
        Assert.Equal("vi", alone.Language);
        Assert.Equal("tôi hỏi xong rồi", alone.Phrase);
        Assert.Equal(string.Empty, alone.Question);
    }

    [Fact]
    public void Trailing_fillers_and_punctuation_are_ignored()
    {
        // English trailing filler "please" and trailing punctuation
        var enWithFiller = AskDoneLexicon.Match("What is the quarterly revenue? That's my question, please!");
        Assert.NotNull(enWithFiller);
        Assert.Equal("en", enWithFiller.Language);
        Assert.Equal("that's my question", enWithFiller.Phrase);
        Assert.Equal("What is the quarterly revenue", enWithFiller.Question);

        // English trailing punctuation only
        var enPunctuation = AskDoneLexicon.Match("What is the quarterly revenue - ask done...");
        Assert.NotNull(enPunctuation);
        Assert.Equal("en", enPunctuation.Language);
        Assert.Equal("ask done", enPunctuation.Phrase);
        Assert.Equal("What is the quarterly revenue", enPunctuation.Question);

        // Vietnamese trailing filler "ạ" and trailing punctuation
        var viWithFiller = AskDoneLexicon.Match("Chi phí dự án là bao nhiêu, hỏi xong ạ?");
        Assert.NotNull(viWithFiller);
        Assert.Equal("vi", viWithFiller.Language);
        Assert.Equal("hỏi xong", viWithFiller.Phrase);
        Assert.Equal("Chi phí dự án là bao nhiêu", viWithFiller.Question);

        // Trailing filler alone with phrase gives empty question
        var enAloneWithFiller = AskDoneLexicon.Match("ask done, please.");
        Assert.NotNull(enAloneWithFiller);
        Assert.Equal("en", enAloneWithFiller.Language);
        Assert.Equal(string.Empty, enAloneWithFiller.Question);

        var viAloneWithFiller = AskDoneLexicon.Match("xong rồi ạ!");
        Assert.NotNull(viAloneWithFiller);
        Assert.Equal("vi", viAloneWithFiller.Language);
        Assert.Equal(string.Empty, viAloneWithFiller.Question);
    }

    [Theory]
    [InlineData("What is the task done?")]
    [InlineData("flask done")]
    [InlineData("asking done")]
    [InlineData("ask doneness")]
    [InlineData("unasked done")]
    [InlineData("Could you hover to you?")]
    [InlineData("Dự án này thế nào, không rồi")]
    [InlineData("Tôi hỏi không rồi")]
    public void Phrase_tokens_inside_other_words_do_not_match(string utterance)
    {
        Assert.Null(AskDoneLexicon.Match(utterance));
    }

    [Theory]
    [InlineData("ask done", "en", "ask done")]
    [InlineData("done asking", "en", "done asking")]
    [InlineData("that's my question", "en", "that's my question")]
    [InlineData("That's my question!", "en", "that's my question")]
    [InlineData("I'm done", "en", "I'm done")]
    [InlineData("over to you", "en", "over to you")]
    [InlineData("hỏi xong", "vi", "hỏi xong")]
    [InlineData("tôi hỏi xong rồi", "vi", "tôi hỏi xong rồi")]
    [InlineData("xong rồi", "vi", "xong rồi")]
    [InlineData("Xong rồi ạ.", "vi", "xong rồi")]
    [InlineData("đó là câu hỏi của tôi", "vi", "đó là câu hỏi của tôi")]
    public void Phrase_alone_matches_with_an_empty_question(string utterance, string expectedLanguage, string expectedPhrase)
    {
        var match = AskDoneLexicon.Match(utterance);
        Assert.NotNull(match);
        Assert.Equal(expectedLanguage, match.Language);
        Assert.Equal(expectedPhrase, match.Phrase);
        Assert.Equal(string.Empty, match.Question);
    }

    [Fact]
    public void Every_language_has_a_phrase()
    {
        Assert.Contains(AskDoneLexicon.Languages, lang => lang.Code == "en");
        Assert.Contains(AskDoneLexicon.Languages, lang => lang.Code == "vi");

        foreach (var language in AskDoneLexicon.Languages)
        {
            Assert.False(string.IsNullOrWhiteSpace(language.Code));
            Assert.NotEmpty(language.Phrases);

            foreach (var phrase in language.Phrases)
            {
                var match = AskDoneLexicon.Match(phrase);
                Assert.NotNull(match);
                Assert.Equal(language.Code, match.Language);
                Assert.Equal(phrase, match.Phrase);
                Assert.Equal(string.Empty, match.Question);
            }
        }
    }
}
