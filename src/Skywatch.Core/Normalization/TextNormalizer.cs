using System.Text;

namespace Skywatch.Core.Normalization;

/// <summary>Text cleanup before keyword matching (AC-F4.1–F4.7).</summary>
public static class TextNormalizer
{
    private static readonly NormalizedText _empty = new("", "");

    public static NormalizedText Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return _empty;
        }

        var nfkc = text.Normalize(NormalizationForm.FormKC);
        var cleaned = new StringBuilder(nfkc.Length);
        var pendingSpace = false;
        foreach (var c in nfkc)
        {
            if (IsInvisible(c))
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                // Collapse runs and trim: a space is written only before the next visible character.
                pendingSpace = cleaned.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                cleaned.Append(' ');
                pendingSpace = false;
            }

            // AC-F4.4. No other folding: і ≠ и and Latin look-alikes stay as they are (AC-F4.6, D10).
            cleaned.Append(c switch
            {
                'ё' => 'е',
                'Ё' => 'Е',
                _ => c,
            });
        }

        var casePreserved = cleaned.ToString();
        return new NormalizedText(casePreserved.ToLowerInvariant(), casePreserved);
    }

    // AC-F4.2: zero-width space/non-joiner/joiner, word joiner, BOM, soft hyphen.
    private static bool IsInvisible(char c) => c is >= '​' and <= '‍' or '⁠' or '﻿' or '­';
}
