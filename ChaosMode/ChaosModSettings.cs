using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace AutoAnthony;

internal static class ChaosModSettings
{
    private sealed class SettingsData
    {
        public int Schema { get; set; } = 16;
        public bool Enabled { get; set; } = true;
        public bool AddGeneratedCards { get; set; } = true;
        public bool ReplaceStartingCards { get; set; } = true;
        public bool UltimateChaos { get; set; }
        public bool NumericBalanceOptimization { get; set; }
        public bool NumericRandomMode { get; set; }
        public bool PreserveOriginalCards { get; set; }
        public bool DecomposeOriginalCards { get; set; }
        public bool RandomCardArt { get; set; }
        public bool AnytimeCardEditing { get; set; }
        public bool ShowGenerationModeHoverTips { get; set; } = true;
        public bool ShowCardInternalIds { get; set; }
        public bool SurpriseMode { get; set; }
        public bool SurpriseModeLite { get; set; }
        public bool SurpriseModePro { get; set; }
        public Dictionary<int, string> HistoryOptimizationSignatures { get; set; } = [];
    }

    private static readonly string SettingsPath = Path.Combine(
        OS.GetUserDataDir(), "AutoAnthony", "settings.json");
    private static bool _loaded;
    private static bool _enabled = true;
    private static bool _addGeneratedCards = true;
    private static bool _replaceStartingCards = true;
    private static bool _ultimateChaos;
    private static bool _numericBalanceOptimization;
    private static bool _numericRandomMode;
    private static bool _preserveOriginalCards;
    private static bool _decomposeOriginalCards;
    private static bool _randomCardArt;
    private static bool _anytimeCardEditing;
    private static bool _showGenerationModeHoverTips = true;
    private static bool _showCardInternalIds;
    private static bool _surpriseMode;
    private static bool _surpriseModeLite;
    private static bool _surpriseModePro;
    private static Dictionary<int, string> _historyOptimizationSignatures = [];
    [ThreadStatic] private static bool? _generationOverride;
    [ThreadStatic] private static bool? _numericBalanceOverride;
    [ThreadStatic] private static bool? _numericRandomOverride;

    /// <summary>The persisted preference, unless an internal audit/save migration explicitly pins a mode.</summary>
    internal static bool EffectiveUltimateChaos => _generationOverride ?? UltimateChaos;
    internal static bool EffectiveNumericBalanceOptimization =>
        _numericBalanceOverride ?? NumericBalanceOptimization;
    internal static bool EffectiveNumericRandomMode => _numericRandomOverride ?? NumericRandomMode;
    internal static bool EffectiveGeneratedCardsEnabled => Enabled && AddGeneratedCards;

    internal static bool Enabled
    {
        get
        {
            EnsureLoaded();
            return _enabled;
        }
        set
        {
            EnsureLoaded();
            if (_enabled == value) return;
            _enabled = value;
            Save();
            Log.Info($"[AutoAnthony] Mod effects {(value ? "enabled" : "disabled")}; the change applies to new runs. Saved generated runs and run history remain readable.");
        }
    }

    internal static bool UltimateChaos
    {
        get
        {
            EnsureLoaded();
            return _ultimateChaos;
        }
        set
        {
            EnsureLoaded();
            if (_ultimateChaos == value) return;
            _ultimateChaos = value;
            Save();
            Log.Info($"[AutoAnthony] Ultimate Chaos {(value ? "enabled" : "disabled")}; the change applies to newly generated runs.");
        }
    }

    internal static bool NumericBalanceOptimization
    {
        get
        {
            EnsureLoaded();
            return _numericBalanceOptimization;
        }
        set
        {
            EnsureLoaded();
            if (_numericBalanceOptimization == value) return;
            _numericBalanceOptimization = value;
            Save();
            Log.Info($"[AutoAnthony] Numeric Balance Optimization {(value ? "enabled" : "disabled")}; the change applies to newly generated runs.");
        }
    }

    internal static bool NumericRandomMode
    {
        get { EnsureLoaded(); return _numericRandomMode; }
        set
        {
            EnsureLoaded();
            if (_numericRandomMode == value) return;
            _numericRandomMode = value;
            Save();
            Log.Info($"[AutoAnthony] Numeric Random Mode {(value ? "enabled" : "disabled")}; the change applies to newly generated runs.");
        }
    }

    internal static bool PreserveOriginalCards
    {
        get { EnsureLoaded(); return _preserveOriginalCards; }
        set
        {
            EnsureLoaded();
            var normalized = value || !_addGeneratedCards;
            if (_preserveOriginalCards == normalized) return;
            _preserveOriginalCards = normalized;
            Save();
            Log.Info($"[AutoAnthony] Original cards {(normalized ? "enabled" : "disabled")}; the change applies to newly generated runs.");
        }
    }

    internal static bool DecomposeOriginalCards
    {
        get { EnsureLoaded(); return _decomposeOriginalCards; }
        set
        {
            EnsureLoaded();
            if (_decomposeOriginalCards == value) return;
            _decomposeOriginalCards = value;
            Save();
            Log.Info($"[AutoAnthony] Original-card component descriptions {(value ? "enabled" : "disabled")}.");
        }
    }

    internal static bool AddGeneratedCards
    {
        get { EnsureLoaded(); return _addGeneratedCards; }
        set
        {
            EnsureLoaded();
            if (_addGeneratedCards == value) return;
            _addGeneratedCards = value;
            if (!value)
            {
                // A run must contain at least one of the generated and original pools. Without generated cards,
                // the complete vanilla pool/starting deck/relic behavior is authoritative.
                _preserveOriginalCards = true;
                _replaceStartingCards = false;
            }
            Save();
            Log.Info($"[AutoAnthony] Generated cards {(value ? "enabled" : "disabled")}; the change applies to newly generated runs.");
        }
    }

    internal static bool RandomCardArt
    {
        get { EnsureLoaded(); return _randomCardArt; }
        set
        {
            EnsureLoaded();
            if (_randomCardArt == value) return;
            _randomCardArt = value;
            Save();
            Log.Info($"[AutoAnthony] Random Card Art {(value ? "enabled" : "disabled")}; "
                     + "the change applies to newly generated runs.");
        }
    }

    internal static bool AnytimeCardEditing
    {
        get { EnsureLoaded(); return _anytimeCardEditing; }
        set
        {
            EnsureLoaded();
            if (_anytimeCardEditing == value) return;
            _anytimeCardEditing = value;
            Save();
            Log.Info($"[AutoAnthony] Anytime card editing {(value ? "enabled" : "disabled")}; "
                     + "Card Tinkering applies the change immediately outside combat.");
        }
    }

    internal static bool ReplaceStartingCards
    {
        get
        {
            EnsureLoaded();
            return _replaceStartingCards;
        }
        set
        {
            EnsureLoaded();
            var normalized = value && _addGeneratedCards;
            if (_replaceStartingCards == normalized) return;
            _replaceStartingCards = normalized;
            Save();
            Log.Info($"[AutoAnthony] Starting-card replacement {(normalized ? "enabled" : "disabled")}; the change applies to newly generated runs.");
        }
    }

    internal static bool ShowCardInternalIds
    {
        get
        {
            EnsureLoaded();
            return _showCardInternalIds;
        }
        set
        {
            EnsureLoaded();
            if (_showCardInternalIds == value) return;
            _showCardInternalIds = value;
            Save();
            Log.Info($"[AutoAnthony] Card internal-ID hover tips {(value ? "enabled" : "disabled")}.");
        }
    }

    internal static bool ShowGenerationModeHoverTips
    {
        get
        {
            EnsureLoaded();
            return _showGenerationModeHoverTips;
        }
        set
        {
            EnsureLoaded();
            if (_showGenerationModeHoverTips == value) return;
            _showGenerationModeHoverTips = value;
            Save();
            Log.Info($"[AutoAnthony] Generated-card mode hover tips {(value ? "enabled" : "disabled")}.");
        }
    }

    internal static bool SurpriseMode
    {
        get
        {
            EnsureLoaded();
            return _surpriseMode;
        }
        set
        {
            EnsureLoaded();
            if (_surpriseMode == value && (!value || !_surpriseModeLite && !_surpriseModePro)) return;
            _surpriseMode = value;
            if (value)
            {
                _surpriseModeLite = false;
                _surpriseModePro = false;
            }
            Save();
            Log.Info($"[AutoAnthony] Surprise Mode {(value ? "enabled" : "disabled")}; the change applies immediately.");
        }
    }

    internal static bool SurpriseModeLite
    {
        get
        {
            EnsureLoaded();
            return _surpriseModeLite;
        }
        set
        {
            EnsureLoaded();
            if (_surpriseModeLite == value && (!value || !_surpriseMode && !_surpriseModePro)) return;
            _surpriseModeLite = value;
            if (value)
            {
                _surpriseMode = false;
                _surpriseModePro = false;
            }
            Save();
            Log.Info($"[AutoAnthony] Surprise Mode Lite {(value ? "enabled" : "disabled")}; the change applies immediately.");
        }
    }

    internal static bool SurpriseModePro
    {
        get
        {
            EnsureLoaded();
            return _surpriseModePro;
        }
        set
        {
            EnsureLoaded();
            if (_surpriseModePro == value && (!value || !_surpriseMode && !_surpriseModeLite)) return;
            _surpriseModePro = value;
            if (value)
            {
                _surpriseMode = false;
                _surpriseModeLite = false;
            }
            Save();
            Log.Info($"[AutoAnthony] Surprise Mode Pro {(value ? "enabled" : "disabled")}; the change applies immediately.");
        }
    }

    internal static bool AnySurpriseMode => SurpriseMode || SurpriseModeLite || SurpriseModePro;

    internal static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var json = File.ReadAllText(SettingsPath);
            _enabled = ParseEnabled(json);
            _addGeneratedCards = ParseBooleanSetting(json, true,
                "AddGeneratedCards", "add_generated_cards", "addAutoAnthonyCards");
            _replaceStartingCards = ParseReplaceStartingCards(json);
            _ultimateChaos = ParseUltimateChaos(json);
            _numericBalanceOptimization = ParseNumericBalanceOptimization(json);
            _numericRandomMode = ParseBooleanSetting(json, false, "NumericRandomMode", "numeric_random_mode");
            _preserveOriginalCards = ParseBooleanSetting(json, false, "PreserveOriginalCards", "preserve_original_cards");
            _decomposeOriginalCards = ParseBooleanSetting(json, false,
                "DecomposeOriginalCards", "decompose_original_cards");
            _randomCardArt = ParseBooleanSetting(json, false, "RandomCardArt", "random_card_art");
            _anytimeCardEditing = ParseBooleanSetting(json, false,
                "AnytimeCardEditing", "anytime_card_editing");
            _showGenerationModeHoverTips = ParseBooleanSetting(json, true,
                "ShowGenerationModeHoverTips", "show_generation_mode_hover_tips");
            _showCardInternalIds = ParseShowCardInternalIds(json);
            _surpriseMode = ParseSurpriseMode(json);
            _surpriseModeLite = ParseSurpriseModeLite(json);
            _surpriseModePro = ParseSurpriseModePro(json);
            _historyOptimizationSignatures = ParseHistoryOptimizationSignatures(json);
            NormalizeSurpriseModes(ref _surpriseMode, ref _surpriseModeLite, ref _surpriseModePro);
            NormalizePoolSettings(ref _addGeneratedCards, ref _preserveOriginalCards,
                ref _replaceStartingCards);
        }
        catch (Exception exception)
        {
            _enabled = true;
            _addGeneratedCards = true;
            _replaceStartingCards = true;
            _ultimateChaos = false;
            _numericBalanceOptimization = false;
            _numericRandomMode = false;
            _preserveOriginalCards = false;
            _decomposeOriginalCards = false;
            _randomCardArt = false;
            _anytimeCardEditing = false;
            _showGenerationModeHoverTips = true;
            _showCardInternalIds = false;
            _surpriseMode = false;
            _surpriseModeLite = false;
            _surpriseModePro = false;
            _historyOptimizationSignatures = [];
            Log.Error($"[AutoAnthony] Failed to load settings; using defaults: {exception}");
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(
                new SettingsData
                {
                    Enabled = _enabled,
                    AddGeneratedCards = _addGeneratedCards,
                    ReplaceStartingCards = _replaceStartingCards,
                    UltimateChaos = _ultimateChaos,
                    NumericBalanceOptimization = _numericBalanceOptimization,
                    NumericRandomMode = _numericRandomMode,
                    PreserveOriginalCards = _preserveOriginalCards,
                    DecomposeOriginalCards = _decomposeOriginalCards,
                    RandomCardArt = _randomCardArt,
                    AnytimeCardEditing = _anytimeCardEditing,
                    ShowGenerationModeHoverTips = _showGenerationModeHoverTips,
                    ShowCardInternalIds = _showCardInternalIds,
                    SurpriseMode = _surpriseMode,
                    SurpriseModeLite = _surpriseModeLite,
                    SurpriseModePro = _surpriseModePro,
                    HistoryOptimizationSignatures = new Dictionary<int, string>(_historyOptimizationSignatures)
                },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            Log.Error($"[AutoAnthony] Failed to save settings: {exception}");
        }
    }

    internal static IDisposable OverrideGenerationMode(bool ultimateChaos,
        bool? numericBalanceOptimization = null, bool? numericRandomMode = null)
    {
        var previous = _generationOverride;
        var previousNumeric = _numericBalanceOverride;
        var previousRandom = _numericRandomOverride;
        _generationOverride = ultimateChaos;
        if (numericBalanceOptimization.HasValue)
            _numericBalanceOverride = numericBalanceOptimization.Value;
        if (numericRandomMode.HasValue)
            _numericRandomOverride = numericRandomMode.Value;
        return new GenerationOverrideScope(previous, previousNumeric, previousRandom);
    }

    private static bool ParseBooleanSetting(string json, bool defaultValue, params string[] names)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return defaultValue;
        foreach (var property in root.EnumerateObject())
            if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return TryReadBoolean(property.Value, out var value) ? value : defaultValue;
        return defaultValue;
    }

    private static bool ParseEnabled(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return true;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals("Enabled", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Equals("mod_enabled", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Equals("enableModEffects", StringComparison.OrdinalIgnoreCase))
                continue;
            return TryReadBoolean(property.Value, out var value) ? value : true;
        }
        // Existing settings files predate the switch and must preserve the previous enabled behavior.
        return true;
    }

    private static bool ParseUltimateChaos(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (TryReadBoolean(root, out var direct)) return direct;
        if (root.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals("UltimateChaos", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Equals("ultimate_chaos", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Equals("unlockComponentRoles", StringComparison.OrdinalIgnoreCase))
                continue;
            return TryReadBoolean(property.Value, out var value) && value;
        }
        return false;
    }

    private static bool ParseNumericBalanceOptimization(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals("NumericBalanceOptimization", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Equals("numeric_balance_optimization", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Equals("balancedValues", StringComparison.OrdinalIgnoreCase))
                continue;
            return TryReadBoolean(property.Value, out var value) && value;
        }
        // Aggressive values are deliberately the user-facing default. Existing settings files therefore leave
        // this option disabled; save snapshots separately preserve the legacy balanced generation mode.
        return false;
    }

    private static bool ParseReplaceStartingCards(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return true;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals("ReplaceStartingCards", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Equals("replace_starting_cards", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Equals("replaceBasicCards", StringComparison.OrdinalIgnoreCase))
                continue;
            return TryReadBoolean(property.Value, out var value) ? value : true;
        }
        // Existing settings files used the original behavior, which replaced both Basic cards and the deck.
        return true;
    }

    private static bool ParseShowCardInternalIds(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals("ShowCardInternalIds", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Equals("show_card_internal_ids", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Equals("showCardIds", StringComparison.OrdinalIgnoreCase))
                continue;
            return TryReadBoolean(property.Value, out var value) && value;
        }
        return false;
    }

    private static bool ParseSurpriseMode(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals("SurpriseMode", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Equals("surprise_mode", StringComparison.OrdinalIgnoreCase))
                continue;
            return TryReadBoolean(property.Value, out var value) && value;
        }
        return false;
    }

    private static bool ParseSurpriseModeLite(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals("SurpriseModeLite", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Equals("surprise_mode_lite", StringComparison.OrdinalIgnoreCase))
                continue;
            return TryReadBoolean(property.Value, out var value) && value;
        }
        return false;
    }

    private static bool ParseSurpriseModePro(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals("SurpriseModePro", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Equals("surprise_mode_pro", StringComparison.OrdinalIgnoreCase))
                continue;
            return TryReadBoolean(property.Value, out var value) && value;
        }
        return false;
    }

    private static Dictionary<int, string> ParseHistoryOptimizationSignatures(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return [];
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals("HistoryOptimizationSignatures", StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind != JsonValueKind.Object) continue;
            var result = new Dictionary<int, string>();
            foreach (var entry in property.Value.EnumerateObject())
                if (int.TryParse(entry.Name, out var profileId)
                    && entry.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(entry.Value.GetString()))
                    result[profileId] = entry.Value.GetString()!;
            return result;
        }
        return [];
    }

    internal static bool HasHistoryOptimizationRecord(int profileId)
    {
        EnsureLoaded();
        return _historyOptimizationSignatures.ContainsKey(profileId);
    }

    internal static bool IsHistoryOptimizationCurrent(int profileId, string signature)
    {
        EnsureLoaded();
        return _historyOptimizationSignatures.TryGetValue(profileId, out var saved)
               && string.Equals(saved, signature, StringComparison.Ordinal);
    }

    internal static void MarkHistoryOptimizationCurrent(int profileId, string signature)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(signature)) return;
        if (_historyOptimizationSignatures.TryGetValue(profileId, out var saved)
            && string.Equals(saved, signature, StringComparison.Ordinal)) return;
        _historyOptimizationSignatures[profileId] = signature;
        Save();
    }

    private static void NormalizeSurpriseModes(ref bool full, ref bool lite, ref bool pro)
    {
        // A manually edited or future/legacy settings file may contain more than one. Preserve the legacy full
        // mode first, then Lite, so upgrading an existing settings file is deterministic.
        if (full)
        {
            lite = false;
            pro = false;
        }
        else if (lite)
            pro = false;
    }

    private static bool TryReadBoolean(JsonElement element, out bool value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.True: value = true; return true;
            case JsonValueKind.False: value = false; return true;
            case JsonValueKind.Number when element.TryGetInt32(out var number): value = number != 0; return true;
            case JsonValueKind.String when bool.TryParse(element.GetString(), out value): return true;
            default: value = false; return false;
        }
    }

    private static void NormalizePoolSettings(ref bool addGeneratedCards, ref bool preserveOriginalCards,
        ref bool replaceStartingCards)
    {
        if (addGeneratedCards) return;
        preserveOriginalCards = true;
        replaceStartingCards = false;
    }

    internal static void AuditCompatibility()
    {
        if (!ParseEnabled("{\"Enabled\":true}")
            || ParseEnabled("{\"mod_enabled\":0}")
            || !ParseEnabled("{\"Schema\":5,\"UltimateChaos\":true}"))
            throw new InvalidOperationException("Global enabled-setting compatibility audit failed.");
        if (!ParseUltimateChaos("{\"UltimateChaos\":true}")
            || !ParseUltimateChaos("{\"ultimateChaos\":true}")
            || !ParseUltimateChaos("{\"ultimate_chaos\":1}")
            || !ParseUltimateChaos("{\"unlockComponentRoles\":\"true\"}")
            || ParseUltimateChaos("{\"Schema\":1}"))
            throw new InvalidOperationException("Ultimate Chaos legacy settings migration audit failed.");
        if (!ParseNumericBalanceOptimization("{\"NumericBalanceOptimization\":true}")
            || !ParseNumericBalanceOptimization("{\"numeric_balance_optimization\":1}")
            || !ParseNumericBalanceOptimization("{\"balancedValues\":\"true\"}")
            || ParseNumericBalanceOptimization("{\"Schema\":9,\"UltimateChaos\":true}"))
            throw new InvalidOperationException("Numeric Balance Optimization setting compatibility audit failed.");
        if (!ParseBooleanSetting("{\"NumericRandomMode\":true}", false, "NumericRandomMode")
            || ParseBooleanSetting("{\"Schema\":10}", false, "NumericRandomMode")
            || !ParseBooleanSetting("{\"Schema\":14}", true, "AddGeneratedCards")
            || ParseBooleanSetting("{\"add_generated_cards\":0}", true,
                "AddGeneratedCards", "add_generated_cards")
            || !ParseBooleanSetting("{\"preserve_original_cards\":1}", false, "PreserveOriginalCards", "preserve_original_cards")
            || !ParseBooleanSetting("{\"decompose_original_cards\":1}", false,
                "DecomposeOriginalCards", "decompose_original_cards")
            || !ParseBooleanSetting("{\"random_card_art\":1}", false, "RandomCardArt", "random_card_art")
            || !ParseBooleanSetting("{\"anytime_card_editing\":1}", false,
                "AnytimeCardEditing", "anytime_card_editing")
            || ParseBooleanSetting("{\"Schema\":13}", false, "AnytimeCardEditing", "anytime_card_editing")
            || !ParseBooleanSetting("{\"Schema\":12}", true, "ShowGenerationModeHoverTips")
            || ParseBooleanSetting("{\"show_generation_mode_hover_tips\":false}", true,
                "ShowGenerationModeHoverTips", "show_generation_mode_hover_tips"))
            throw new InvalidOperationException("New generation-setting compatibility audit failed.");
        var addGenerated = false;
        var addOriginal = false;
        var replaceStarting = true;
        NormalizePoolSettings(ref addGenerated, ref addOriginal, ref replaceStarting);
        if (addGenerated || !addOriginal || replaceStarting)
            throw new InvalidOperationException("Card-pool setting dependency normalization failed.");
        if (!ParseReplaceStartingCards("{\"ReplaceStartingCards\":true}")
            || ParseReplaceStartingCards("{\"replace_starting_cards\":0}")
            || !ParseReplaceStartingCards("{\"Schema\":7,\"UltimateChaos\":true}"))
            throw new InvalidOperationException("Starting-card replacement setting compatibility audit failed.");
        if (!ParseShowCardInternalIds("{\"ShowCardInternalIds\":true}")
            || !ParseShowCardInternalIds("{\"show_card_internal_ids\":1}")
            || !ParseShowCardInternalIds("{\"showCardIds\":\"true\"}")
            || ParseShowCardInternalIds("{\"Schema\":2,\"UltimateChaos\":true}"))
            throw new InvalidOperationException("Card internal-ID setting compatibility audit failed.");
        if (!ParseSurpriseMode("{\"SurpriseMode\":true}")
            || !ParseSurpriseMode("{\"surprise_mode\":1}")
            || ParseSurpriseMode("{\"Schema\":3,\"UltimateChaos\":true}"))
            throw new InvalidOperationException("Surprise Mode setting compatibility audit failed.");
        if (!ParseSurpriseModeLite("{\"SurpriseModeLite\":true}")
            || !ParseSurpriseModeLite("{\"surprise_mode_lite\":1}")
            || ParseSurpriseModeLite("{\"Schema\":4,\"SurpriseMode\":true}"))
            throw new InvalidOperationException("Surprise Mode Lite setting compatibility audit failed.");
        if (!ParseSurpriseModePro("{\"SurpriseModePro\":true}")
            || !ParseSurpriseModePro("{\"surprise_mode_pro\":1}")
            || ParseSurpriseModePro("{\"Schema\":8,\"SurpriseMode\":true}"))
            throw new InvalidOperationException("Surprise Mode Pro setting compatibility audit failed.");
        var historySignatures = ParseHistoryOptimizationSignatures(
            "{\"HistoryOptimizationSignatures\":{\"1\":\"ABC\",\"bad\":\"ignored\"}}");
        if (historySignatures.Count != 1 || historySignatures.GetValueOrDefault(1) != "ABC")
            throw new InvalidOperationException("History optimization signature compatibility audit failed.");
        var full = true;
        var lite = true;
        var pro = true;
        NormalizeSurpriseModes(ref full, ref lite, ref pro);
        if (!full || lite || pro)
            throw new InvalidOperationException("Mutually exclusive Surprise Mode settings normalization failed.");

        var persisted = UltimateChaos;
        var persistedNumeric = NumericBalanceOptimization;
        var persistedRandom = NumericRandomMode;
        using (OverrideGenerationMode(!persisted, !persistedNumeric, !persistedRandom))
        {
            if (EffectiveUltimateChaos == persisted)
                throw new InvalidOperationException("Ultimate Chaos generation-mode override audit failed.");
            using (OverrideGenerationMode(persisted))
                if (EffectiveUltimateChaos != persisted)
                    throw new InvalidOperationException("Nested generation-mode override audit failed.");
            if (EffectiveUltimateChaos == persisted)
                throw new InvalidOperationException("Nested generation-mode override restoration audit failed.");
            if (EffectiveNumericBalanceOptimization == persistedNumeric)
                throw new InvalidOperationException("Numeric balance generation-mode override audit failed.");
            if (EffectiveNumericRandomMode == persistedRandom)
                throw new InvalidOperationException("Numeric random generation-mode override audit failed.");
        }
        if (EffectiveUltimateChaos != persisted)
            throw new InvalidOperationException("Generation-mode override changed the persisted setting.");
        if (EffectiveNumericBalanceOptimization != persistedNumeric)
            throw new InvalidOperationException("Numeric balance override changed the persisted setting.");
        if (EffectiveNumericRandomMode != persistedRandom)
            throw new InvalidOperationException("Numeric random override changed the persisted setting.");
    }

    private sealed class GenerationOverrideScope(bool? previous, bool? previousNumeric, bool? previousRandom) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _generationOverride = previous;
            _numericBalanceOverride = previousNumeric;
            _numericRandomOverride = previousRandom;
        }
    }
}
