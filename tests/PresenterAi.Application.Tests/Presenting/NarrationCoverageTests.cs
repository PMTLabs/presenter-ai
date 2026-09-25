using PresenterAi.Application.Presenting;
using Xunit;

namespace PresenterAi.Application.Tests.Presenting;

public sealed class NarrationCoverageTests
{
    private const string Narration =
        "This is the team org chart. The program started in 2020. We currently have ten resources in the delivery team.";

    [Fact]
    public void Verbatim_transcript_in_fragments_is_spoken_in_full()
    {
        Assert.True(NarrationCoverage.SpokenInFull(Narration,
            " This is the team org ch" + "art. The program started in 2020. We currently have ten resources in the deli" +
            "very team.", out var coverage));
        Assert.Equal(1, coverage);
    }

    [Fact]
    public void Leading_filler_and_a_small_slip_still_count_when_the_ending_was_spoken()
    {
        Assert.True(NarrationCoverage.SpokenInFull(Narration,
            "One moment. This is the team org chart. The program started in 2020. We have ten resources in the delivery team.",
            out var coverage));
        Assert.InRange(coverage, NarrationCoverage.MinCoverage, 1);
    }

    [Fact]
    public void A_cut_off_ending_is_not_spoken_in_full_even_with_high_coverage()
    {
        // 19 of 20 words, but the last one is missing: the tail run fails.
        Assert.False(NarrationCoverage.SpokenInFull(Narration,
            "This is the team org chart. The program started in 2020. We currently have ten resources in the delivery",
            out var coverage));
        Assert.True(coverage >= NarrationCoverage.MinCoverage);
    }

    [Fact]
    public void A_quoted_last_sentence_is_not_spoken_in_full()
    {
        Assert.False(NarrationCoverage.SpokenInFull(Narration,
            "Sure: we currently have ten resources in the delivery team.", out var coverage));
        Assert.True(coverage < NarrationCoverage.MinCoverage);
    }

    [Fact]
    public void A_paraphrase_is_not_spoken_in_full()
    {
        Assert.False(NarrationCoverage.SpokenInFull(Narration,
            "Here is how the team is organised. Since 2020 the delivery team has grown to ten people.", out _));
    }

    [Fact]
    public void Vietnamese_keeps_its_diacritics()
    {
        const string vi = "Đây là sơ đồ tổ chức của nhóm. Chương trình bắt đầu từ năm 2020.";
        Assert.True(NarrationCoverage.SpokenInFull(vi, "Đây là sơ đồ tổ ch" + "ức của nhóm. Chương trình bắt đầu từ năm 2020.", out _));
        // Without diacritics the words differ ("chuc" is not "chức"): not proof.
        Assert.False(NarrationCoverage.SpokenInFull(vi, "Day la so do to chuc cua nhom. Chuong trinh bat dau tu nam 2020.", out _));
    }

    [Fact]
    public void Short_and_empty_narration()
    {
        Assert.True(NarrationCoverage.SpokenInFull("Thank you.", "Thank you!", out _));
        Assert.False(NarrationCoverage.SpokenInFull("Thank you.", "Thank", out _));
        Assert.False(NarrationCoverage.SpokenInFull("", "anything", out var coverage));
        Assert.Equal(0, coverage);
    }

    [Fact]
    public void Words_are_lower_case_unicode_letters_and_digits()
    {
        Assert.Equal(["đây", "là", "2020", "q3"], NarrationCoverage.Words("Đây LÀ — 2020, Q3!"));
    }
}
