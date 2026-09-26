using Rahiq.SharedKernel.Text;

namespace Rahiq.SharedKernel.Compliance;

public enum FindingSeverity
{
    /// <summary>A forbidden health claim (Law 1). Blocks publishing until a manager explicitly approves.</summary>
    Blocking,

    /// <summary>Marketing filler (content-seo.md §2). Shown to the editor, does not block.</summary>
    Advisory,
}

public sealed record ClaimFinding(string Term, string Field, FindingSeverity Severity);

/// <summary>
/// Checks every saved text (product, story, coupon, page) against the forbidden health-claim terms in all
/// three languages, whatever the text's own language: a Turkish claim pasted into an Arabic field still counts.
/// </summary>
public sealed class ClaimsGuard
{
    private static readonly string[] DefaultFiller =
    [
        "فريد من نوعه", "تجربة لا تنسى", "الأفضل على الإطلاق", "أفضل عسل في العالم",
        "benzersiz", "eşsiz deneyim", "premium kalite",
        "seamless", "elevate", "unlock", "one of a kind", "unforgettable experience",
    ];

    private readonly IReadOnlyList<Term> _terms;

    public ClaimsGuard(IEnumerable<string> forbiddenTerms, IEnumerable<string>? fillerTerms = null)
    {
        ArgumentNullException.ThrowIfNull(forbiddenTerms);
        _terms =
        [
            .. forbiddenTerms.Select(t => Term.Create(t, FindingSeverity.Blocking)),
            .. (fillerTerms ?? DefaultFiller).Select(t => Term.Create(t, FindingSeverity.Advisory)),
        ];
    }

    /// <param name="fields">Field name → text, e.g. {"name.tr": "...", "story.ar": "..."}.</param>
    public IReadOnlyList<ClaimFinding> Check(IEnumerable<KeyValuePair<string, string?>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var findings = new List<ClaimFinding>();

        foreach (var (field, text) in fields)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var folded = TextFolding.Fold(text);
            var tokens = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var term in _terms.Where(term => term.Matches(folded, tokens)))
            {
                findings.Add(new ClaimFinding(term.Original, field, term.Severity));
            }
        }

        return findings;
    }

    public IReadOnlyList<ClaimFinding> Check(string field, string? text) => Check([new(field, text)]);

    private sealed record Term(string Original, string Folded, string[] Words, bool IsArabic, FindingSeverity Severity)
    {
        public static Term Create(string original, FindingSeverity severity)
        {
            var folded = TextFolding.Fold(original);
            var isArabic = TextFolding.ContainsArabic(folded);

            // Arabic present-tense verbs: match the stem so every person is caught (يعالج → عالج: تعالج، يعالج، معالجة).
            if (isArabic && folded.Length >= 4 && folded[0] == 'ي')
            {
                folded = folded[1..];
            }

            return new Term(original, folded, folded.Split(' ', StringSplitOptions.RemoveEmptyEntries), isArabic, severity);
        }

        public bool Matches(string foldedText, string[] tokens)
        {
            if (Words.Length == 0)
            {
                return false;
            }

            // Arabic attaches prefixes (ال، و، ب، ل، ف) to words, so any occurrence counts.
            if (IsArabic)
            {
                return foldedText.Contains(Folded, StringComparison.Ordinal);
            }

            // Latin: each word of the term must start a consecutive token (treat → treatment, şifa → şifalı).
            for (var i = 0; i <= tokens.Length - Words.Length; i++)
            {
                var matched = true;
                for (var w = 0; w < Words.Length && matched; w++)
                {
                    matched = StartsWithWord(tokens[i + w], Words[w]);
                }

                if (matched)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Prefix match that also accepts Turkish consonant softening before a vowel suffix:
        /// bağışıklık → bağışıklığı (k→ğ), ilaç → ilacı (ç→c), kitap → kitabı (p→b), dert → derdi (t→d).
        /// Text is folded, so ğ is g and ç is c here.
        /// </summary>
        private static bool StartsWithWord(string token, string word)
        {
            if (token.StartsWith(word, StringComparison.Ordinal))
            {
                return true;
            }

            var softened = word[^1] switch { 'k' => 'g', 'p' => 'b', 't' => 'd', _ => '\0' };
            return softened != '\0' && token.Length >= word.Length &&
                token.AsSpan(0, word.Length - 1).SequenceEqual(word.AsSpan(0, word.Length - 1)) && token[word.Length - 1] == softened;
        }
    }
}
