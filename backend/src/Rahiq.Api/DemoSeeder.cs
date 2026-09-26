#pragma warning disable CA1861 // Seed data is literal and runs once.
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Catalog.Application;
using Rahiq.Modules.Content.Application;
using Rahiq.Modules.Inventory.Application;
using Rahiq.Modules.Pricing.Application;
using Rahiq.SharedKernel;

namespace Rahiq.Api;

/// <summary>
/// Development and staging data. Every text goes through the same commands (and the same claims guard) as the admin.
/// Products have NO photos on purpose (Law 8): the storefront shows a neutral placeholder until the real shoot.
/// </summary>
internal sealed partial class DemoSeeder(ISender sender, RahiqDbContext db, IClock clock, ILogger<DemoSeeder> logger)
{
    private static TranslationDto[] Tr(string trName, string arName, string enName, string trShort, string arShort, string enShort, string trStory, string arStory, string enStory, string? trUse = null, string? arUse = null, string? enUse = null) =>
    [
        new("tr", trName, trShort, trStory, trUse, null, null),
        new("ar", arName, arShort, arStory, arUse, null, null),
        new("en", enName, enShort, enStory, enUse, null, null),
    ];

    public async Task SeedAsync(CancellationToken ct)
    {
        if (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM catalog.products").FirstAsync(ct) > 0)
        {
            LogSkipped(logger);
            return;
        }

        await Notes(ct);
        var today = clock.Today;

        // ── Perfume ───────────────────────────────────────────────
        var oud = await Product("perfume", "oud-taif-rose", Tr(
            "Oud & Taif Gülü", "عود وورد طائفي", "Oud & Taif Rose",
            "Safranla açılan, Taif gülüyle yerleşen, akşama kadar kalan bir oud.", "يفتح بالزعفران، ويستقر على الورد الطائفي، ويبقى حتى المساء.", "Opens with saffron, settles on Taif rose, stays until evening.",
            "Bergamot ve safranla açılır, ardından Taif gülü yerleşir; oud ve amber bileğinizde akşama kadar kalır. Eau de Parfum, %18 esans.",
            "يفتح بالبرغموت والزعفران، ثم يستقر على الورد الطائفي، ويبقى العود والعنبر على المعصم حتى المساء. ماء عطر بتركيز 18%.",
            "Opens with bergamot and saffron, then Taif rose settles in; oud and amber stay on the wrist until evening. Eau de Parfum, 18% concentrate.",
            "Nabız noktalarına 2-3 sıkım.", "رشتان أو ثلاث على نقاط النبض.", "Two or three sprays on pulse points."),
            new { concentration = "edp", families = new[] { "oud", "oriental" }, gender = "unisex", seasons = new[] { "autumn", "winter" }, notes = new { top = new[] { "bergamot", "saffron" }, heart = new[] { "taif-rose" }, @base = new[] { "oud", "amber" } }, intensity = 4, longevity = 5, sillage = 4, inci = Inci, declaredAllergens = new[] { "LIMONENE", "LINALOOL", "CITRONELLOL", "GERANIOL" } },
            featured: true, sort: 1,
            [("RHQ-OUD-2", 2, null, 30, "flammable", true, 9_900), ("RHQ-OUD-50", 50, null, 420, "flammable", false, 245_000), ("RHQ-OUD-100", 100, null, 620, "flammable", false, 395_000)], ct);

        var bergamot = await Product("perfume", "morning-bergamot", Tr(
            "Sabah Bergamotu", "برغموت الصباح", "Morning Bergamot",
            "Bergamot, limon ve lavanta; sedirle biten ferah bir koku.", "برغموت وليمون ولافندر، يختم بخشب الأرز.", "Bergamot, lemon and lavender, closing on cedar.",
            "Bergamot, limon ve mandalinayla parlak açılır, ortada lavanta, dipte sedir ve beyaz misk. Eau de Toilette; gündüz ve iş için.",
            "يفتح مشرقًا بالبرغموت والليمون واليوسفي، وفي القلب لافندر، وفي القاعدة خشب الأرز والمسك الأبيض. ماء تواليت للنهار والعمل.",
            "Opens bright with bergamot, lemon and mandarin; lavender at the heart; cedar and white musk at the base. Eau de Toilette for daytime and work."),
            new { concentration = "edt", families = new[] { "fresh", "aromatic" }, gender = "unisex", seasons = new[] { "spring", "summer" }, notes = new { top = new[] { "bergamot", "lemon", "mandarin" }, heart = new[] { "lavender" }, @base = new[] { "cedar", "white-musk" } }, intensity = 2, longevity = 3, sillage = 2, inci = Inci, declaredAllergens = new[] { "LIMONENE", "LINALOOL", "CITRAL" } },
            featured: false, sort: 2,
            [("RHQ-BRG-2", 2, null, 30, "flammable", true, 7_900), ("RHQ-BRG-50", 50, null, 420, "flammable", false, 185_000)], ct);

        await Product("perfume", "amber-majlis", Tr(
            "Amber Meclisi", "مجلس العنبر", "Amber Majlis",
            "Kakule, tütsü ve vanilya; kış akşamları için yoğun bir extrait.", "هيل وبخور وفانيليا؛ خلاصة عطر لأمسيات الشتاء.", "Cardamom, incense and vanilla: a dense extrait for winter evenings.",
            "Kakuleyle ısınır, tütsüyle derinleşir, amber, vanilya ve tonka ile yumuşar. Extrait de Parfum, %25 esans; az miktar yeterlidir.",
            "يدفأ بالهيل، ويتعمق بالبخور، ويلين بالعنبر والفانيليا والتونكا. خلاصة عطر بتركيز 25%؛ القليل منه يكفي.",
            "Warms with cardamom, deepens with incense, softens with amber, vanilla and tonka. Extrait de Parfum, 25% concentrate; a little goes far."),
            new { concentration = "extrait", families = new[] { "oriental", "gourmand" }, gender = "unisex", seasons = new[] { "winter" }, notes = new { top = new[] { "cardamom" }, heart = new[] { "incense" }, @base = new[] { "amber", "vanilla", "tonka" } }, intensity = 5, longevity = 5, sillage = 4, inci = Inci, declaredAllergens = new[] { "COUMARIN", "LINALOOL" } },
            featured: false, sort: 3,
            [("RHQ-AMB-2", 2, null, 30, "flammable", true, 11_900), ("RHQ-AMB-50", 50, null, 420, "flammable", false, 325_000)], ct);

        await Product("attar", "sandal-attar", Tr(
            "Sandal Esans Yağı", "دهن الصندل", "Sandalwood Attar",
            "Alkolsüz, yoğun bir sandal ağacı yağı.", "دهن صندل مركز بلا كحول.", "A dense, alcohol-free sandalwood oil.",
            "Sandal ağacı ve bir damla vetiverle hazırlanan alkolsüz esans yağı. Tenle ısındıkça açılır.",
            "دهن عطر بلا كحول من خشب الصندل مع قطرة من نجيل الهند. ينفتح كلما دفئ على الجلد.",
            "An alcohol-free perfume oil of sandalwood with a drop of vetiver. It opens as it warms on the skin.",
            "Bilek ve kulak arkasına bir damla.", "قطرة على المعصم وخلف الأذن.", "One drop on the wrist and behind the ear."),
            new { concentration = "oil", families = new[] { "woody" }, gender = "unisex", seasons = new[] { "autumn", "winter" }, notes = new { top = Array.Empty<string>(), heart = new[] { "sandalwood" }, @base = new[] { "vetiver" } }, intensity = 3, longevity = 5, sillage = 2, inci = new[] { "CAPRYLIC/CAPRIC TRIGLYCERIDE", "PARFUM (FRAGRANCE)", "SANTALUM ALBUM OIL" }, declaredAllergens = Array.Empty<string>() },
            featured: false, sort: 4,
            [("RHQ-SND-3", 3, null, 60, "fragile", false, 69_000), ("RHQ-SND-6", 6, null, 80, "fragile", false, 119_000)], ct);

        // ── Honey ─────────────────────────────────────────────────
        var chestnut = await Product("honey", "kestane-bali", Tr(
            "Kestane Balı", "عسل الكستناء", "Chestnut Honey",
            "Karadeniz'den koyu, sonunda hafif acı bir bal.", "عسل داكن من البحر الأسود، مُرّ قليلًا في آخره.", "Dark honey from the Black Sea, gently bitter at the finish.",
            "Rize yaylalarından kestane balı. Koyu, sonunda hafif acı, yavaş kristalleşir. Eylülde süzüldü; her kavanozun parti raporu QR kodundadır.",
            "عسل كستناء من مرتفعات ريزه. داكن، مُرّ قليلًا في آخره، يتبلور ببطء. قُطف في سبتمبر، وتقرير الدفعة في رمز QR على كل برطمان.",
            "Chestnut honey from the Rize highlands. Dark, gently bitter at the finish, slow to crystallise. Harvested in September; each jar's batch report is behind its QR code.",
            "Kahvaltıda peynirle ya da ılık çayla.", "مع الجبن في الفطور أو مع شاي دافئ.", "With cheese at breakfast or with warm tea."),
            new { floralSource = "chestnut", regionCode = "black_sea", region = new { tr = "Rize, Karadeniz", ar = "ريزه، البحر الأسود", en = "Rize, Black Sea" }, altitude = 1200, taste = new { sweetness = 2, bitterness = 4, intensity = 5 }, texture = "liquid", colorScale = 5 },
            featured: true, sort: 10,
            [("RHQ-KST-250", null, 250, 420, "liquid", false, 42_000), ("RHQ-KST-500", null, 500, 720, "liquid", false, 74_000), ("RHQ-KST-1000", null, 1000, 1350, "liquid", false, 135_000)], ct);

        var thyme = await Product("honey", "kekik-bali", Tr(
            "Kekik Balı", "عسل الزعتر", "Thyme Honey",
            "Ege dağlarından, hafif baharatlı kremsi bir bal.", "عسل كريمي من جبال إيجة بلمسة توابل خفيفة.", "A creamy honey from the Aegean hills, lightly herbal.",
            "Muğla'nın kekikli yamaçlarından. Açık renkli, kremsi, hafif baharatlı bir koku taşır. Haziranda süzüldü.",
            "من سفوح موغلا المليئة بالزعتر. فاتح اللون، كريمي، برائحة توابل خفيفة. قُطف في يونيو.",
            "From the thyme-covered slopes of Muğla. Light, creamy, with a faintly herbal scent. Harvested in June."),
            new { floralSource = "thyme", regionCode = "aegean", region = new { tr = "Muğla, Ege", ar = "موغلا، إيجة", en = "Muğla, Aegean" }, taste = new { sweetness = 4, bitterness = 1, intensity = 3 }, texture = "creamy", colorScale = 2 },
            featured: false, sort: 11,
            [("RHQ-KKK-500", null, 500, 720, "liquid", false, 69_000)], ct);

        await Product("honey", "cam-bali", Tr(
            "Çam Balı", "عسل الصنوبر", "Pine Honey",
            "Ege çamlarından, az tatlı, kolay kristalleşmeyen bir bal.", "عسل صنوبر من إيجة، قليل الحلاوة، بطيء التبلور.", "Aegean pine honey: less sweet, slow to crystallise.",
            "Çam ağaçlarındaki salgıdan üretilir; az tatlı, mineralli, uzun süre akışkan kalır.",
            "يُنتج من إفرازات أشجار الصنوبر؛ قليل الحلاوة، غني بالطعم، ويبقى سائلًا مدة طويلة.",
            "Made from secretions on pine trees; less sweet, mineral, stays liquid for a long time."),
            new { floralSource = "pine", regionCode = "aegean", taste = new { sweetness = 2, bitterness = 2, intensity = 3 }, texture = "liquid", colorScale = 3 },
            featured: false, sort: 12,
            [("RHQ-CAM-500", null, 500, 720, "liquid", false, 58_000)], ct);

        await Product("honey", "sidr-bali", Tr(
            "Sidr Balı", "عسل السدر", "Sidr Honey",
            "Sidr ağacı çiçeklerinden, yoğun ve karamel tonlu.", "عسل السدر، كثيف بنكهة الكراميل.", "From Sidr blossoms: thick, with caramel tones.",
            "Sidr ağacının çiçeklerinden gelen koyu kehribar renkli bir bal. İthal partidir; menşe ve analiz raporu parti sayfasındadır.",
            "عسل كهرماني داكن من أزهار شجرة السدر. دفعة مستوردة، وبلد المنشأ وتقرير التحليل في صفحة الدفعة.",
            "A dark amber honey from the blossoms of the Sidr tree. An imported batch; origin and lab report are on the batch page."),
            new { floralSource = "sidr", regionCode = "imported", taste = new { sweetness = 4, bitterness = 1, intensity = 4 }, texture = "liquid", colorScale = 4 },
            featured: true, sort: 13,
            [("RHQ-SDR-250", null, 250, 420, "liquid", false, 89_000)], ct);

        await Product("comb_honey", "petek-bal", Tr(
            "Petek Bal", "عسل بالشمع", "Comb Honey",
            "Kesilmemiş petek; bal ve mum bir arada.", "قرص شمع كامل، العسل والشمع معًا.", "Uncut comb: honey and wax together.",
            "Çerçeveden kesilmiş doğal petek. Mumuyla birlikte yenir; sıcakta yumuşar.",
            "قرص عسل طبيعي مقطوع من الإطار، يؤكل مع شمعه، ويلين في الحر.",
            "Natural comb cut from the frame. Eaten with its wax; softens in the heat."),
            new { floralSource = "multifloral", regionCode = "central_anatolia", texture = "crystallized", colorScale = 2 },
            featured: false, sort: 14,
            [("RHQ-PTK-500", null, 500, 800, "fragile", false, 95_000)], ct);

        var nuts = await Product("nuts_in_honey", "ballı-ceviz-findik".Replace("ı", "i", StringComparison.Ordinal), Tr(
            "Ballı Ceviz ve Fındık", "جوز وبندق بالعسل", "Walnuts & Hazelnuts in Honey",
            "Çiçek balı içinde bütün ceviz ve fındık.", "جوز وبندق كاملان في عسل الزهور.", "Whole walnuts and hazelnuts in wildflower honey.",
            "%60 çiçek balı, %25 ceviz içi, %15 Giresun fındığı. Kavanozda katmanlar halinde, kesildiğinde bal aralarından süzülür.",
            "60% عسل زهور، 25% جوز، 15% بندق من غيرسون. طبقات في البرطمان، ويسيل العسل بينها عند التقطيع.",
            "60% wildflower honey, 25% walnuts, 15% Giresun hazelnuts. Layered in the jar; the honey runs between them when cut.",
            "Kahvaltıda ya da yoğurdun üstünde bir kaşık.", "ملعقة في الفطور أو فوق اللبن.", "A spoonful at breakfast or over yoghurt."),
            new { honeyType = "multifloral", composition = new object[] { new { ingredient = new { tr = "Çiçek balı", ar = "عسل زهور", en = "Wildflower honey" }, percent = 60 }, new { ingredient = new { tr = "Ceviz", ar = "جوز", en = "Walnut" }, percent = 25 }, new { ingredient = new { tr = "Fındık", ar = "بندق", en = "Hazelnut" }, percent = 15 } } },
            featured: false, sort: 15,
            [("RHQ-NUT-250", null, 250, 420, "liquid", false, 54_000)], ct, allergens: ["tree-nuts"]);

        await Product("honey_blend", "corekotu-zencefil-bal", Tr(
            "Çörek Otlu ve Zencefilli Bal", "عسل بالحبة السوداء والزنجبيل", "Honey with Black Seed & Ginger",
            "Çiçek balına çörek otu ve zencefil; kış akşamlarının geleneksel karışımı.", "عسل زهور بالحبة السوداء والزنجبيل؛ خلطة تقليدية لأمسيات الشتاء.", "Wildflower honey with black seed and ginger: a traditional winter blend.",
            "%90 çiçek balı, %6 çörek otu, %4 toz zencefil. Nesillerdir kışın hazırlanan bir karışım; tadı sıcak ve hafif acıdır.",
            "90% عسل زهور، 6% حبة سوداء، 4% زنجبيل مطحون. خلطة تُحضّر في الشتاء منذ أجيال، طعمها دافئ ولاذع قليلًا.",
            "90% wildflower honey, 6% black seed, 4% ground ginger. A blend made in winter for generations; warm and slightly sharp.",
            "Ilık çayın yanında bir tatlı kaşığı.", "ملعقة صغيرة مع شاي دافئ.", "A teaspoon alongside warm tea."),
            new { honeyType = "multifloral", composition = new object[] { new { ingredient = new { tr = "Çiçek balı", ar = "عسل زهور", en = "Wildflower honey" }, percent = 90 }, new { ingredient = new { tr = "Çörek otu", ar = "حبة سوداء", en = "Black seed" }, percent = 6 }, new { ingredient = new { tr = "Zencefil", ar = "زنجبيل", en = "Ginger" }, percent = 4 } } },
            featured: false, sort: 16,
            [("RHQ-BLD-250", null, 250, 420, "liquid", false, 49_000)], ct);

        // ── Shared: the gift box (the bridge between the two rooms) and a fixed bundle ──
        var box = await Product("gift_box", "iki-isik-hediye-kutusu", Tr(
            "İki Işık Hediye Kutusu", "صندوق الضوءين", "Two Lights Gift Box",
            "Bir koku, bir bal ve sizin mesajınız.", "عطر وعسل ورسالتك.", "One fragrance, one honey and your message.",
            "Bir parfüm ve bir kavanoz bal seçin, mesajınızı yazın. Kutu, iki bölümün şeritleriyle kapanır; dilerseniz fiyatsız gönderilir.",
            "اختر عطرًا وبرطمان عسل واكتب رسالتك. يُغلق الصندوق بشريطي القسمين، ويمكن إرساله بلا أسعار.",
            "Choose a fragrance and a jar of honey, write your message. The box closes with both sections' ribbons and can ship without prices."),
            new { }, featured: true, sort: 0,
            [("RHQ-BOX-2L", null, 400, 500, "fragile", false, 14_900)], ct);
        await sender.Send(new SetGiftSlotsCommand(box.ProductId, [new("perfume", ["perfume"], true, 0), new("honey", ["honey"], true, 1)]), ct);

        var bundle = await Product("bundle", "kahvalti-ikilisi", Tr(
            "Kahvaltı İkilisi", "ثنائي الفطور", "Breakfast Pair",
            "Kestane ve kekik balı, birlikte daha uygun.", "عسل الكستناء وعسل الزعتر معًا بسعر أقل.", "Chestnut and thyme honey together, for less.",
            "Koyu ve açık iki bal: kestane balının acımsı sonu ve kekik balının yumuşaklığı. İki adet 500 g kavanoz.",
            "عسلان داكن وفاتح: مرارة الكستناء الخفيفة ونعومة الزعتر. برطمانان بوزن 500 غ.",
            "Two honeys, dark and light: chestnut's bitter finish and thyme's softness. Two 500 g jars."),
            new { }, featured: false, sort: 20,
            [("RHQ-BND-KAH", null, 1000, 1500, "liquid", false, 129_000)], ct);
        await sender.Send(new SetBundleItemsCommand(bundle.ProductId, bundle.Variants[0], [new(chestnut.Variants[1], 1), new(thyme.Variants[0], 1)]), ct);

        foreach (var product in new[] { box, bundle })
        {
            Check(await sender.Send(new PublishProductCommand(product.ProductId, false, null), ct), product.Slug);
        }

        // ── Batches (FEFO demo: one chestnut batch expires too soon to be sold) ──
        var lab = JsonSerializer.SerializeToElement(new { moisture = 17.2, hmf = 8.4, diastase = 14, pollen = new { name = "Castanea sativa", percent = 91 }, lab = "Akredite laboratuvar (örnek)", reportDate = "2026-09-12" });
        var origin = JsonSerializer.SerializeToElement(new { region = "Rize, Karadeniz", altitude = 1200, season = "2026-09", beekeeper = "Ahmet Usta" });
        foreach (var variant in chestnut.Variants)
        {
            await Batch(variant, "KST-2609A", 24, today.AddDays(-15), today.AddYears(2), origin, lab, ct);
        }

        await Batch(chestnut.Variants[1], "KST-2503Z", 6, today.AddYears(-2), today.AddDays(45), origin, null, ct); // Never sold: < 60 days left.

        foreach (var (p, code) in new[] { (thyme, "KKK-2606"), (nuts, "NUT-2609") })
        {
            foreach (var variant in p.Variants)
            {
                await Batch(variant, code, 18, today.AddDays(-60), today.AddMonths(18), null, null, ct);
            }
        }

        foreach (var slug in new[] { "cam-bali", "sidr-bali", "petek-bal", "corekotu-zencefil-bal" })
        {
            var created = await ProductBySlug(slug, ct);
            foreach (var variant in created.Variants)
            {
                await Batch(variant, $"{slug[..3].ToUpperInvariant()}-2608", 15, today.AddDays(-30), today.AddMonths(20), null, null, ct);
            }
        }

        foreach (var slug in new[] { "oud-taif-rose", "morning-bergamot", "amber-majlis", "sandal-attar" })
        {
            var created = await ProductBySlug(slug, ct);
            foreach (var variant in created.Variants)
            {
                await Batch(variant, $"P{slug[..3].ToUpperInvariant()}-26A", variant == created.Variants[0] ? 60 : 12, null, null, null, null, ct);
            }
        }

        await Batch(box.Variants[0], "BOX-26A", 40, null, null, null, null, ct);

        // ── Coupons ──
        await sender.Send(new SaveCouponCommand(null, new CouponInput("HOSGELDIN10", "percent", 1_000, 50_000, null, 500, 1, null, null, "Hoş geldin indirimi", true)), ct);
        await sender.Send(new SaveCouponCommand(null, new CouponInput("KARGO", "free_shipping", 0, 30_000, null, null, null, null, null, "Ücretsiz kargo", true)), ct);

        await Pages(ct);
        LogSeeded(logger);
    }

    private static readonly string[] Inci = ["ALCOHOL DENAT.", "PARFUM (FRAGRANCE)", "AQUA (WATER)", "LIMONENE", "LINALOOL", "CITRONELLOL", "GERANIOL"];

    private sealed record Seeded(Guid ProductId, string Slug, List<Guid> Variants);

    private async Task<Seeded> Product(string type, string slug, TranslationDto[] translations, object attributes, bool featured, int sort,
        (string Sku, int? Ml, int? G, int ShipG, string Class, bool Sample, long Price)[] variants, CancellationToken ct, string[]? allergens = null)
    {
        var id = Check(await sender.Send(new CreateProductCommand(type, slug), ct), slug);
        var version = Check(await sender.Send(new AdminGetProductQuery(id), ct), slug).Version;
        var saved = Check(await sender.Send(new UpdateProductCommand(id, version, slug, translations, JsonSerializer.SerializeToElement(attributes), [], allergens ?? [], featured, sort), ct), slug);
        var ids = new List<Guid>();
        var order = 0;
        foreach (var v in variants)
        {
            var variantId = Check(await sender.Send(new UpsertVariantCommand(id, null, new VariantInput(v.Sku, v.Ml, v.G, null, v.ShipG, v.Class, v.Sample, true, order++)), ct), v.Sku);
            Check(await sender.Send(new SetPriceCommand(variantId, v.Price), ct), v.Sku);
            ids.Add(variantId);
        }

        if (type is not ("gift_box" or "bundle"))
        {
            Check(await sender.Send(new PublishProductCommand(id, false, null), ct), slug);
        }

        _ = saved;
        return new Seeded(id, slug, ids);
    }

    private async Task<Seeded> ProductBySlug(string slug, CancellationToken ct)
    {
        var product = await db.Set<Modules.Catalog.Domain.Product>().AsNoTracking().Include(p => p.Variants).FirstAsync(p => p.Slug == slug, ct);
        return new Seeded(product.Id, slug, [.. product.Variants.OrderBy(v => v.SortOrder).Select(v => v.Id)]);
    }

    private async Task Batch(Guid variantId, string code, int qty, DateOnly? produced, DateOnly? bestBefore, JsonElement? origin, JsonElement? lab, CancellationToken ct) =>
        Check(await sender.Send(new ReceiveBatchCommand(variantId, code, qty, produced, bestBefore, origin, lab), ct), code);

    private async Task Notes(CancellationToken ct)
    {
        foreach (var note in Modules.Catalog.Domain.CatalogDictionary.Instance.Notes)
        {
            await db.Database.ExecuteSqlAsync($"INSERT INTO catalog.notes (id, family, names) VALUES ({note.Code}, {note.Family}, {JsonSerializer.Serialize(note.Name)}::jsonb) ON CONFLICT (id) DO NOTHING", ct);
        }
    }

    private async Task Pages(CancellationToken ct)
    {
        const string draft = "> Taslak metin: yayından önce avukat tarafından her dilde incelenecektir.\n\n";
        var pages = new (string Slug, string Kind, string Locale, string Title, string Body)[]
        {
            ("gizlilik", "legal", "tr", "KVKK Aydınlatma Metni", draft + "Kişisel verileriniz (ad, iletişim, adres, sipariş bilgileri) siparişinizin kurulması ve ifası, faturalandırma ve yasal yükümlülükler için 6698 sayılı KVKK md. 5/2 kapsamında işlenir. Pazarlama iletileri yalnızca ayrıca verdiğiniz açık rıza ile ve İYS kaydıyla gönderilir. Haklarınız için: destek@rahiq.example"),
            ("gizlilik", "legal", "ar", "نص الإعلام عن البيانات الشخصية", "> مسودة: تُراجع مع المحامي قبل النشر.\n\nنستخدم بياناتك (الاسم، التواصل، العنوان، الطلبات) لإنشاء طلبك وتنفيذه والفوترة والالتزامات القانونية وفق قانون حماية البيانات التركي رقم 6698. لا نرسل رسائل تسويقية إلا بموافقتك الصريحة المنفصلة والمسجلة في نظام İYS."),
            ("gizlilik", "legal", "en", "Privacy notice (KVKK)", "> Draft: to be reviewed by the lawyer before publishing.\n\nWe process your personal data (name, contact, address, orders) to set up and deliver your order, invoice it and meet legal duties under Turkish Law No. 6698. Marketing messages are sent only with your separate, explicit consent, registered with İYS."),
            ("iade", "legal", "tr", "İade ve Cayma", draft + "Teslimattan itibaren 14 gün içinde cayma hakkınız vardır. Bal, ballı ürünler ve koruma bandı açılmış parfümler sağlık ve hijyen nedeniyle cayma kapsamı dışındadır; ürün hasarlı veya yanlış geldiyse her zaman iade edilir (fotoğraf gerekir)."),
            ("iade", "legal", "ar", "الإرجاع والانسحاب", "> مسودة: تُراجع مع المحامي قبل النشر.\n\nيحق لك الانسحاب خلال 14 يومًا من التسليم. العسل ومنتجاته والعطور التي فُتح شريط حمايتها مستثناة لأسباب صحية؛ ويُقبل إرجاع أي منتج وصل تالفًا أو خاطئًا دائمًا (مع صور)."),
            ("iade", "legal", "en", "Returns and withdrawal", "> Draft: to be reviewed by the lawyer before publishing.\n\nYou may withdraw within 14 days of delivery. Honey products and perfumes whose protective seal is opened are excluded for hygiene reasons; anything that arrives damaged or wrong is always taken back (photos required)."),
            ("cerezler", "legal", "tr", "Çerez Politikası", draft + "Zorunlu çerezler sepet ve oturum içindir. Analitik çerezler yalnızca onay verirseniz yüklenir ve onayınızı her zaman geri alabilirsiniz."),
            ("hakkimizda", "story", "tr", "Rahiq: çiçeğin özü", "Arı ve attar aynı şeyi yapar: çiçeğe gider ve özüyle döner. Biri bal olur, diğeri koku.\n\n**Satıcı bilgileri (ETBİS):** [Şirket unvanı] · [Adres] · MERSİS [no] · ETBİS [no]"),
            ("hakkimizda", "story", "ar", "رحيق: خلاصة الزهر", "النحلة والعطّار يفعلان الشيء نفسه: يذهبان إلى الزهرة ويعودان بخلاصتها. تصير عند الأولى عسلًا، وعند الثاني عطرًا."),
            ("hakkimizda", "story", "en", "Rahiq: the essence of the flower", "The bee and the perfumer do the same thing: they go to the flower and come back with its essence. One makes honey, the other fragrance."),
            ("bal-neden-kristallesir", "guide", "tr", "Bal neden kristalleşir?", "Kristalleşme, balın türüne ve sıcaklığa bağlı doğal bir süreçtir; bozulduğu anlamına gelmez. Akışkan hale getirmek için kavanozu 40°C'yi geçmeyen ılık suya koyun."),
            ("bal-neden-kristallesir", "guide", "ar", "لماذا يتبلور العسل؟", "التبلور ظاهرة طبيعية تعتمد على نوع العسل ودرجة الحرارة، ولا تعني أنه مغشوش. لإعادته سائلًا ضع البرطمان في ماء دافئ لا يتجاوز 40 درجة."),
            ("bal-neden-kristallesir", "guide", "en", "Why does honey crystallise?", "Crystallisation is natural and depends on the honey and the temperature; it does not mean it is fake. To make it liquid again, stand the jar in warm water below 40°C."),
            ("extrait-edp-edt", "guide", "tr", "Extrait, EDP, EDT ve esans yağı", "Fark, esans oranındadır: Extrait %20-30, EDP %15-20, EDT %5-15. Esans yağları alkolsüzdür ve tenle ısındıkça açılır."),
            ("extrait-edp-edt", "guide", "ar", "الفرق بين الخلاصة وماء العطر وماء التواليت ودهن العطر", "الفرق في نسبة الزيت العطري: الخلاصة 20-30%، ماء العطر 15-20%، ماء التواليت 5-15%. دهن العطر بلا كحول وينفتح مع دفء الجلد."),
            ("extrait-edp-edt", "guide", "en", "Extrait, EDP, EDT and perfume oil", "The difference is the concentrate: Extrait 20-30%, EDP 15-20%, EDT 5-15%. Perfume oils are alcohol-free and open as they warm on the skin."),
        };

        foreach (var p in pages)
        {
            var saved = Check(await sender.Send(new SavePageCommand(null, new PageInput(p.Slug, p.Locale, p.Kind, p.Title, null, p.Body, null)), ct), p.Slug);
            Check(await sender.Send(new PublishPageCommand(saved.Id, true, false), ct), p.Slug);
        }
    }

    private static T Check<T>(Result<T> result, string what) =>
        result.IsSuccess ? result.Value : throw new InvalidOperationException($"Seeding {what} failed: {result.Error.Code} {result.Error.Message} {JsonSerializer.Serialize(result.Error.Details)}");

    private static void Check(Result result, string what)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Seeding {what} failed: {result.Error.Code} {result.Error.Message} {JsonSerializer.Serialize(result.Error.Details)}");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Catalog already has products; seeding skipped")]
    private static partial void LogSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo data seeded")]
    private static partial void LogSeeded(ILogger logger);
}
