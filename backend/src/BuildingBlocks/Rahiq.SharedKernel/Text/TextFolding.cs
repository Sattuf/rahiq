using System.Globalization;
using System.Text;

namespace Rahiq.SharedKernel.Text;

/// <summary>
/// Folds text for matching and search (content-seo.md §5): case, Turkish i/ı/İ, Latin diacritics
/// (ş ğ ç ö ü), Arabic tashkeel, tatweel, hamza carriers, alef maqsura and taa marbuta.
/// The result is only for comparison, never for display.
/// </summary>
public static class TextFolding
{
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var lastWasSpace = true;

        foreach (var raw in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            var c = raw switch
            {
                'ı' or 'I' or 'İ' => 'i',
                'ة' => 'ه',
                'ى' => 'ي',
                'ٱ' => 'ا',
                'ـ' => '\0',
                _ => char.ToLowerInvariant(raw),
            };

            if (c == '\0')
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().TrimEnd();
    }

    public static bool ContainsArabic(string text) => text.Any(c => c is >= '؀' and <= 'ۿ');
}
