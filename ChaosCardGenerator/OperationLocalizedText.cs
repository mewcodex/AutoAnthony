using System.Globalization;
using System.Text.RegularExpressions;

namespace ChaosCardGenerator;

/// <summary>
/// Bilingual presentation templates whose placeholders name RuntimeSpec value slots. Templates are presentation
/// data only; changing their prose cannot change execution, valuation, legality, or upgrade selection.
/// </summary>
public sealed record OperationTextSlot(string Id, string ChineseValue, string EnglishValue)
{
    internal void Validate()
    {
        OperationRuntimeSpec.ValidateId(Id, nameof(Id), required: true);
        if (string.IsNullOrEmpty(ChineseValue) || string.IsNullOrEmpty(EnglishValue))
            throw new InvalidOperationException($"Localized text slot {Id} must provide both languages.");
    }
}

public sealed record OperationLocalizedText(string ChineseTemplate, string? EnglishTemplate = null,
    IReadOnlyList<OperationTextSlot>? TextSlots = null)
{
    private static readonly Regex Placeholder = new(@"\[\[(?<id>[a-z0-9_]+)(?::(?<format>[a-z_]+))?\]\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] CardinalWords =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
            "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen",
            "nineteen", "twenty"];
    private static readonly string[] OrdinalWords =
        ["zeroth", "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth",
            "tenth", "eleventh", "twelfth", "thirteenth", "fourteenth", "fifteenth", "sixteenth",
            "seventeenth", "eighteenth", "nineteenth", "twentieth"];

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> ChineseSlots => Slots(ChineseTemplate);
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> EnglishSlots => EnglishTemplate is null ? [] : Slots(EnglishTemplate);

    public void Validate(OperationRuntimeSpec spec)
    {
        foreach (var slot in TextSlots ?? [])
        {
            if (slot is null)
                throw new InvalidOperationException("Localized text slots cannot contain null.");
            slot.Validate();
        }
        if ((TextSlots ?? []).Select(slot => slot.Id).Distinct(StringComparer.Ordinal).Count()
            != (TextSlots?.Count ?? 0))
            throw new InvalidOperationException("Localized text-slot IDs must be unique.");
        ValidateTemplate(ChineseTemplate, spec, "Chinese");
        if (EnglishTemplate is not null) ValidateTemplate(EnglishTemplate, spec, "English");
    }

    internal OperationLocalizedText ValidateAndReturn(OperationRuntimeSpec spec)
    {
        Validate(spec);
        return this;
    }

    public string RenderChinese(OperationRuntimeSpec spec,
        IReadOnlyDictionary<string, string>? replacements = null) => Render(ChineseTemplate, spec, replacements,
        chinese: true);

    public string? RenderEnglish(OperationRuntimeSpec spec,
        IReadOnlyDictionary<string, string>? replacements = null) => EnglishTemplate is null
        ? null
        : Render(EnglishTemplate, spec, replacements, chinese: false);

    /// <summary>
    /// Replaces one reviewed literal with a named presentation slot. This is an authoring/load-boundary operation;
    /// subsequent card generation, derivative rebinding and upgrades mutate the slot value, never localized prose.
    /// </summary>
    public OperationLocalizedText BindTextSlot(string slotId, string chineseLiteral, string englishLiteral)
    {
        OperationRuntimeSpec.ValidateId(slotId, nameof(slotId), required: true);
        if ((TextSlots ?? []).Any(slot => slot.Id == slotId))
            throw new InvalidOperationException($"Localized text slot {slotId} is already bound.");
        var token = $"[[{slotId}]]";
        var chinese = ReplaceSingleLiteral(ChineseTemplate, chineseLiteral, token,
            StringComparison.Ordinal, slotId, "Chinese");
        var english = EnglishTemplate is null ? null : ReplaceSingleLiteral(EnglishTemplate, englishLiteral,
            token, StringComparison.OrdinalIgnoreCase, slotId, "English");
        return this with
        {
            ChineseTemplate = chinese,
            EnglishTemplate = english,
            TextSlots = (TextSlots ?? []).Append(new OperationTextSlot(slotId, chineseLiteral, englishLiteral))
                .ToArray()
        };
    }

    public OperationLocalizedText WithTextSlotValue(string slotId, string chineseValue, string englishValue)
    {
        var slots = TextSlots?.ToArray() ?? [];
        var index = Array.FindIndex(slots, slot => slot.Id == slotId);
        if (index < 0) throw new InvalidOperationException($"Localized text slot {slotId} is not bound.");
        slots[index] = new OperationTextSlot(slotId, chineseValue, englishValue);
        return this with { TextSlots = slots };
    }

    /// <summary>
    /// Replaces one named value in an already style-normalized projection. This is used for SmartFormat dynamic
    /// variables: execution still reads RuntimeSpec, while presentation replaces the exact literal belonging to
    /// the same slot instead of guessing that the first localized number is behavioral.
    /// </summary>
    public bool TryReplaceRenderedSlot(string rendered, OperationRuntimeSpec spec, string slotId,
        string replacement, bool chinese, out string result)
    {
        var template = chinese ? ChineseTemplate : EnglishTemplate;
        if (template is null)
        {
            result = rendered;
            return false;
        }
        var placeholder = Placeholder.Matches(template).Cast<Match>()
            .SingleOrDefault(match => string.Equals(match.Groups["id"].Value, slotId,
                StringComparison.Ordinal));
        var slot = spec.Values.FirstOrDefault(candidate => candidate.Id == slotId);
        if (placeholder is null || slot is null)
        {
            result = rendered;
            return false;
        }
        var literal = Literal(slot, placeholder.Groups["format"].Value, chinese);
        var first = rendered.IndexOf(literal, StringComparison.Ordinal);
        if (first < 0 || rendered.IndexOf(literal, first + literal.Length, StringComparison.Ordinal) >= 0)
        {
            result = rendered;
            return false;
        }
        result = rendered[..first] + replacement + rendered[(first + literal.Length)..];
        return true;
    }

    /// <summary>
    /// Authoring/migration bridge. Production operations retain the compiled template, so later value changes never
    /// rescan localized prose. This compiler may eventually move entirely into the offline catalog exporter.
    /// </summary>
    public static bool TryCompile(string chinese, string? english, OperationRuntimeSpec spec,
        out OperationLocalizedText? localized)
    {
        if (!TryCompileLanguage(chinese, spec, english: false, out var chineseTemplate))
        {
            localized = null;
            return false;
        }
        string? englishTemplate = null;
        if (!string.IsNullOrWhiteSpace(english) && !HasAmbiguousRepeatedValues(spec))
        {
            if (!TryCompileLanguage(english!, spec, english: true, out var compiledEnglish))
                compiledEnglish = null;
            englishTemplate = compiledEnglish;
        }
        localized = new OperationLocalizedText(chineseTemplate, englishTemplate);
        localized.Validate(spec);
        return true;
    }

    private static bool HasAmbiguousRepeatedValues(OperationRuntimeSpec spec) => spec.Values
        .Where(IsPrintedSlot)
        .GroupBy(Literal, StringComparer.Ordinal)
        .Any(group => group.Key.Length > 0 && group.Count() > 1);

    private static bool TryCompileLanguage(string text, OperationRuntimeSpec spec, bool english, out string template)
    {
        template = text;
        var cursor = 0;
        foreach (var slot in spec.Values.Where(IsPrintedSlot))
        {
            var literal = Literal(slot);
            if (literal.Length == 0) continue;
            var index = template.IndexOf(literal, cursor, StringComparison.Ordinal);
            var format = string.Empty;
            if (index < 0)
            {
                // English modifiers can reverse two differently-valued clauses. Resolve an unambiguous literal
                // anywhere before declining the English template; Chinese authoring normally follows slot order.
                var first = template.IndexOf(literal, StringComparison.Ordinal);
                var second = first < 0 ? -1 : template.IndexOf(literal, first + literal.Length,
                    StringComparison.Ordinal);
                if (first >= 0 && second < 0)
                    index = first;
                else if (!english || !TryFindEnglishWord(template, slot.BaseValue + slot.Offset,
                             out index, out format))
                    return false;
            }
            var replacedLength = format switch
            {
                "cardinal" => Cardinal(slot.BaseValue + slot.Offset).Length,
                "ordinal" => Ordinal(slot.BaseValue + slot.Offset).Length,
                _ => literal.Length
            };
            var token = format.Length == 0 ? $"[[{slot.Id}]]" : $"[[{slot.Id}:{format}]]";
            template = template[..index] + token + template[(index + replacedLength)..];
            cursor = index + token.Length;
        }
        template = NormalizeResourcePlaceholder(template, english);
        return true;
    }

    private string Render(string template, OperationRuntimeSpec spec,
        IReadOnlyDictionary<string, string>? replacements, bool chinese) => Placeholder.Replace(template, match =>
    {
        var id = match.Groups["id"].Value;
        if (replacements?.TryGetValue(id, out var replacement) == true) return replacement;
        var valueSlot = spec.Values.FirstOrDefault(candidate => candidate.Id == id);
        if (valueSlot is not null) return Literal(valueSlot, match.Groups["format"].Value, chinese);
        var textSlot = TextSlots?.FirstOrDefault(candidate => candidate.Id == id)
            ?? throw new InvalidOperationException($"Localized template references missing slot {id}.");
        return chinese ? textSlot.ChineseValue : textSlot.EnglishValue;
    });

    private void ValidateTemplate(string template, OperationRuntimeSpec spec, string language)
    {
        var slots = Slots(template);
        if (slots.Count != slots.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidOperationException($"{language} operation template repeats a named slot.");
        foreach (var id in slots)
        {
            var slot = spec.Values.FirstOrDefault(candidate => candidate.Id == id);
            if (slot is not null && !IsPrintedSlot(slot))
                throw new InvalidOperationException($"{language} operation template exposes hidden slot {id}.");
            if (slot is null && (TextSlots ?? []).All(candidate => candidate.Id != id))
                throw new InvalidOperationException($"{language} operation template references missing slot {id}.");
        }
        var expected = spec.Values.Where(IsPrintedSlot).Select(slot => slot.Id)
            .Concat((TextSlots ?? []).Select(slot => slot.Id)).ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(slots))
            throw new InvalidOperationException($"{language} operation template does not cover every printed slot. "
                                                + $"Expected={string.Join(',', expected)}; actual={string.Join(',', slots)}.");
    }

    private static string ReplaceSingleLiteral(string template, string literal, string replacement,
        StringComparison comparison, string slotId, string language)
    {
        if (string.IsNullOrEmpty(literal))
            throw new InvalidOperationException($"{language} localized text slot {slotId} has an empty literal.");
        var first = template.IndexOf(literal, comparison);
        var second = first < 0 ? -1 : template.IndexOf(literal, first + literal.Length, comparison);
        if (first < 0 || second >= 0)
            throw new InvalidOperationException($"{language} localized text slot {slotId} must match exactly once.");
        return template[..first] + replacement + template[(first + literal.Length)..];
    }

    private static IReadOnlyList<string> Slots(string template) => Placeholder.Matches(template)
        .Select(match => match.Groups["id"].Value).ToArray();

    private static bool IsPrintedSlot(RuntimeValueSlot slot) => slot.Explicit
        && !slot.Id.EndsWith("_marker", StringComparison.Ordinal)
        && (slot.Source == "fixed" || slot.Source is "energy_x" or "star_x" or "special_x");

    private static string Literal(RuntimeValueSlot slot)
    {
        if (slot.Source == "fixed")
            return Math.Max(0, slot.BaseValue + slot.Offset).ToString(CultureInfo.InvariantCulture);
        if (slot.Source is not ("energy_x" or "star_x" or "special_x")) return string.Empty;
        return slot.Offset switch
        {
            > 0 => $"X+{slot.Offset}",
            < 0 => $"X{slot.Offset}",
            _ => "X"
        };
    }

    private static string Literal(RuntimeValueSlot slot, string format, bool chinese) => format switch
    {
        "cardinal" => Cardinal(slot.BaseValue + slot.Offset),
        "ordinal" => Ordinal(slot.BaseValue + slot.Offset),
        "energy" => chinese ? Literal(slot) + "点能量" : Literal(slot) + " Energy",
        "stars" => chinese
            ? Literal(slot) + "颗蓝星"
            : Literal(slot) + (slot.BaseValue + slot.Offset == 1 ? " Star" : " Stars"),
        "" => Literal(slot),
        _ => throw new InvalidOperationException($"Unsupported localized slot format {format}.")
    };

    private static string NormalizeResourcePlaceholder(string template, bool english)
    {
        if (english)
        {
            template = Regex.Replace(template, @"\[\[(?<id>[a-z0-9_]+)\]\] Energy\b",
                "[[$1:energy]]", RegexOptions.CultureInvariant);
            return Regex.Replace(template, @"\[\[(?<id>[a-z0-9_]+)\]\] Stars?\b",
                "[[$1:stars]]", RegexOptions.CultureInvariant);
        }
        template = Regex.Replace(template, @"\[\[(?<id>[a-z0-9_]+)\]\]点能量",
            "[[$1:energy]]", RegexOptions.CultureInvariant);
        return Regex.Replace(template, @"\[\[(?<id>[a-z0-9_]+)\]\]颗蓝星",
            "[[$1:stars]]", RegexOptions.CultureInvariant);
    }

    private static bool TryFindEnglishWord(string text, int value, out int index, out string format)
    {
        foreach (var candidate in new[] { (Text: Cardinal(value), Format: "cardinal"),
                     (Text: Ordinal(value), Format: "ordinal") })
        {
            var matches = Regex.Matches(text, $@"\b{Regex.Escape(candidate.Text)}\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (matches.Count != 1) continue;
            index = matches[0].Index;
            format = candidate.Format;
            return true;
        }
        index = -1;
        format = string.Empty;
        return false;
    }

    private static string Cardinal(int value) => value >= 0 && value < CardinalWords.Length
        ? CardinalWords[value]
        : value.ToString(CultureInfo.InvariantCulture);

    private static string Ordinal(int value)
    {
        if (value >= 0 && value < OrdinalWords.Length) return OrdinalWords[value];
        var suffix = value % 100 is 11 or 12 or 13 ? "th" : (value % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th"
        };
        return value.ToString(CultureInfo.InvariantCulture) + suffix;
    }
}

/// <summary>Stable localization-ID registry for external components.</summary>
public static class ComponentLocalizationApi
{
    public const int ApiVersion = 2;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, OperationLocalizedText> Values = new(StringComparer.Ordinal);

    public static void Register(string localizationId, OperationLocalizedText localizedText,
        OperationRuntimeSpec runtimeSpec)
    {
        ValidateId(localizationId);
        ArgumentNullException.ThrowIfNull(localizedText);
        ArgumentNullException.ThrowIfNull(runtimeSpec);
        localizedText.Validate(runtimeSpec);
        lock (Sync)
        {
            if (ComponentApi.RegistrationsFrozen)
                throw new InvalidOperationException(
                    "Localization registration must finish before the first generation profile resolves.");
            RegisterCore(localizationId, localizedText);
        }
    }

    internal static void EnsureCanRegister(
        IEnumerable<(string Id, OperationLocalizedText Text, OperationRuntimeSpec RuntimeSpec)> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var values = registrations.ToArray();
        lock (Sync)
        {
            if (ComponentApi.RegistrationsFrozen)
                throw new InvalidOperationException(
                    "Localization registration must finish before the first generation profile resolves.");
            foreach (var value in values)
            {
                ValidateId(value.Id);
                value.Text.Validate(value.RuntimeSpec);
                if (Values.TryGetValue(value.Id, out var existing) && existing != value.Text)
                    throw new InvalidOperationException(
                        $"Localization '{value.Id}' is already registered with different templates.");
            }
        }
    }

    /// <summary>Built-in catalogs are materialized lazily while ComponentApi is resolving and already frozen.</summary>
    internal static void RegisterBuiltIn(string localizationId, OperationLocalizedText localizedText,
        OperationRuntimeSpec runtimeSpec)
    {
        ValidateId(localizationId);
        ArgumentNullException.ThrowIfNull(localizedText);
        ArgumentNullException.ThrowIfNull(runtimeSpec);
        localizedText.Validate(runtimeSpec);
        lock (Sync) RegisterCore(localizationId, localizedText);
    }

    public static bool TryGet(string? localizationId, out OperationLocalizedText localizedText)
    {
        if (localizationId is null)
        {
            localizedText = null!;
            return false;
        }
        lock (Sync)
            if (Values.TryGetValue(localizationId, out localizedText!)) return true;
        EnsureBuiltInCatalogLoaded(localizationId);
        lock (Sync) return Values.TryGetValue(localizationId, out localizedText!);
    }

    private static void ValidateId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => character > 0x7f))
            throw new ArgumentException("Localization IDs must be non-empty ASCII strings.", nameof(value));
    }

    private static void RegisterCore(string localizationId, OperationLocalizedText localizedText)
    {
        if (Values.TryGetValue(localizationId, out var existing))
        {
            if (existing != localizedText)
                throw new InvalidOperationException(
                    $"Localization '{localizationId}' is already registered with different templates.");
            return;
        }
        Values.Add(localizationId, localizedText);
    }

    private static void EnsureBuiltInCatalogLoaded(string localizationId)
    {
        var separator = localizationId.IndexOf('/');
        if (separator <= 0) return;
        var prefix = localizationId[..separator];
        if (!Enum.TryParse<GeneratedCharacter>(prefix, ignoreCase: true, out var character)) return;
        _ = StructuredComponentCatalogRegistry.Get(character);
    }
}
