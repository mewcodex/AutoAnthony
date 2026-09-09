namespace ChaosCardGenerator;

/// <summary>
/// Stable component-level metadata shared by generation, validation and future external catalogs. “SinglePerCard”
/// is the existing non-stacking rule: a second copy is rejected only inside the same card. “UniquePerPool” is
/// stronger: after one finalized card in a character/colorless pool consumes the component, later cards cannot
/// select it. Failed speculative assemblies never consume pool uniqueness.
/// </summary>
public static class ComponentPolicy
{
    private static readonly object RegistrationLock = new();
    private static readonly Dictionary<(string Template, string Variant), ComponentMultiplicity> Overrides = new();
    private static bool _registrationsFrozen;

    // Pool uniqueness is semantic rather than template-wide. Several operations share one interpreter template
    // (notably N:Exhaust and T:Apply), so matching only the template would incorrectly remove ordinary Exhaust or
    // Vulnerable components after an unrelated whole-zone/rule component had been used once.
    private static readonly HashSet<string> BuiltInPoolUniqueKeys = new(StringComparer.Ordinal)
    {
        "rule:retain_block_between_turns",
        "rule:skills_cost_zero",
        "rule:skills_exhaust",
        "rule:derivative_hits_all",
        "rule:derivative_retain",
        "rule:played_skills_gain_sly",
        "rule:retain_hand_at_turn_end",
        "rule:kings_sword_hits_all",
        "rule:buffer",
        "rule:die_on_unblocked_attack",
        "rule:no_block_from_cards",
        "turn:free_hand",
        "condition:exhaust_pile_minimum",
        "damage:cards_played_combat",
        "transform:all_hand_attacks",
        "clear:all_non_attack_hand_exhaust",
        "clear:all_hand_exhaust",
        "target:remove_all_block_and_artifact",
        "value:block_equal_all_poison",
        "action:play_selected_skill_multiple_times",
        "target:vulnerable_double",
        "target:vulnerable_weak_double",
        "target:copy_debuffs_to_others",
        "action:draw_discard_nonzero",
        "action:shuffle_unexhausted",
        "reward:gain_gold"
    };

    /// <summary>
    /// External catalogs may register multiplicity by ASCII template and optional RuntimeSpec variant before pool
    /// generation begins. An exact template+variant registration wins over a template-wide registration.
    /// </summary>
    public static void RegisterMultiplicity(string template, ComponentMultiplicity multiplicity,
        string variant = "")
    {
        if (string.IsNullOrWhiteSpace(template) || template.Any(value => value > 0x7f))
            throw new ArgumentException("Component template IDs must be non-empty ASCII.", nameof(template));
        if (variant.Any(value => value > 0x7f))
            throw new ArgumentException("Component variant IDs must be ASCII.", nameof(variant));
        lock (RegistrationLock)
        {
            if (_registrationsFrozen)
                throw new InvalidOperationException(
                    "Component multiplicity registration must finish before the first generation profile resolves.");
            Overrides[(template, variant)] = multiplicity;
        }
    }

    internal static void EnsureCanRegister(IEnumerable<ComponentMultiplicityRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var values = registrations.ToArray();
        lock (RegistrationLock)
        {
            if (_registrationsFrozen)
                throw new InvalidOperationException(
                    "Component multiplicity registration must finish before the first generation profile resolves.");
            var conflict = values.FirstOrDefault(value => Overrides.ContainsKey((value.Template, value.Variant)));
            if (conflict is not null)
                throw new InvalidOperationException(
                    $"A multiplicity is already registered for '{conflict.Template}'/'{conflict.Variant}'.");
        }
    }

    internal static void FreezeRegistrations()
    {
        lock (RegistrationLock) _registrationsFrozen = true;
    }

    public static ComponentMultiplicity Multiplicity(ComponentAtom atom) =>
        Resolve(atom.Template, OperationRuntimeSpecCompiler.GetOrCompile(atom),
            CardEffectRules.CardUniqueEffectKey(atom), atom.SemanticId);

    public static ComponentMultiplicity Multiplicity(GeneratorOperation operation) =>
        Resolve(operation.Template, OperationRuntimeSpecCompiler.GetOrCompile(operation),
            CardEffectRules.CardUniqueEffectKey(operation), operation.LocalizationId);

    internal static string? PoolUniqueKey(ComponentAtom atom) =>
        Multiplicity(atom) == ComponentMultiplicity.UniquePerPool
            ? StableMultiplicityKey(atom.Template, OperationRuntimeSpecCompiler.GetOrCompile(atom),
                CardEffectRules.CardUniqueEffectKey(atom), atom.SemanticId)
            : null;

    internal static string? PoolUniqueKey(GeneratorOperation operation) =>
        Multiplicity(operation) == ComponentMultiplicity.UniquePerPool
            ? StableMultiplicityKey(operation.Template, OperationRuntimeSpecCompiler.GetOrCompile(operation),
                CardEffectRules.CardUniqueEffectKey(operation), operation.LocalizationId)
            : null;

    internal static bool HasReplaceableSlot(ComponentAtom atom) =>
        DerivativeSlotCatalog.IsSlotOperation(atom.Template) || OrbSlotCatalog.IsSlotOperation(atom.Template);

    internal static int ReplaceableSlotOccurrenceWeight(ComponentAtom atom) => HasReplaceableSlot(atom) ? 110 : 100;

    /// <summary>
    /// Validates the two multiplicity scopes on a completed generated pool. This is intentionally independent of
    /// the random assembler so imported/external catalogs and saved snapshots can run the same invariant check.
    /// </summary>
    public static bool TryAuditPool(IEnumerable<GeneratedCard> cards, out string failure)
    {
        var seenPoolUnique = new HashSet<string>(StringComparer.Ordinal);
        var cardIndex = 0;
        foreach (var card in cards)
        {
            cardIndex++;
            var seenSinglePerCard = new HashSet<string>(StringComparer.Ordinal);
            foreach (var operation in card.Operations)
            {
                var multiplicity = Multiplicity(operation);
                if (multiplicity == ComponentMultiplicity.Repeatable) continue;
                var key = StableMultiplicityKey(operation.Template,
                    OperationRuntimeSpecCompiler.GetOrCompile(operation),
                    CardEffectRules.CardUniqueEffectKey(operation), operation.LocalizationId);
                if (!seenSinglePerCard.Add(key))
                {
                    failure = $"Card {cardIndex} repeats single-card component '{key}'.";
                    return false;
                }
                if (multiplicity == ComponentMultiplicity.UniquePerPool && !seenPoolUnique.Add(key))
                {
                    failure = $"Pool repeats unique component '{key}' on card {cardIndex}.";
                    return false;
                }
            }
        }
        failure = string.Empty;
        return true;
    }

    private static ComponentMultiplicity Resolve(string template, OperationRuntimeSpec spec, string? nonStackingKey,
        string? semanticId)
    {
        lock (RegistrationLock)
        {
            if (Overrides.TryGetValue((template, spec.Variant), out var exact)) return exact;
            if (Overrides.TryGetValue((template, string.Empty), out var templateWide)) return templateWide;
        }
        if (BuiltInPoolUniqueKey(template, spec, nonStackingKey, semanticId) is not null)
            return ComponentMultiplicity.UniquePerPool;
        return nonStackingKey is not null
            ? ComponentMultiplicity.SinglePerCard
            : ComponentMultiplicity.Repeatable;
    }

    private static string StableMultiplicityKey(string template, OperationRuntimeSpec spec, string? nonStackingKey,
        string? semanticId) =>
        BuiltInPoolUniqueKey(template, spec, nonStackingKey, semanticId)
        ?? nonStackingKey
        ?? $"component:{template}:{spec.Variant}";

    private static string? BuiltInPoolUniqueKey(string template, OperationRuntimeSpec spec,
        string? nonStackingKey, string? semanticId)
    {
        if (nonStackingKey is not null && BuiltInPoolUniqueKeys.Contains(nonStackingKey))
            return nonStackingKey;

        var semantic = semanticId switch
        {
            // Corruption's payoff is deliberately a generic referenced-card Exhaust component; the preceding
            // trigger owns the Skill restriction. Preserve the source component's pool uniqueness without making
            // every other referenced-card Exhaust unique.
            "ironclad/corruption/2" => "rule:skills_exhaust",
            _ => template switch
            {
                "N:Exhaust" when spec is { Opcode: "exhaust_card", Variant: "all", SourceZone: "hand",
                    CardFilter: "non_attack" } => "clear:all_non_attack_hand_exhaust",
                "N:Exhaust" when spec is
                    { Opcode: "exhaust_card", Variant: "referenced", CardFilter: "skill" }
                    => "rule:skills_exhaust",
                "N:BlockEqualAllPoison" => "value:block_equal_all_poison",
                "R:PlaySelectedSkillMultipleTimes" => "action:play_selected_skill_multiple_times",
                "T:Apply" when spec.Variant == "vulnerable_double" => "target:vulnerable_double",
                "NCR:DoubleVulnerableWeak" => "target:vulnerable_weak_double",
                "NCR:CopyTargetDebuffsToOthers" => "target:copy_debuffs_to_others",
                "D:DrawAndDiscardNonZero" => "action:draw_discard_nonzero",
                "D:ShuffleAllUnexhaustedIntoDraw" => "action:shuffle_unexhausted",
                "CL:DamageEqualCardsPlayedCombat" => "damage:cards_played_combat",
                "C:ifExhaustPileAtLeast" => "condition:exhaust_pile_minimum",
                "A:ProxyAtomic_Buffer" => "rule:buffer",
                "CL:GainGold" or "A:ProxyAtomic_Royalties" => "reward:gain_gold",
                _ => null
            }
        };
        return semantic is not null && BuiltInPoolUniqueKeys.Contains(semantic) ? semantic : null;
    }
}
