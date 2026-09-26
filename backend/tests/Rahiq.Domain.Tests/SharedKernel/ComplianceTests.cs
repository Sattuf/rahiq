using Rahiq.SharedKernel;
using Rahiq.SharedKernel.Compliance;
using Rahiq.SharedKernel.Text;

namespace Rahiq.Domain.Tests.SharedKernel;

public class ComplianceTests
{
    private static readonly ClaimsGuard Guard = Taxonomy.CreateClaimsGuard();

    [Theory]
    [InlineData("İSTANBUL", "istanbul")]
    [InlineData("ISPARTA", "isparta")]
    [InlineData("Kestane balı, Şifalı değil", "kestane bali sifali degil")]
    [InlineData("ÇÖÜĞ", "coug")]
    [InlineData("العَسَلُ", "العسل")]
    [InlineData("أإآ", "ااا")]
    [InlineData("مكتبة", "مكتبه")]
    [InlineData("على", "علي")]
    [InlineData("عـــسل", "عسل")]
    [InlineData("  hello,   world!  ", "hello world")]
    public void Folds_turkish_and_arabic_for_matching(string input, string expected) =>
        Assert.Equal(expected, TextFolding.Fold(input));

    [Fact]
    public void Empty_text_folds_to_empty() => Assert.Equal(string.Empty, TextFolding.Fold(null));

    [Theory]
    [InlineData("خلطة تعالج السعال")]
    [InlineData("معالجة طبيعية")]
    [InlineData("عسل يعالج")]
    public void Arabic_verb_terms_match_every_conjugation(string text) =>
        Assert.Contains(Guard.Check("story.ar", text), f => f.Term == "يعالج");

    [Theory]
    [InlineData("story.ar", "هذا العسل يعالج السعال", "يعالج")]
    [InlineData("story.ar", "خلطة طبية تقليدية", "طبية")]
    [InlineData("story.ar", "يقوي المناعة في الشتاء", "المناعة")]
    [InlineData("story.ar", "العلاج الطبيعي", "علاج")]
    [InlineData("story.tr", "Şifalı bal", "şifalı")]
    [InlineData("story.tr", "SIFALI BAL", "şifalı")]
    [InlineData("story.tr", "bağışıklığı güçlendirir", "bağışıklık")]
    [InlineData("story.en", "A natural remedy for colds", "remedy")]
    [InlineData("story.en", "It HEALS the throat", "heals")]
    [InlineData("story.en", "boosts immunity", "immunity")]
    [InlineData("story.en", "a treatment for coughs", "treat")]
    public void Flags_forbidden_health_claims_in_any_language(string field, string text, string term)
    {
        var findings = Guard.Check(field, text);

        Assert.Contains(findings, f => f.Term == term && f.Severity == FindingSeverity.Blocking && f.Field == field);
    }

    [Fact]
    public void A_turkish_claim_in_an_arabic_field_is_still_caught()
    {
        Assert.Contains(Guard.Check("story.ar", "عسل şifalı من الجبال"), f => f.Term == "şifalı");
    }

    [Theory]
    [InlineData("عسل كستناء من البحر الأسود. داكن، مُرّ قليلًا في آخره، يتبلور ببطء. قُطف في سبتمبر.")]
    [InlineData("يفتح بالبرغموت، ثم يستقر على الورد الطائفي، ويبقى العود على المعصم حتى المساء.")]
    [InlineData("Karadeniz'den kestane balı. Koyu, sonunda hafif acı, yavaş kristalleşir.")]
    [InlineData("Opens with bergamot, settles on Taif rose, and the oud stays on the wrist until evening.")]
    public void The_brand_voice_examples_pass(string text)
    {
        Assert.DoesNotContain(Guard.Check("story", text), f => f.Severity == FindingSeverity.Blocking);
    }

    [Theory]
    [InlineData("تجربة لا تُنسى")]
    [InlineData("Benzersiz bir koku")]
    [InlineData("elevate your ritual")]
    public void Marketing_filler_is_advisory_only(string text)
    {
        var findings = Guard.Check("story", text);

        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.Equal(FindingSeverity.Advisory, f.Severity));
    }

    [Fact]
    public void Checks_many_fields_and_skips_empty_ones()
    {
        var findings = Guard.Check([new("name.tr", "Kestane Balı"), new("story.en", null), new("usage.en", "a cure")]);

        Assert.Single(findings);
        Assert.Equal("usage.en", findings[0].Field);
    }

    [Fact]
    public void Taxonomy_lists_terms_in_three_languages()
    {
        var terms = Taxonomy.ForbiddenClaimTerms();

        Assert.Contains("يعالج", terms);
        Assert.Contains("şifalı", terms);
        Assert.Contains("cure", terms);
    }

    [Fact]
    public void Localized_text_falls_back_to_turkish_then_anything()
    {
        var text = new LocalizedText(new Dictionary<string, string> { ["tr"] = "Bal", ["ar"] = "عسل" });

        Assert.Equal("عسل", text.For("ar"));
        Assert.Equal("Bal", text.For("en"));
        Assert.Equal("x", new LocalizedText(new Dictionary<string, string> { ["en"] = "x" }).For("ar"));
    }

    [Theory]
    [InlineData("rhq-hny-500", true)]
    [InlineData("RHQ-OUD-50", true)]
    [InlineData("a", false)]
    [InlineData("-AB", false)]
    [InlineData("AB CD", false)]
    public void Skus_are_upper_invariant_and_well_formed(string raw, bool valid)
    {
        var sku = Sku.Create(raw);

        Assert.Equal(valid, sku.IsSuccess);
        if (valid)
        {
            Assert.Equal(raw.ToUpperInvariant(), sku.Value.Value);
        }
    }

    [Fact]
    public void Weight_and_volume_are_bounded()
    {
        Assert.True(Weight.FromGrams(500).IsSuccess);
        Assert.Equal("1 kg", Weight.FromGrams(1000).Value.ToString());
        Assert.Equal("250 g", Weight.FromGrams(250).Value.ToString());
        Assert.False(Weight.FromGrams(0).IsSuccess);
        Assert.True(Volume.FromMillilitres(50).IsSuccess);
        Assert.Equal("2 ml", Volume.FromMillilitres(2).Value.ToString());
        Assert.False(Volume.FromMillilitres(-1).IsSuccess);
    }

    [Fact]
    public void Result_guards_its_invariants()
    {
        Assert.Throws<InvalidOperationException>(() => Result.Failure<int>(Error.NotFound("x", "y")).Value);
        Result<int> ok = 5;
        Assert.Equal(5, ok.Value);
        Result<int> failed = Error.Conflict("c", "m");
        Assert.True(failed.IsFailure);
        Assert.Equal(ErrorType.Conflict, failed.Error.Type);
    }
}
