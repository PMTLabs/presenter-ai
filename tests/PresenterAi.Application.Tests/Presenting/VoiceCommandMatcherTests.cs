using Xunit;
using PresenterAi.Application.Presenting.VoiceCommands;

namespace PresenterAi.Application.Tests.Presenting;

public class VoiceCommandMatcherTests
{
    public static TheoryData<string, VoiceCommandIntent> Phrases => new()
    {
        { "stop", VoiceCommandIntent.Pause }, { "pause", VoiceCommandIntent.Pause },
        { "wait", VoiceCommandIntent.Pause }, { "hold on", VoiceCommandIntent.Pause },
        { "stop talking", VoiceCommandIntent.Pause }, { "stop there", VoiceCommandIntent.Pause },
        { "pause there", VoiceCommandIntent.Pause },
        { "continue", VoiceCommandIntent.Resume }, { "carry on", VoiceCommandIntent.Resume },
        { "keep going", VoiceCommandIntent.Resume }, { "go on", VoiceCommandIntent.Resume },
        { "go ahead", VoiceCommandIntent.Resume }, { "resume", VoiceCommandIntent.Resume },
        { "keep continue", VoiceCommandIntent.Resume },
        { "next", VoiceCommandIntent.Next }, { "next slide", VoiceCommandIntent.Next },
        { "go next", VoiceCommandIntent.Next }, { "move on", VoiceCommandIntent.Next },
        { "back", VoiceCommandIntent.Previous }, { "go back", VoiceCommandIntent.Previous },
        { "previous", VoiceCommandIntent.Previous }, { "previous slide", VoiceCommandIntent.Previous },
        { "last slide", VoiceCommandIntent.Previous },
        { "end", VoiceCommandIntent.End }, { "end meeting", VoiceCommandIntent.End },
        { "end presentation", VoiceCommandIntent.End }, { "end talk", VoiceCommandIntent.End },
        { "finish", VoiceCommandIntent.End }, { "stop presentation", VoiceCommandIntent.End },
        { "yes", VoiceCommandIntent.Yes }, { "yeah", VoiceCommandIntent.Yes },
        { "yep", VoiceCommandIntent.Yes }, { "sure", VoiceCommandIntent.Yes },
        { "do it", VoiceCommandIntent.Yes },
        { "no", VoiceCommandIntent.No }, { "nope", VoiceCommandIntent.No },
        { "not yet", VoiceCommandIntent.No }, { "don't", VoiceCommandIntent.No }
    };

    [Theory]
    [MemberData(nameof(Phrases))]
    public void Every_phrase_matches_with_fragmentation(string phrase, VoiceCommandIntent intent)
    {
        // The assembler concatenates deltas verbatim, never inserts a separator.
        foreach (var fragments in new[]
        {
            new[] { phrase },
            new[] { phrase[..1], phrase[1..] },
            phrase.Select(ch => ch.ToString()).ToArray(),
            new[] { phrase.Replace(" ", "", StringComparison.Ordinal) }
        })
            Assert.Equal(intent, VoiceCommandMatcher.Match(string.Concat(fragments))?.Intent);
    }

    [Theory]
    [InlineData("next", " slide")]
    [InlineData("next ", "slide")]
    [InlineData("next", "slide")]
    public void Next_slide_split_at_word_boundary(string first, string second) =>
        Assert.Equal(VoiceCommandIntent.Next, VoiceCommandMatcher.Match(first + second)?.Intent);

    [Theory]
    [InlineData("Please, can you just stop now?", VoiceCommandIntent.Pause)]
    [InlineData("Could you, okay, go to the slide 3 please?", VoiceCommandIntent.GoTo)]
    [InlineData("Hey! OK, a next slide now.", VoiceCommandIntent.Next)]
    [InlineData("Just end the meeting now", VoiceCommandIntent.End)]
    [InlineData("Can you stop now", VoiceCommandIntent.Pause)]
    public void Only_allowlisted_fillers_are_removed(string phrase, VoiceCommandIntent intent) =>
        Assert.Equal(intent, VoiceCommandMatcher.Match(phrase)?.Intent);

    [Theory]
    [InlineData("slide 1", 1)]
    [InlineData("go to slide 40", 40)]
    [InlineData("go to the slide twenty", 20)]
    [InlineData("slideone", 1)]
    public void Numbered_navigation(string phrase, int number) =>
        Assert.Equal(new VoiceCommand(VoiceCommandIntent.GoTo, number), VoiceCommandMatcher.Match(phrase));

    [Fact]
    public void All_number_words_and_fragmentations_match()
    {
        var words = new[] { "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
            "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty" };
        for (var i = 0; i < words.Length; i++)
            foreach (var prefix in new[] { "slide ", "go to slide " })
            {
                var phrase = prefix + words[i];
                foreach (var assembled in new[] { phrase, string.Concat(phrase.Select(ch => ch.ToString())), phrase.Replace(" ", "", StringComparison.Ordinal) })
                    Assert.Equal(new VoiceCommand(VoiceCommandIntent.GoTo, i + 1), VoiceCommandMatcher.Match(assembled));
            }
    }

    [Theory]
    [InlineData("what happens when you stop?")]
    [InlineData("can you go back to what you said about keys")]
    [InlineData("please explain the next slide")]
    [InlineData("will you stop talking about this?")]
    [InlineData("go to slide 3 and explain it")]
    [InlineData("someone said yes")]
    [InlineData("could you perhaps pause now")]
    [InlineData("slide 0")]
    public void Extra_words_are_not_commands(string question) => Assert.Null(VoiceCommandMatcher.Match(question));

    [Theory]
    [InlineData("có", VoiceCommandIntent.Yes)]
    [InlineData("Có.", VoiceCommandIntent.Yes)]
    [InlineData("vâng ạ", VoiceCommandIntent.Yes)]
    [InlineData("ừ", VoiceCommandIntent.Yes)]
    [InlineData("Đúng rồi!", VoiceCommandIntent.Yes)]
    [InlineData("được", VoiceCommandIntent.Yes)]
    [InlineData("không", VoiceCommandIntent.No)]
    [InlineData("Không ạ.", VoiceCommandIntent.No)]
    [InlineData("thôi", VoiceCommandIntent.No)]
    [InlineData("chưa", VoiceCommandIntent.No)]
    public void Vietnamese_yes_no_match_whole_utterance_only(string phrase, VoiceCommandIntent intent)
    {
        Assert.Equal(intent, VoiceCommandMatcher.Match(phrase)?.Intent);
        // Decomposed diacritics (as some recognisers emit them) match the same way.
        Assert.Equal(intent, VoiceCommandMatcher.Match(phrase.Normalize(System.Text.NormalizationForm.FormD))?.Intent);
    }

    [Theory]
    [InlineData("Bạn có biết không?")]
    [InlineData("Slide này có số liệu không?")]
    [InlineData("không biết")]
    [InlineData("có lẽ")]
    public void Vietnamese_question_particles_inside_a_sentence_are_not_answers(string question) =>
        Assert.Null(VoiceCommandMatcher.Match(question));

    [Fact]
    public void Every_lexicon_language_has_yes_and_no()
    {
        Assert.Contains(ConfirmationLexicon.Languages, language => language.Code == "en");
        Assert.Contains(ConfirmationLexicon.Languages, language => language.Code == "vi");
        foreach (var language in ConfirmationLexicon.Languages)
        {
            Assert.NotEmpty(language.Yes);
            Assert.NotEmpty(language.No);
            foreach (var phrase in language.Yes) Assert.Equal(VoiceCommandIntent.Yes, VoiceCommandMatcher.Match(phrase)?.Intent);
            foreach (var phrase in language.No) Assert.Equal(VoiceCommandIntent.No, VoiceCommandMatcher.Match(phrase)?.Intent);
        }
    }
}
