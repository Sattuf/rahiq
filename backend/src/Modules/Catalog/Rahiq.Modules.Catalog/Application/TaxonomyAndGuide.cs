using Rahiq.Application.Abstractions;
using Rahiq.Modules.Catalog.Contracts;
using Rahiq.Modules.Catalog.Domain;
using Rahiq.SharedKernel;

namespace Rahiq.Modules.Catalog.Application;

public sealed record TaxonomyEntryDto(string Code, string Name, string? Family = null);

public sealed record TaxonomyDto(
    IReadOnlyList<TaxonomyEntryDto> Sections,
    IReadOnlyList<TaxonomyEntryDto> ProductTypes,
    IReadOnlyList<TaxonomyEntryDto> Concentrations,
    IReadOnlyList<TaxonomyEntryDto> Families,
    IReadOnlyList<TaxonomyEntryDto> Genders,
    IReadOnlyList<TaxonomyEntryDto> Seasons,
    IReadOnlyList<TaxonomyEntryDto> Notes,
    IReadOnlyList<TaxonomyEntryDto> FloralSources,
    IReadOnlyList<TaxonomyEntryDto> Textures,
    IReadOnlyList<TaxonomyEntryDto> Allergens,
    IReadOnlyList<TaxonomyEntryDto> Warnings,
    IReadOnlyList<string> RegionCodes,
    string CrystallizationNote);

public sealed record GetTaxonomyQuery(string Locale) : IQuery<TaxonomyDto>;

internal sealed class GetTaxonomyHandler : IRequestHandler<GetTaxonomyQuery, TaxonomyDto>
{
    public Task<TaxonomyDto> Handle(GetTaxonomyQuery request, CancellationToken cancellationToken)
    {
        var d = CatalogDictionary.Instance;
        var l = request.Locale;
        IReadOnlyList<TaxonomyEntryDto> Map(IReadOnlyList<DictionaryEntry> entries) => [.. entries.Select(e => new TaxonomyEntryDto(e.Code, e.Name.For(l), e.Family))];

        return Task.FromResult(new TaxonomyDto(
            Map(d.Sections), Map(d.ProductTypes), Map(d.Concentrations), Map(d.Families), Map(d.Genders), Map(d.Seasons),
            Map(d.Notes), Map(d.FloralSources), Map(d.Textures), Map(d.Allergens),
            [.. d.Warnings.Select(w => new TaxonomyEntryDto(w.Code, w.Text.For(l)))],
            AttributeRules.RegionCodes,
            d.CrystallizationNote.For(l)));
    }
}

public sealed record GuideOptionDto(string Code, string Label);

public sealed record GuideQuestionDto(string Code, string Prompt, IReadOnlyList<GuideOptionDto> Options);

public sealed record GetGuideQuestionsQuery(string Locale) : IQuery<IReadOnlyList<GuideQuestionDto>>;

public sealed record RecommendPerfumesQuery(IReadOnlyDictionary<string, string> Answers, string Locale) : IQuery<IReadOnlyList<ProductCardDto>>;

/// <summary>
/// The perfume guide: four simple questions (a scene, when you wear it, what you dislike, who it is for) scored against
/// structured attributes. Scenes are shown as pictures on the storefront; the labels here are their captions.
/// </summary>
internal sealed class PerfumeGuideHandlers(ISender sender)
    : IRequestHandler<GetGuideQuestionsQuery, IReadOnlyList<GuideQuestionDto>>, IRequestHandler<RecommendPerfumesQuery, IReadOnlyList<ProductCardDto>>
{
    private static readonly Dictionary<string, string[]> SceneFamilies = new()
    {
        ["morning_garden"] = ["floral", "fresh"],
        ["winter_majlis"] = ["oud", "oriental"],
        ["seaside"] = ["fresh", "aromatic"],
        ["spice_market"] = ["oriental", "gourmand"],
    };

    private static readonly (string Code, LocalizedText Prompt, (string Code, LocalizedText Label)[] Options)[] Questions =
    [
        ("scene", T("Hangi sahne size benziyor?", "أي مشهد يشبهك؟", "Which scene feels like you?"),
        [
            ("morning_garden", T("Sabah bahçesi", "حديقة صباحية", "A morning garden")),
            ("winter_majlis", T("Kış akşamı sohbet", "مجلس شتوي", "A winter majlis")),
            ("seaside", T("Deniz kıyısı", "شاطئ", "The seaside")),
            ("spice_market", T("Baharat çarşısı", "سوق التوابل", "A spice market")),
        ]),
        ("moment", T("Ne zaman sürüyorsunuz?", "متى تتعطر؟", "When do you wear it?"),
        [
            ("everyday", T("Her gün, iş ve okul", "كل يوم، في العمل", "Every day, at work")),
            ("evening", T("Akşamları", "في المساء", "In the evening")),
            ("occasions", T("Özel günlerde", "في المناسبات", "On special occasions")),
        ]),
        ("dislike", T("Sevmediğiniz?", "ما الذي لا تحبه؟", "What don't you like?"),
        [
            ("too_sweet", T("Fazla tatlı", "الحلو جدًا", "Too sweet")),
            ("too_heavy", T("Ağır ve yoğun", "الثقيل والكثيف", "Heavy and dense")),
            ("too_floral", T("Fazla çiçeksi", "الزهري جدًا", "Too floral")),
            ("nothing", T("Hepsini denerim", "أجرب كل شيء", "I'll try anything")),
        ]),
        ("for", T("Kimin için?", "لمن العطر؟", "Who is it for?"),
        [
            ("feminine", T("Kadın", "نسائي", "Feminine")),
            ("masculine", T("Erkek", "رجالي", "Masculine")),
            ("any", T("Fark etmez", "لا يهم", "Either")),
        ]),
    ];

    public Task<IReadOnlyList<GuideQuestionDto>> Handle(GetGuideQuestionsQuery request, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GuideQuestionDto>>(
            [.. Questions.Select(q => new GuideQuestionDto(q.Code, q.Prompt.For(request.Locale), [.. q.Options.Select(o => new GuideOptionDto(o.Code, o.Label.For(request.Locale)))]))]);

    public async Task<IReadOnlyList<ProductCardDto>> Handle(RecommendPerfumesQuery request, CancellationToken cancellationToken)
    {
        var perfumes = await sender.Send(new ListProductsQuery(new ProductFilter { Section = Sections.Perfume, Limit = 200 }, request.Locale), cancellationToken);
        var answers = request.Answers;
        var wanted = SceneFamilies.GetValueOrDefault(answers.GetValueOrDefault("scene") ?? string.Empty) ?? [];
        var dislike = answers.GetValueOrDefault("dislike");
        var forWhom = answers.GetValueOrDefault("for");
        var moment = answers.GetValueOrDefault("moment");

        return [.. perfumes
            .Where(p => p.Available && p.Type is ProductTypes.Perfume or ProductTypes.Attar)
            .Select(p =>
            {
                var h = p.Highlights;
                var score = h.Families.Count(wanted.Contains) * 3;
                score += forWhom is null or "any" || h.Gender == forWhom ? 2 : h.Gender == "unisex" ? 1 : -3;
                score -= dislike switch
                {
                    "too_sweet" when h.Families.Contains("gourmand") => 4,
                    "too_heavy" when h.Families.Contains("oud") || h.Concentration == "extrait" => 4,
                    "too_floral" when h.Families.Contains("floral") => 4,
                    _ => 0,
                };
                score += moment switch
                {
                    "everyday" when h.Concentration is "edt" or "edc" or "edp" => 1,
                    "evening" or "occasions" when h.Concentration is "extrait" or "oil" or "edp" => 1,
                    _ => 0,
                };
                score += p.HasSample ? 1 : 0; // The guide leads to "try the three samples".
                return (Product: p, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Product.Price)
            .Take(3)
            .Select(x => x.Product)];
    }

    private static LocalizedText T(string tr, string ar, string en) => new(new Dictionary<string, string> { ["tr"] = tr, ["ar"] = ar, ["en"] = en });
}
