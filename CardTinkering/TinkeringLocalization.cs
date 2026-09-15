using MegaCrit.Sts2.Core.Localization;

namespace AutoAnthonyCardTinkering;

/// <summary>
/// Lightweight runtime localization for the DLL-only workbench UI. Generated card/component prose continues to
/// come from Auto-Anthonyology's structured localization; this class owns only Card Tinkering's own interface text.
/// </summary>
internal static class TinkeringText
{
    internal static bool IsChinese => LocManager.Instance?.Language is "zhs" or "zht";
    internal static bool IsEnglish => !IsChinese;

    internal static string Localize(string chinese, string english) => IsChinese ? chinese : english;
}
