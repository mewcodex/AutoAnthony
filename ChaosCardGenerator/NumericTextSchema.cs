using System.Text;

namespace ChaosCardGenerator;

/// <summary>
/// Shared normalization for identities whose numeric values are slots rather than part of the effect type.
/// This is equivalent to replacing every contiguous Unicode decimal-digit run with one marker, but avoids the
/// repeated Regex work performed for every candidate during card assembly.
/// </summary>
internal static class NumericTextSchema
{
    internal static string Family(string text) => ReplaceDigitRuns(text, "#");

    internal static string Fields(string text) => ReplaceDigitRuns(text, "{n}");

    private static string ReplaceDigitRuns(string text, string marker)
    {
        var firstDigit = -1;
        for (var index = 0; index < text.Length; index++)
        {
            if (!char.IsDigit(text[index])) continue;
            firstDigit = index;
            break;
        }
        if (firstDigit < 0) return text;

        var result = new StringBuilder(text.Length + marker.Length);
        result.Append(text, 0, firstDigit);
        for (var index = firstDigit; index < text.Length;)
        {
            if (!char.IsDigit(text[index]))
            {
                result.Append(text[index++]);
                continue;
            }
            result.Append(marker);
            do index++; while (index < text.Length && char.IsDigit(text[index]));
        }
        return result.ToString();
    }
}
