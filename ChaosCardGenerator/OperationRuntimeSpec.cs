using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Globalization;

namespace ChaosCardGenerator;

/// <summary>
/// Versioned, localization-independent execution metadata consumed by generation, upgrades, balance, snapshots,
/// multiplayer sync, and combat execution. Localized text is only an output projection or a legacy migration input.
/// </summary>
public sealed record OperationRuntimeSpec(
    int SchemaVersion,
    string Opcode,
    string Variant,
    string Target,
    string SourceZone,
    string DestinationZone,
    string CardFilter,
    IReadOnlyList<string> Flags,
    IReadOnlyList<RuntimeValueSlot> Values,
    RuntimeConditionSpec? Condition = null,
    RuntimeTriggerSpec? Trigger = null)
{
    public const int CurrentSchemaVersion = 1;
    private static readonly ConditionalWeakTable<OperationRuntimeSpec, CachedSignatures> SignatureCache = new();

    private static bool IsIdentityNeutralFlag(string flag) => flag is
        "zero_damage" or "legacy_enemy_strength_one" or "percentage_value"
        or "printed_damage_value" or "printed_block_value" or "legacy_direct_draw_center"
        or "requires_block_anchor" or "standalone_keyword" or "requires_player_choice"
        or "copy_this_to_discard" or "referenced_non_attack_exhaust" or "skill_card_reference"
        or "giant_rock_reference" or "soul_reference" or "fuel_reference" or "debris_reference"
        or "sovereign_blade_reference";

    private sealed class CachedSignatures(OperationRuntimeSpec spec)
    {
        public string Stable { get; } = BuildStableSignature(spec);
        public string Shape { get; } = BuildShapeSignature(spec);
        public string XShape { get; } = BuildXShapeSignature(spec);
    }

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new InvalidOperationException($"Unsupported RuntimeSpec schema {SchemaVersion}.");
        ValidateId(Opcode, nameof(Opcode), required: true);
        ValidateId(Variant, nameof(Variant), required: false);
        ValidateId(Target, nameof(Target), required: true);
        ValidateId(SourceZone, nameof(SourceZone), required: true);
        ValidateId(DestinationZone, nameof(DestinationZone), required: true);
        ValidateId(CardFilter, nameof(CardFilter), required: true);
        if (Flags is null) throw new InvalidOperationException("RuntimeSpec flags cannot be null.");
        if (Values is null) throw new InvalidOperationException("RuntimeSpec value slots cannot be null.");
        foreach (var flag in Flags) ValidateId(flag, "flag", required: true);
        if (Flags.Distinct(StringComparer.Ordinal).Count() != Flags.Count)
            throw new InvalidOperationException("RuntimeSpec flags must be unique.");
        foreach (var value in Values)
        {
            if (value is null) throw new InvalidOperationException("RuntimeSpec value slots cannot contain null.");
            value.Validate();
        }
        if (Values.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != Values.Count)
            throw new InvalidOperationException("RuntimeSpec value-slot IDs must be unique.");
        Condition?.Validate();
        Trigger?.Validate();
        if (Condition?.ValueSlot is { } conditionSlot && Values.All(value => value.Id != conditionSlot))
            throw new InvalidOperationException($"Condition references missing RuntimeSpec slot {conditionSlot}.");
        if (Trigger?.ThresholdSlot is { } thresholdSlot && Values.All(value => value.Id != thresholdSlot))
            throw new InvalidOperationException($"Trigger references missing RuntimeSpec slot {thresholdSlot}.");
        if (Trigger?.DurationSlot is { } durationSlot && Values.All(value => value.Id != durationSlot))
            throw new InvalidOperationException($"Trigger references missing RuntimeSpec slot {durationSlot}.");
    }

    public string StableSignature() => SignatureCache.GetValue(this, static spec =>
    {
        spec.Validate();
        return new CachedSignatures(spec);
    }).Stable;

    private static string BuildStableSignature(OperationRuntimeSpec spec)
    {
        var builder = new StringBuilder()
            .Append(spec.SchemaVersion.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(spec.Opcode).Append('|').Append(spec.Variant).Append('|')
            .Append(spec.Target).Append('|').Append(spec.SourceZone).Append('|').Append(spec.DestinationZone).Append('|')
            .Append(spec.CardFilter).Append('|');
        foreach (var flag in spec.Flags.Order(StringComparer.Ordinal)) builder.Append("f:").Append(flag).Append(';');
        foreach (var value in spec.Values.OrderBy(value => value.Id, StringComparer.Ordinal))
            builder.Append("v:").Append(value.Id).Append('=')
                .Append(value.BaseValue.ToString(CultureInfo.InvariantCulture)).Append('@')
                .Append(value.Source).Append('+')
                .Append(value.Offset.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(value.Upgradable ? '1' : '0').Append(':')
                .Append(value.Explicit ? '1' : '0').Append(';');
        if (spec.Condition is not null)
            builder.Append("c:").Append(spec.Condition.Kind).Append(':').Append(spec.Condition.Subject).Append(':')
                .Append(spec.Condition.ValueSlot ?? string.Empty).Append(';');
        if (spec.Trigger is not null)
            builder.Append("t:").Append(spec.Trigger.Kind).Append(':').Append(spec.Trigger.Lifetime).Append(':')
                .Append(spec.Trigger.ThresholdSlot ?? string.Empty).Append(':')
                .Append(spec.Trigger.DurationSlot ?? string.Empty).Append(';');
        return builder.ToString();
    }

    public string RoutingSignature() => BuildRoutingSignature(Opcode, Variant, Target, SourceZone,
        DestinationZone, CardFilter, Flags);

    /// <summary>
    /// Numeric-value-independent identity used to group the same effect field. Unlike StableSignature this keeps
    /// slot positions and value sources but deliberately omits rolled values and upgrade deltas.
    /// </summary>
    public string ShapeSignature() => SignatureCache.GetValue(this, static spec =>
    {
        spec.Validate();
        return new CachedSignatures(spec);
    }).Shape;

    private static string BuildShapeSignature(OperationRuntimeSpec spec)
    {
        var builder = new StringBuilder()
            .Append(spec.Opcode).Append('|').Append(spec.Variant).Append('|').Append(spec.Target).Append('|')
            .Append(spec.SourceZone).Append('|').Append(spec.DestinationZone).Append('|').Append(spec.CardFilter).Append('|');
        foreach (var flag in spec.Flags
                     .Where(flag => !IsIdentityNeutralFlag(flag))
                     .Order(StringComparer.Ordinal))
            builder.Append("f:").Append(flag).Append(';');
        foreach (var value in spec.Values.OrderBy(value => value.Id, StringComparer.Ordinal))
            builder.Append("v:").Append(value.Id).Append('@').Append(value.Source).Append(':')
                .Append(value.Upgradable ? '1' : '0').Append(':').Append(value.Explicit ? '1' : '0').Append(';');
        if (spec.Condition is not null)
            builder.Append("c:").Append(spec.Condition.Kind).Append(':').Append(spec.Condition.Subject).Append(':')
                .Append(spec.Condition.ValueSlot ?? string.Empty).Append(';');
        if (spec.Trigger is not null)
            builder.Append("t:").Append(spec.Trigger.Kind).Append(':').Append(spec.Trigger.Lifetime).Append(':')
                .Append(spec.Trigger.ThresholdSlot ?? string.Empty).Append(':')
                .Append(spec.Trigger.DurationSlot ?? string.Empty).Append(';');
        return builder.ToString();
    }

    public string XShapeSignature() => SignatureCache.GetValue(this, static spec =>
    {
        spec.Validate();
        return new CachedSignatures(spec);
    }).XShape;

    private static string BuildXShapeSignature(OperationRuntimeSpec spec)
    {
        var builder = new StringBuilder()
            .Append(spec.Opcode).Append('|').Append(spec.Target).Append('|').Append(spec.SourceZone).Append('|')
            .Append(spec.DestinationZone).Append('|').Append(spec.CardFilter).Append('|');
        if (spec.Opcode.StartsWith("template_", StringComparison.Ordinal))
            builder.Append("variant:").Append(spec.Variant).Append(';');
        foreach (var flag in spec.Flags.Where(flag => flag is not ("uses_energy_x" or "uses_star_x")
                     && !IsIdentityNeutralFlag(flag))
                     .Order(StringComparer.Ordinal))
            builder.Append("f:").Append(flag).Append(';');
        foreach (var value in spec.Values.OrderBy(value => value.Id, StringComparer.Ordinal))
            builder.Append("v:").Append(value.Id).Append('@')
                .Append(value.Source is "energy_x" or "star_x" or "special_x" ? "x" : value.Source).Append(':')
                .Append(value.Upgradable ? '1' : '0').Append(':').Append(value.Explicit ? '1' : '0').Append(';');
        if (spec.Condition is not null)
            builder.Append("c:").Append(spec.Condition.Kind).Append(':').Append(spec.Condition.Subject).Append(':')
                .Append(spec.Condition.ValueSlot ?? string.Empty).Append(';');
        if (spec.Trigger is not null)
            builder.Append("t:").Append(spec.Trigger.Kind).Append(':').Append(spec.Trigger.Lifetime).Append(':')
                .Append(spec.Trigger.ThresholdSlot ?? string.Empty).Append(':')
                .Append(spec.Trigger.DurationSlot ?? string.Empty).Append(';');
        return builder.ToString();
    }

    public static string BuildRoutingSignature(string opcode, string variant, string target,
        string sourceZone = "none", string destinationZone = "none", string cardFilter = "any",
        IEnumerable<string>? flags = null) => string.Join('|', opcode, variant, target, sourceZone,
        destinationZone, cardFilter, string.Join(',', (flags ?? []).Order(StringComparer.Ordinal)));

    internal static void ValidateId(string value, string field, bool required)
    {
        if (string.IsNullOrEmpty(value))
        {
            if (required) throw new InvalidOperationException($"RuntimeSpec {field} cannot be empty.");
            return;
        }
        if (value.Any(character => character > 0x7f)
            || !Regex.IsMatch(value, "^[a-z0-9_]+$", RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)))
            throw new InvalidOperationException($"RuntimeSpec {field} must be a lowercase ASCII identifier: {value}");
    }
}

public sealed record RuntimeValueSlot(string Id, int BaseValue, string Source = "fixed", int Offset = 0,
    bool Upgradable = true, bool Explicit = true)
{
    internal void Validate()
    {
        OperationRuntimeSpec.ValidateId(Id, nameof(Id), required: true);
        OperationRuntimeSpec.ValidateId(Source, nameof(Source), required: true);
    }
}

public sealed record RuntimeConditionSpec(string Kind, string Subject = "none", string? ValueSlot = null)
{
    internal void Validate()
    {
        OperationRuntimeSpec.ValidateId(Kind, nameof(Kind), required: true);
        OperationRuntimeSpec.ValidateId(Subject, nameof(Subject), required: true);
        if (ValueSlot is not null) OperationRuntimeSpec.ValidateId(ValueSlot, nameof(ValueSlot), required: true);
    }
}

public sealed record RuntimeTriggerSpec(string Kind, string Lifetime, string? ThresholdSlot = null,
    string? DurationSlot = null)
{
    internal void Validate()
    {
        OperationRuntimeSpec.ValidateId(Kind, nameof(Kind), required: true);
        OperationRuntimeSpec.ValidateId(Lifetime, nameof(Lifetime), required: true);
        if (ThresholdSlot is not null)
            OperationRuntimeSpec.ValidateId(ThresholdSlot, nameof(ThresholdSlot), required: true);
        if (DurationSlot is not null)
            OperationRuntimeSpec.ValidateId(DurationSlot, nameof(DurationSlot), required: true);
    }
}

/// <summary>
/// Temporary source-text location used while legacy descriptions are still rendered with DynamicVar tokens.
/// It is deliberately not part of persisted RuntimeSpec: new snapshots will persist values, not localized spans.
/// </summary>
public sealed record LegacyRuntimeValueProjection(string SlotId, int BaseValue, int Start, int Length);

/// <summary>
/// The only migration layer allowed to infer semantics from legacy localized operation text. New structural
/// producers will gradually bypass these branches and construct the same RuntimeSpec directly.
/// </summary>
public static class OperationRuntimeSpecCompiler
{
    private static readonly Regex Number = new(@"\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly ConditionalWeakTable<GeneratorOperation, OperationRuntimeSpec> OperationSpecs = new();
    private static readonly ConditionalWeakTable<ComponentAtom, OperationRuntimeSpec> AtomSpecs = new();
    private static readonly ConditionalWeakTable<GeneratorOperation, string> OperationFieldKeys = new();
    private static readonly ConditionalWeakTable<ComponentAtom, string> AtomFieldKeys = new();
    private static readonly ConditionalWeakTable<GeneratorOperation, string> OperationRelationKeys = new();
    private static readonly ConditionalWeakTable<ComponentAtom, string> AtomRelationKeys = new();
    private static readonly ConditionalWeakTable<GeneratorOperation, string> OperationXKeys = new();
    private static readonly ConditionalWeakTable<ComponentAtom, string> AtomXKeys = new();
    internal static bool EnableLegacyEquivalenceAssertions { get; set; }
    private static readonly HashSet<string> BatchOneTemplates = new(StringComparer.Ordinal)
    {
        "T:Apply", "N:Self", "N:StrengthPerTargetVulnerable", "N:Create",
        "N:CreateCurrentCharacterCardInHand", "N:Move", "N:Exhaust"
    };

    private static readonly HashSet<string> NumericBatchTemplates = new(StringComparer.Ordinal)
    {
        "N:AllD", "N:RandomD", "N:RandomPoison", "CL:AddRandomZeroCostCardsToHand",
        "CL:ProxyAtomic_Discovery", "CL:ProxyAtomic_Splash", "I:ProxyAtomic_Quasar",
        "D:DrawAndDiscardNonZero", "I:DrawAndBlockIfSkill"
    };

    public static bool IsBatchOne(string template) => BatchOneTemplates.Contains(template);

    public static string StructuralFieldKey(GeneratorOperation operation) => OperationFieldKeys.GetValue(operation,
        static value => $"{NumericTextSchema.Family(value.Template)}|{KeyShape(value.Template, GetOrCompile(value))}"
            + SlotShape(value.Template, value.DerivativeId, value.DerivativeEnchantmentId,
                value.OrbSourceId, value.OrbOutputId));

    public static string StructuralFieldKey(ComponentAtom atom) => AtomFieldKeys.GetValue(atom,
        static value => $"{NumericTextSchema.Family(value.Template)}|{KeyShape(value.Template, GetOrCompile(value))}"
            + SlotShape(value.Template, null, null, null, null));

    public static string StructuralRelationKey(GeneratorOperation operation) => OperationRelationKeys.GetValue(operation,
        static value => $"{value.Template}|{KeyShape(value.Template, GetOrCompile(value))}"
            + SlotShape(value.Template, value.DerivativeId, value.DerivativeEnchantmentId,
                value.OrbSourceId, value.OrbOutputId));

    public static string StructuralRelationKey(ComponentAtom atom) => AtomRelationKeys.GetValue(atom,
        static value => $"{value.Template}|{KeyShape(value.Template, GetOrCompile(value))}"
            + SlotShape(value.Template, null, null, null, null));

    public static string StructuralXEffectKindKey(GeneratorOperation operation) => OperationXKeys.GetValue(operation,
        static value => KeyXShape(value.Template, GetOrCompile(value))
            + SlotShape(value.Template, value.DerivativeId, value.DerivativeEnchantmentId,
                value.OrbSourceId, value.OrbOutputId));

    public static string StructuralXEffectKindKey(ComponentAtom atom) => AtomXKeys.GetValue(atom,
        static value => KeyXShape(value.Template, GetOrCompile(value))
            + SlotShape(value.Template, null, null, null, null));

    public static string StructuralExactKey(GeneratorOperation operation) =>
        $"{operation.Template}|{GetOrCompile(operation).StableSignature()}"
        + SlotShape(operation.Template, operation.DerivativeId, operation.DerivativeEnchantmentId,
            operation.OrbSourceId, operation.OrbOutputId);

    public static string StructuralExactKey(ComponentAtom atom) =>
        $"{atom.Template}|{GetOrCompile(atom).StableSignature()}"
        + SlotShape(atom.Template, null, null, null, null);

    private static string KeyShape(string template, OperationRuntimeSpec spec) =>
        LegacyFieldSlotAliases(template, spec).ShapeSignature();

    private static string KeyXShape(string template, OperationRuntimeSpec spec) =>
        LegacyFieldSlotAliases(template, spec).XShapeSignature();

    private static OperationRuntimeSpec LegacyFieldSlotAliases(string template, OperationRuntimeSpec spec)
    {
        // The frozen text field identity only knew “the first number”. Runtime execution and upgrades now use
        // semantic names, but renaming these two old fallback slots must not perturb fixed-seed candidate groups.
        var semanticSlot = template switch
        {
            "N:NextTurnBlock" => "block",
            "N:NextTurnDraw" => "draw",
            _ => null
        };
        if (semanticSlot is null || spec.Values.All(value => value.Id != semanticSlot)) return spec;
        return spec with
        {
            Values = spec.Values.Select(value => value.Id == semanticSlot ? value with { Id = "amount" } : value)
                .ToArray()
        };
    }

    private static string SlotShape(string template, string? derivativeId, string? enchantmentId,
        string? orbSourceId, string? orbOutputId)
    {
        var derivative = DerivativeSlotCatalog.Resolve(derivativeId, template)?.Id;
        var enchantment = DerivativeSlotCatalog.ResolveEnchantment(derivativeId, enchantmentId, template)?.Id;
        var orbSource = OrbSlotCatalog.ResolveSource(orbSourceId, template)?.Id;
        var orbOutput = OrbSlotCatalog.ResolveOutput(orbOutputId, template)?.Id;
        return $"|slots:d={derivative ?? "none"},e={enchantment ?? "none"},os={orbSource ?? "none"},oo={orbOutput ?? "none"}";
    }

    /// <summary>
    /// Returns the immutable spec attached by the new generator, or compiles legacy operations exactly once at
    /// the caller's migration boundary. Runtime code should use this instead of reparsing localized text itself.
    /// </summary>
    public static OperationRuntimeSpec GetOrCompile(GeneratorOperation operation) =>
        operation.RuntimeSpec ?? OperationSpecs.GetValue(operation, static value => CompileRequired(value));

    /// <summary>
    /// Runtime boundary for live generated cards. Unlike GetOrCompile this never infers behavior from localized
    /// prose: old snapshots must be migrated and hydrated before a card reaches combat, rendering or valuation.
    /// </summary>
    public static OperationRuntimeSpec RequireStructured(GeneratorOperation operation) =>
        operation.RuntimeSpec ?? throw new InvalidDataException(
            $"Live operation {operation.Template} reached runtime without a persisted RuntimeSpec.");

    /// <summary>Explicit legacy snapshot/authoring migration entry point.</summary>
    public static OperationRuntimeSpec CompileLegacy(GeneratorOperation operation) => CompileRequired(operation);

    public static OperationRuntimeSpec GetOrCompile(ComponentAtom atom) => atom.RuntimeSpec
        ?? AtomSpecs.GetValue(atom, static value =>
            CompileRequired(new GeneratorOperation(value.Template, value.Scope, value.ChineseText,
                new Dictionary<string, int>(), RequiresSingleTarget: value.RequiresSingleTarget)));

    public static bool IsIntrinsicNegative(GeneratorOperation operation)
    {
        var spec = GetOrCompile(operation);
        if (spec.Opcode is "lose_hp" or "discard_card") return true;
        // A player-selected Exhaust is controlled combat deck-thinning. Native Burning Pact, Scavenge,
        // Cleanse and Purity all spend card budget on that utility; only random/all/referenced Exhaust remains
        // an intrinsic payment. Mandatory selection count still keeps its special upgrade/count safeguards.
        if (spec.Opcode == "exhaust_card" && spec.Variant != "selected") return true;
        if (operation.Template is
            "N:LoseDex" or "I:PreventDrawThisTurn" or "I:ExhaustRandomAttack"
            or "D:LoseFocus" or "D:LoseTemporaryFocus" or "D:LoseOrbSlots"
            or "D:DrawAndDiscardNonZero"
            or "NCR:ApplySelfDoom" or "NCR:LoseStrength" or "NCR:KillOsty"
            or "NCR:IncreaseAllCardCostsThisTurn"
            or "CL:DieOnUnblockedAttack" or "CL:NoBlockFromCards"
            or "R:EndTurn" or "R:DiscardTopOfDraw" or "D:IncreaseThisCardCost"
            or "M:DamageMinusPerCardInHand"
            )
            return true;
        if (operation.Template == "R:FillHandWithDebris"
            && DerivativeSlotCatalog.Resolve(operation.DerivativeId, operation.Template) is { } output)
            return DerivativeSlotCatalog.IsStatus(output) || DerivativeSlotCatalog.IsCurse(output);
        return operation.Template == "T:Apply" && spec.Variant == "strength_gain";
    }

    public static bool IsIntrinsicNegative(ComponentAtom atom) => IsIntrinsicNegative(new GeneratorOperation(
        atom.Template, atom.Scope, atom.ChineseText, new Dictionary<string, int>(),
        RequiresSingleTarget: atom.RequiresSingleTarget, RuntimeSpec: GetOrCompile(atom)));

    /// <summary>
    /// Upgrade projection for random hand exhaustion. The execution contract changes from RNG selection to an
    /// exact player choice without changing the count, source zone, filter, or snapshot-stable numeric slots.
    /// </summary>
    public static OperationRuntimeSpec AsSelectedExhaust(OperationRuntimeSpec spec)
    {
        if (spec is { Opcode: "template_independent_action", Variant: "i_exhaustrandomattack" })
            return spec with
            {
                Variant = "i_exhaustselectedattack",
                Target = "selected_card",
                SourceZone = "hand",
                CardFilter = "attack",
                Flags = spec.Flags.Append("requires_player_choice").Append("requires_hand_cards")
                    .Distinct(StringComparer.Ordinal).ToArray()
            };
        if (spec.Opcode != "exhaust_card" || spec.Variant != "random") return spec;
        return spec with
        {
            Variant = "selected",
            Target = "selected_card",
            Flags = spec.Flags.Append("requires_player_choice").Append("requires_hand_cards")
                .Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    public static bool IsCostIncrease(GeneratorOperation operation) => operation.Template is
        "D:IncreaseThisCardCost" or "NCR:IncreaseAllCardCostsThisTurn";

    public static bool IsCostIncrease(ComponentAtom atom) => atom.Template is
        "D:IncreaseThisCardCost" or "NCR:IncreaseAllCardCostsThisTurn";

    public static OperationRuntimeSpec StandaloneRetaliateDamage(OperationRuntimeSpec source) => source with
    {
        Flags = ["has_numeric_literal", "printed_damage_value", "scalable_reward_wording"]
    };

    public static OperationRuntimeSpec AddExplicitFixedValue(OperationRuntimeSpec source, string slotId,
        int value)
    {
        if (source.Values.Any(slot => slot.Id == slotId))
            throw new InvalidOperationException($"RuntimeSpec already contains value slot {slotId}.");
        return source with
        {
            Values = source.Values.Append(new RuntimeValueSlot(slotId, Math.Max(0, value))).ToArray(),
            Flags = source.Flags.Append("has_numeric_literal").Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray()
        };
    }

    public static OperationRuntimeSpec ConvertFixedValuesToSpecialX(OperationRuntimeSpec source,
        IReadOnlyCollection<string> slotIds)
    {
        var selected = slotIds.ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0 || selected.Any(id => source.Values.All(value => value.Id != id)))
            throw new InvalidOperationException("Special-X conversion references a missing RuntimeSpec slot.");
        return source with
        {
            Values = source.Values.Select(value => selected.Contains(value.Id)
                ? value with { BaseValue = 0, Source = "special_x", Offset = 0 }
                : value).ToArray()
        };
    }

    public static OperationRuntimeSpec WithFixedValue(OperationRuntimeSpec source, string slotId, int value) =>
        ReplaceFixedValueInSpec(source, slotId, value);

    public static OperationRuntimeSpec WithDerivativeReference(OperationRuntimeSpec source, string derivativeId)
    {
        var flags = source.Flags.ToHashSet(StringComparer.Ordinal);
        flags.ExceptWith([
            "shiv_reference", "giant_rock_reference", "soul_reference", "fuel_reference",
            "debris_reference", "sovereign_blade_reference"
        ]);
        var replacement = derivativeId switch
        {
            "shiv" => "shiv_reference",
            "rock" => "giant_rock_reference",
            "soul" => "soul_reference",
            "fuel" => "fuel_reference",
            "debris" => "debris_reference",
            "sword" => "sovereign_blade_reference",
            _ => null
        };
        if (replacement is not null) flags.Add(replacement);
        return source with { Flags = flags.Order(StringComparer.Ordinal).ToArray() };
    }

    public static OperationRuntimeSpec GeneratedHandSelection(bool attackOnly) => new(
        OperationRuntimeSpec.CurrentSchemaVersion,
        "template_self_action",
        attackOnly ? "n_select_hand_attack" : "n_select_hand_card",
        "self", "none", "none", "any",
        ["count_unit_reference", "requires_hand_cards", "requires_player_choice"], []);

    public static GeneratorOperation Attach(GeneratorOperation operation)
    {
        return operation.RuntimeSpec is not null
            ? operation
            : throw new InvalidOperationException(
                $"Newly generated operation {operation.Template} has no structured RuntimeSpec.");
    }

    public static IReadOnlyList<GeneratorOperation> Attach(IReadOnlyList<GeneratorOperation> operations) =>
        operations.Select(Attach).ToArray();

    public static GeneratedCard Attach(GeneratedCard card)
    {
        var operations = Attach(card.Operations);
        return card with { Operations = operations };
    }

    public static string? UpgradeValueSlot(GeneratorOperation operation)
    {
        var spec = GetOrCompile(operation);
        var structured = operation.Template == "I:DrawAndBlockIfSkill"
            && spec.Values.Any(value => value.Id == "block" && value.Upgradable)
                ? "block"
                : spec.Values.FirstOrDefault(value => value.Upgradable
                    && value.Source is "energy_x" or "star_x" or "special_x")?.Id
                  ?? spec.Values.FirstOrDefault(value => value.Upgradable)?.Id;
        if (EnableLegacyEquivalenceAssertions)
        {
            string? legacy = spec.Values.FirstOrDefault(value => value.Upgradable
                && value.Source is "energy_x" or "star_x" or "special_x")?.Id;
            if (legacy is null
                && TryProjectLegacyUpgradeValue(operation, operation.ChineseText, out var projection)
                && projection is not null
                && spec.Values.Any(value => value.Id == projection.SlotId && value.Upgradable))
                legacy = projection.SlotId;
            legacy ??= spec.Values.FirstOrDefault(value => value.Upgradable)?.Id;
            if (legacy != structured)
                throw new InvalidOperationException($"Upgrade slot drift for {operation.Template}:"
                    + $" legacy={legacy}, structured={structured}, text={operation.ChineseText}");
        }
        return structured;
    }

    public static bool ValueUsesX(GeneratorOperation operation)
    {
        var structured = GetOrCompile(operation).Values.Any(value =>
            value.Source is "energy_x" or "star_x" or "special_x");
        if (EnableLegacyEquivalenceAssertions)
        {
            var legacy = LegacyValueUsesX(operation);
            if (legacy != structured)
                throw new InvalidOperationException($"X value classification drift for {operation.Template}:"
                    + $" legacy={legacy}, structured={structured}, text={operation.ChineseText}");
        }
        return structured;
    }

    public static OperationRuntimeSpec ApplyUpgradeDelta(OperationRuntimeSpec spec, string slotId, int delta)
    {
        var slot = spec.Values.FirstOrDefault(value => value.Id == slotId)
            ?? throw new InvalidOperationException($"RuntimeSpec has no value slot {slotId}.");
        if (!slot.Upgradable)
            throw new InvalidOperationException($"RuntimeSpec slot {slotId} is not upgradable.");
        if (slot.Source == "fixed")
            return ReplaceFixedValueInSpec(spec, slotId, slot.BaseValue + slot.Offset + delta);
        return spec with
        {
            Values = spec.Values.Select(value => value.Id == slotId
                ? value with { Offset = Math.Max(0, value.Offset + delta) }
                : value).ToArray()
        };
    }

    internal static OperationRuntimeSpec ReplaceFixedValueInSpec(OperationRuntimeSpec spec, string slotId,
        int newValue)
    {
        var slotIndex = spec.Values.ToList().FindIndex(value => value.Id == slotId);
        if (slotIndex < 0 || spec.Values[slotIndex] is not { Source: "fixed" } slot)
            throw new InvalidOperationException($"RuntimeSpec has no fixed value slot {slotId}.");
        newValue = Math.Max(0, newValue);
        var values = spec.Values.ToArray();
        values[slotIndex] = slot with { BaseValue = Math.Max(0, newValue - slot.Offset) };
        // Several native template actions expose their printed count as the generic `amount` slot while the
        // executor consumes an inferred, non-explicit `repeat_count` slot.  They are two projections of the same
        // semantic value, not two independently rollable numbers.  Keep that hidden projection synchronized when
        // numeric generation or an upgrade replaces the printed amount.  Requiring the old values to agree avoids
        // touching operations that genuinely carry a separate repeat count.
        if (slotId == "amount")
        {
            var oldValue = slot.BaseValue + slot.Offset;
            for (var index = 0; index < values.Length; index++)
            {
                if (values[index] is not { Id: "repeat_count", Source: "fixed", Explicit: false } repeat
                    || repeat.BaseValue + repeat.Offset != oldValue)
                    continue;
                values[index] = repeat with { BaseValue = Math.Max(0, newValue - repeat.Offset) };
            }
        }
        var flags = spec.Flags.ToHashSet(StringComparer.Ordinal);
        if (slotId == "damage" && spec.Opcode == "deal_damage")
        {
            if (newValue == 0) flags.Add("zero_damage");
            else flags.Remove("zero_damage");
        }
        if (slotId == "amount" && spec.Opcode == "apply_power"
            && spec.Variant is "strength" or "strength_gain" or "strength_this_turn")
        {
            if (newValue == 1) flags.Add("legacy_enemy_strength_one");
            else flags.Remove("legacy_enemy_strength_one");
        }
        return spec with
        {
            Values = values,
            Flags = flags.Order(StringComparer.Ordinal).ToArray()
        };
    }

    public static bool IsNumericBatch(GeneratorOperation operation) =>
        NumericBatchTemplates.Contains(operation.Template)
        || operation.Template == "M:base"
            && operation.ChineseText.Contains("每有", StringComparison.Ordinal)
            && operation.ChineseText.Contains("力量", StringComparison.Ordinal)
            && operation.ChineseText.Contains("额外获得", StringComparison.Ordinal)
            && operation.ChineseText.Contains("格挡", StringComparison.Ordinal);

    public static bool TryCompile(GeneratorOperation operation, out OperationRuntimeSpec? spec, out string failure)
    {
        spec = operation.Template switch
        {
            "T:Apply" => CompileTargetPower(operation),
            "N:Self" or "N:StrengthPerTargetVulnerable" => CompileSelfPower(operation),
            "N:Create" or "N:CreateCurrentCharacterCardInHand" => CompileCreate(operation),
            "I:Create" => Spec("create_card", "random_attack_zero_cost_this_turn", "generated_card",
                sourceZone: "current_character_pool", destinationZone: "hand", cardFilter: "attack",
                flags: ["set_cost_zero_this_turn"], values:
                [Count(1, explicitValue: false),
                    new RuntimeValueSlot("cost_marker", 0, Upgradable: false)]),
            "N:Move" => CompileMove(operation),
            "N:Exhaust" => CompileExhaust(operation),
            "N:AllD" or "N:RandomD" => CompileMultiDamage(operation),
            "T:D" or "T:DX" or "T:D_EnergyX" => CompileTargetDamage(operation),
            "CL:DamageEqualCardsPlayedCombat" => Spec("deal_damage", "cards_played_combat",
                "selected_enemy", flags:
                ["requires_single_target", "damage_budget_effect", ComponentSemanticFlags.EnemyDamage]),
            "T:ProxyDamage_Atomic_EnergyX_Eradicate" or "N:ProxyDamage_Atomic_StarX_Stardust" =>
                CompileProxyXDamage(operation),
            "N:B" or "N_BLOCK" or "N:Draw" or "N:E" or "N:NextTurnEnergy"
                or "D:GainEnergy" or "D:NextTurnEnergy" or "NCR:GainEnergy" or "NCR:NextTurnEnergy"
                or "R:GainEnergy" or "R:GainStars" or "N:HP-" or "N:Discard" or "N:DiscardAll"
                or "N:Heal" or "N_HEAL" or "I:GainMaxHp" or "N:LoseDex" or "D:LoseFocus"
                or "D:LoseTemporaryFocus" or "D:LoseOrbSlots" or "NCR:LoseStrength"
                or "NCR:ApplySelfDoom" => CompileSimpleAction(operation),
            "CL:NoBlockFromCards" => Spec("restrict_block_from_cards", "next_n_turns", "self",
                values: [new RuntimeValueSlot("duration", FirstNumber(operation.ChineseText, 1))]),
            "R:EndTurn" => Spec("end_turn", "after_card_resolution", "self"),
            "N_EXHAUST_SELECTED" or "D:ExhaustSelectedHandCard" or "NCR:ExhaustSelectedDrawCard"
                or "R:DiscardTopOfDraw" => CompileCardPayment(operation),
            "N:RandomPoison" => CompileRandomPoison(operation),
            "CL:AddRandomZeroCostCardsToHand" => CompileRandomZeroCostCards(operation),
            "CL:ProxyAtomic_Discovery" or "CL:ProxyAtomic_Splash" or "I:ProxyAtomic_Quasar" =>
                CompileCardChoice(operation),
            "D:DrawAndDiscardNonZero" => CompileDrawAndDiscardNonZero(operation),
            "I:SetCostZero" => CompileCostZero(operation, "referenced_card"),
            "I:NextSkillCostsZero" => CompileCostZero(operation, "next_skill"),
            "D:NextPowerCostsZero" => CompileCostZero(operation, "next_power"),
            "NCR:NextVoidCostsZero" => CompileCostZero(operation, "next_ethereal"),
            "I:DrawAndBlockIfSkill" => CompileDrawAndBlockIfSkill(operation),
            "NCR:WheneverHighCostCardPlayed" => CompileAbilityTrigger(operation),
            "NCR:ForEachDoomThreshold" or "NCR:DoomPerDoomThreshold" => CompileDoomThreshold(operation),
            "R:IfEnergyXAtLeast" => CompileEnergyXCondition(operation),
            "R:DoubleEitherXAtThreshold" => Spec("modify_x", "double_at_threshold", "self",
                flags: ["uses_energy_x", "uses_star_x", "global_x_multiplier"],
                values: [new RuntimeValueSlot("threshold", 4,
                    Upgradable: false)]),
            "M:TriggeredAttackDamagePercent" => Spec("modify_damage", "triggered_attack_percentage",
                "event_attack", flags: ["damage_budget_effect", "percentage_value"],
                values: [ScalarValue(operation, "percentage", 50)]),
            "D:ForEachEnergySpentThisTurn" => operation.Scope == OperationScope.Modifier
                // Preserve the pre-v0.2.157 shape for old structured snapshots. New cards use the conditional-
                // trigger form below, while old cards keep their dedicated Damage operation's hit calculation.
                ? Spec("template_modifier", "energy_spent_this_turn", "self",
                    flags: ["energy_spent_reference"], values:
                    [new RuntimeValueSlot("threshold", FirstNumber(operation.ChineseText, 2), Upgradable: false)])
                : Spec("trigger", "event", "self",
                    flags: ["energy_spent_reference", "repeated_or_multiplicative"], values:
                    [new RuntimeValueSlot("threshold", FirstNumber(operation.ChineseText, 1), Upgradable: false)],
                    trigger: new RuntimeTriggerSpec("energy_spent_this_turn_excluding_self", "immediate", "threshold")),
            "D:RepeatPerEnergySpentThisTurn" => Spec("deal_damage",
                "selected_per_energy_spent_this_turn", "selected_enemy",
                flags: ["requires_single_target", "energy_spent_reference"],
                values: [ScalarValue(operation, "damage", 5)]),
            "D:IfHasFrost" => Spec("condition", "has_frost_orb", "self",
                condition: new RuntimeConditionSpec("has_frost_orb", "self")),
            "R:IfCardsPlayedAtLeastThisTurn" => Spec("condition", "cards_played_this_turn_at_least", "self",
                values: [new RuntimeValueSlot("threshold", FirstNumber(operation.ChineseText, 1), Upgradable: false)],
                condition: new RuntimeConditionSpec("cards_played_this_turn_at_least", "self", "threshold")),
            "M:base" => CompileBaseModifier(operation),
            "M:value" => Spec("modify_damage", "current_block", "self"),
            "M:repeat" => CompileRepeatModifier(operation),
            _ when operation.Scope == OperationScope.AbilityTrigger => CompileAbilityTrigger(operation),
            _ when operation.Scope == OperationScope.ConditionalTrigger => CompileConditionalTrigger(operation),
            _ when operation.Scope == OperationScope.AbilityRule => CompileAbilityRule(operation),
            _ => CompileTemplateFallback(operation)
        };
        if (spec is null)
        {
            failure = IsBatchOne(operation.Template)
                ? $"unrecognized_legacy_variant:{operation.Template}"
                : "template_not_migrated";
            return false;
        }
        spec = ApplyLegacySemanticFlags(operation, spec);
        try
        {
            spec.Validate();
            failure = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            spec = null;
            failure = $"invalid_runtime_spec:{exception.Message}";
            return false;
        }
    }

    public static OperationRuntimeSpec CompileRequired(GeneratorOperation operation)
    {
        if (TryCompile(operation, out var spec, out var failure) && spec is not null) return spec;
        throw new InvalidOperationException($"Cannot compile {operation.Template} / {operation.ChineseText}: {failure}");
    }

    private static OperationRuntimeSpec ApplyLegacySemanticFlags(GeneratorOperation operation,
        OperationRuntimeSpec spec)
    {
        var text = operation.ChineseText;
        var flags = spec.Flags.ToHashSet(StringComparer.Ordinal);
        if (text.Contains("目标易伤", StringComparison.Ordinal)
            || text.Contains("目标的易伤", StringComparison.Ordinal)
            || text.Contains("该目标", StringComparison.Ordinal)
            || text.Contains("该敌人", StringComparison.Ordinal)
            || text.Contains("敌人身上每有", StringComparison.Ordinal))
            flags.Add("requires_selected_enemy");
        if (text.Contains("随机", StringComparison.Ordinal) && text.Contains("敌人", StringComparison.Ordinal))
            flags.Add("random_enemy_reference");
        if (text.Contains("该目标", StringComparison.Ordinal)
            || text.Contains("该敌人", StringComparison.Ordinal)
            || text.Contains("那名敌人", StringComparison.Ordinal)
            || text.Contains("被命中的敌人", StringComparison.Ordinal))
            flags.Add("event_enemy_reference");
        if (text.Contains("被命中的敌人", StringComparison.Ordinal))
            flags.Add("hit_enemy_reference");
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("这张牌就造成", StringComparison.Ordinal)
            || trimmed.StartsWith("对该目标造成", StringComparison.Ordinal))
            flags.Add("conditional_damage_payoff");
        if (text.Contains("在下个回合", StringComparison.Ordinal)
            || text.Contains("下个回合开始时", StringComparison.Ordinal)
            || text.Contains("在接下来的", StringComparison.Ordinal))
            flags.Add("delayed_effect");
        if (text.Contains("失去生命", StringComparison.Ordinal))
            flags.Add("hp_loss_reference");
        if (text.Contains("获得", StringComparison.Ordinal)
            && text.Contains("格挡", StringComparison.Ordinal)
            && !text.Contains("下回合", StringComparison.Ordinal)
            && !text.Contains("永久增加", StringComparison.Ordinal))
            flags.Add("immediate_block_gain");
        if (text.Contains("手牌中的", StringComparison.Ordinal)
            || text.Contains("手牌中随机", StringComparison.Ordinal))
            flags.Add("requires_hand_cards");
        if (text.Contains("加入你的手牌", StringComparison.Ordinal)
            || text.Contains("加入手牌", StringComparison.Ordinal)
            || text.Contains("置入手牌", StringComparison.Ordinal)
            || text.Contains("放入手牌", StringComparison.Ordinal))
            flags.Add("replenishes_hand");
        if (operation.Scope == OperationScope.Modifier && text.Contains("伤害", StringComparison.Ordinal))
            flags.Add("damage_budget_effect");
        if (text.Contains("造成0点伤害", StringComparison.Ordinal))
            flags.Add("zero_damage");
        if (Regex.IsMatch(text, @"\d+点伤害", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            flags.Add("printed_damage_value");
        if (Regex.IsMatch(text, @"\d+点格挡", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            flags.Add("printed_block_value");
        if (Regex.IsMatch(text, @"\d+%", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            flags.Add("percentage_value");
        if (text.Contains("获得1点力量", StringComparison.Ordinal))
            flags.Add("legacy_enemy_strength_one");
        if (text.Contains("本回合", StringComparison.Ordinal))
            flags.Add("this_turn_reference");
        if (text.StartsWith("抽", StringComparison.Ordinal))
            flags.Add("legacy_direct_draw_center");
        if (operation.Scope == OperationScope.Modifier
            && text.Contains("额外获得", StringComparison.Ordinal))
            flags.Add("requires_block_anchor");
        if (text.Trim().TrimEnd('。') == "虚无")
            flags.Add("standalone_keyword");
        if (operation.Template is "N:Discard" or "I:GrantSlyToHandSkillThisTurn"
                or "I:CopySelectedCardNextTurn" or "CL:TransformSelectedHandCards"
                or "CL:ExhaustUpToHandCards" or "CL:MoveSelectedSkillDrawToHand"
                or "CL:MoveSelectedAttackDrawToHand" or "CL:ChooseFromRandomDrawCards"
                or "CL:ChooseDrawCardToHand" or "R:MoveDiscardCardToDrawTop"
                or "R:PlaySelectedSkillMultipleTimes" or "R:PutSelectedHandCardsOnDraw"
                or "R:PutSelectedHandCardOnDraw" or "R:CopySelectedColorlessCard"
                or "NCR:ExhaustSelectedDrawCard" or "NCR:MoveDiscardCardToHand"
                or "D:MoveDiscardCardToHand" or "I:PlayTopCardAndExhaust" or "I:PlayTopXCards"
                or "CL:PlayTopDrawCard" or "D:AutoPlayRandomAttackFromDraw"
                or "I:AutoPlayRandomAttackFromHand" or "I:PlayAtRandomEnemy"
            || text is "消耗手牌中的一张牌。" or "将弃牌堆中的一张牌放到抽牌堆顶部。"
                or "升级手牌中的一张牌。"
            || text.Contains("选择", StringComparison.Ordinal))
            flags.Add("requires_player_choice");
        if (operation.Template is "N:Create" or "D:CreateZeroCostCopyInDiscard" or "NCR:CreateCopyInDiscard"
            && text.Contains("复制", StringComparison.Ordinal)
            && text.Contains("弃牌堆", StringComparison.Ordinal)
            && (text.Contains("此牌", StringComparison.Ordinal) || text.Contains("这张牌", StringComparison.Ordinal)))
            flags.Add("copy_this_to_discard");
        if (operation.Template == "N:Exhaust" && text == "消耗那张非攻击牌。")
            flags.Add("referenced_non_attack_exhaust");
        if (operation.Template is "D:ReplayEventCard" or "D:ReturnEventCardToHand"
            or "CL:PutEventCardOnDrawTop" or "I:PlayAtRandomEnemy")
            flags.Add("requires_event_card_payload");
        if (operation.Template is "NCR:ApplyEventDamageAsDoom" or "NCR:AllEnemiesLoseEventHp")
            flags.Add("requires_event_amount_payload");
        if (operation.Template == "NCR:ApplyEventDamageAsDoom")
            flags.Add("event_enemy_reference");
        if (text.Contains("技能牌", StringComparison.Ordinal))
            flags.Add("skill_card_reference");
        // The old field key treated these semantically equivalent phrasings as separate sampling identities.
        // Preserve that frozen-seed distinction explicitly while removing localized text from consumers.
        if (operation.Template == "N:NextTurnDraw"
            && text.StartsWith("在下个回合，", StringComparison.Ordinal))
            flags.Add("field_variant_delayed_clause");
        if (operation.Template == "D:ForEachUniqueOrb"
            && text.StartsWith("你每有", StringComparison.Ordinal))
            flags.Add("field_variant_owner_pronoun");
        if (operation.Template == "A:whenAttackDealsDamage"
            || text.Contains("每当", StringComparison.Ordinal)
            || text.Contains("每有", StringComparison.Ordinal)
            || text.Contains("每打出", StringComparison.Ordinal)
            || text.Contains("每使用", StringComparison.Ordinal)
            || text.Contains("每抽", StringComparison.Ordinal)
            || text.Contains("每消耗", StringComparison.Ordinal)
            || text.Contains("每丢弃", StringComparison.Ordinal)
            || text.Contains("每生成", StringComparison.Ordinal)
            || text.Contains("每花费", StringComparison.Ordinal))
            flags.Add("repeated_or_multiplicative");
        if (text.Contains("造成未被格挡的伤害时", StringComparison.Ordinal)
            || text.Contains("攻击造成未被格挡的伤害", StringComparison.Ordinal)
            || text.Contains("攻击对一名敌人造成伤害时", StringComparison.Ordinal)
            || text.Contains("攻击造成伤害时", StringComparison.Ordinal))
            flags.Add("attack_damage_reference");
        if (text.Contains("每当你打出一张牌", StringComparison.Ordinal)
            || text.Contains("每打出一张牌", StringComparison.Ordinal)
            || text.Contains("每当你使用一张牌", StringComparison.Ordinal)
            || text.Contains("每使用一张牌", StringComparison.Ordinal))
            flags.Add("any_card_played_reference");
        if (text.Contains("受到一次攻击", StringComparison.Ordinal))
            flags.Add("attack_received_reference");
        if (text.Contains("回合开始时", StringComparison.Ordinal))
            flags.Add("turn_start_reference");
        if (text.Contains("拥有易伤", StringComparison.Ordinal))
            flags.Add("target_vulnerable_reference");
        if (text.Contains("小刀", StringComparison.Ordinal)) flags.Add("shiv_reference");
        if (text.Contains("巨石", StringComparison.Ordinal)) flags.Add("giant_rock_reference");
        if (text.Contains("灵魂", StringComparison.Ordinal)) flags.Add("soul_reference");
        if (text.Contains("燃料", StringComparison.Ordinal)) flags.Add("fuel_reference");
        if (text.Contains("碎屑", StringComparison.Ordinal) || text.Contains("残骸", StringComparison.Ordinal))
            flags.Add("debris_reference");
        if (text.Contains("君王之剑", StringComparison.Ordinal)) flags.Add("sovereign_blade_reference");
        if (text.Contains("格挡", StringComparison.Ordinal)) flags.Add("block_reference");
        if (text.StartsWith("抽", StringComparison.Ordinal)) flags.Add("draw_reference");
        if (text.TrimStart().StartsWith("抽", StringComparison.Ordinal)) flags.Add("leading_draw_reference");
        if (text.Contains("抽", StringComparison.Ordinal) && text.Contains("牌", StringComparison.Ordinal))
            flags.Add("effect_family_draw");
        if (text.Contains("无实体", StringComparison.Ordinal)) flags.Add("intangible_reference");
        if (text.Contains("所有敌人", StringComparison.Ordinal)) flags.Add("all_enemies_reference");
        if (text.Contains("集中", StringComparison.Ordinal) && !text.Contains("失去", StringComparison.Ordinal))
            flags.Add("positive_focus_reference");
        if ((text.Contains("力量", StringComparison.Ordinal) || text.Contains("敏捷", StringComparison.Ordinal))
            && !text.Contains("失去", StringComparison.Ordinal))
            flags.Add("positive_strength_dexterity_reference");
        if (text.Contains("力量", StringComparison.Ordinal)) flags.Add("strength_reference");
        if (text.Contains("失去力量", StringComparison.Ordinal)) flags.Add("strength_loss_wording");
        if (text.Contains("敏捷", StringComparison.Ordinal)) flags.Add("dexterity_reference");
        if (text.Contains("集中", StringComparison.Ordinal)) flags.Add("focus_reference");
        if (text.Contains("荆棘", StringComparison.Ordinal)) flags.Add("thorns_reference");
        if (text.Contains("召唤", StringComparison.Ordinal)) flags.Add("summon_reference");
        if (text.Contains("覆甲", StringComparison.Ordinal)) flags.Add("plating_reference");
        if (text.Contains("易伤", StringComparison.Ordinal)) flags.Add("vulnerable_reference");
        if (text.Contains("虚弱", StringComparison.Ordinal)) flags.Add("weak_reference");
        if (text.Contains("中毒", StringComparison.Ordinal)) flags.Add("poison_reference");
        if (text.Contains("灾厄", StringComparison.Ordinal)) flags.Add("doom_reference");
        if (text.Contains("回复", StringComparison.Ordinal)) flags.Add("heal_reference");
        if (text.StartsWith("回复", StringComparison.Ordinal)) flags.Add("leading_heal_reference");
        if (text.Contains("最大生命", StringComparison.Ordinal)) flags.Add("max_hp_reference");
        if (text.Contains("铸造", StringComparison.Ordinal)) flags.Add("forge_reference");
        if (text.Contains("蓝星", StringComparison.Ordinal) && text.StartsWith("获得", StringComparison.Ordinal))
            flags.Add("star_gain_reference");
        if (text.Contains("蓝星", StringComparison.Ordinal) && text.Contains("获得", StringComparison.Ordinal))
            flags.Add("effect_family_stars");
        if (text.Contains("充能球", StringComparison.Ordinal)) flags.Add("orb_reference");
        if (text.Contains("充能球", StringComparison.Ordinal) && text.Contains("生成", StringComparison.Ordinal))
            flags.Add("orb_channel_reference");
        if (text.IndexOfAny(['张', '次', '颗', '个']) >= 0) flags.Add("count_unit_reference");
        var needsExternalCardSlot = operation.Template != "A:rulePlayedSkillsGainSly"
            && operation.Template != "M:TriggeredAttackDamagePercent"
            && !operation.Template.StartsWith("I:Create", StringComparison.Ordinal)
            && operation.Template != "I:UpgradeThatCard"
            && operation.Template != "CL:PutEventCardOnDrawTop"
            && operation.Template is not ("D:ReplayEventCard" or "D:ReturnEventCardToHand")
            && !text.Contains("那张攻击牌", StringComparison.Ordinal)
            && !text.Contains("对一名随机敌人打出这张牌", StringComparison.Ordinal)
            && !text.Contains("将该攻击牌额外打出", StringComparison.Ordinal)
            && !text.Contains("那张非攻击牌", StringComparison.Ordinal)
            && !text.Contains("此牌", StringComparison.Ordinal)
            && !text.Contains("这张牌", StringComparison.Ordinal)
            && !text.Contains("那张技能牌", StringComparison.Ordinal)
            && (text.Contains("该牌", StringComparison.Ordinal)
                || text.Contains("该技能", StringComparison.Ordinal)
                || text.Contains("该攻击", StringComparison.Ordinal)
                || text.Contains("那张牌", StringComparison.Ordinal)
                || operation.Template.Contains("UpgradeThatCard", StringComparison.Ordinal)
                || text.Contains("exhausted(card)", StringComparison.Ordinal));
        if (needsExternalCardSlot) flags.Add("needs_external_card_slot");
        if (text.Contains("将一张", StringComparison.Ordinal) && text.Contains("加入手牌", StringComparison.Ordinal))
            flags.Add("add_one_card_to_hand_reference");
        if (text.Contains("重放", StringComparison.Ordinal)) flags.Add("replay_reference");
        if (text.Contains("额外打出", StringComparison.Ordinal)) flags.Add("extra_play_reference");
        if (Number.IsMatch(text)) flags.Add("has_numeric_literal");
        if (text.StartsWith("获得", StringComparison.Ordinal)
            || text.StartsWith("抽", StringComparison.Ordinal)
            || text.StartsWith("回复", StringComparison.Ordinal)
            || text.StartsWith("给予", StringComparison.Ordinal)
            || text.StartsWith("对", StringComparison.Ordinal) && text.Contains("造成", StringComparison.Ordinal))
            flags.Add("scalable_reward_wording");
        if (operation.Scope == OperationScope.Modifier
            && text.Contains("额外造成", StringComparison.Ordinal)
            && text.Contains("次伤害", StringComparison.Ordinal))
            flags.Add("static_extra_damage_hits");
        if (text.Contains("给予", StringComparison.Ordinal)) flags.Add("apply_status_reference");
        if (text.Contains("费用", StringComparison.Ordinal)
            || text.Contains("耗能", StringComparison.Ordinal)
            || text.Contains("免费", StringComparison.Ordinal))
            flags.Add("cost_wording");
        if (text.Contains("消耗牌堆中至少", StringComparison.Ordinal))
            flags.Add("difficult_condition_tier_3");
        if (text.StartsWith("斩杀时", StringComparison.Ordinal)
            || text.Contains("拥有易伤", StringComparison.Ordinal)
            || text.Contains("失去过生命", StringComparison.Ordinal)
            || text.Contains("曾消耗过牌", StringComparison.Ordinal)
            || text.Contains("曾给予灾厄", StringComparison.Ordinal)
            || text.Contains("奥斯提在本回合攻击过", StringComparison.Ordinal))
            flags.Add("difficult_condition_tier_2");
        var values = spec.Values.ToList();
        // In a localized "X+1 times" form, 1 offsets the X-valued count; it is not a second repeat-count slot.
        // Treating it as an independent literal corrupts schema <= 7 migration and makes execution ambiguous.
        var repeatMatch = Regex.Match(text, @"(?<!X\+)(\d+)次", RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        if (repeatMatch.Success && values.All(value => value.Id is not ("hits" or "repeat_count")))
            values.Add(new RuntimeValueSlot("repeat_count",
                int.Parse(repeatMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                Upgradable: false, Explicit: false));
        return spec with
        {
            Flags = flags.Order(StringComparer.Ordinal).ToArray(),
            Values = values
        };
    }

    /// <summary>
    /// Central compatibility projection for the single DynamicVar supported by the legacy card model. No caller
    /// outside this compiler may decide which localized number is the operation's primary value.
    /// </summary>
    public static bool TryProjectLegacyDynamicValue(GeneratorOperation operation, string text,
        out LegacyRuntimeValueProjection? projection)
    {
        projection = null;
        if (operation.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger
            or OperationScope.AbilityRule)
            return false;
        var matches = Number.Matches(text);
        if (matches.Count == 0) return false;
        var numericIndex = operation.Template == "I:DrawAndBlockIfSkill" && matches.Count >= 2
            ? 1
            : operation.Scope == OperationScope.Modifier
                && operation.ChineseText.Contains("消耗牌堆", StringComparison.Ordinal)
                && matches.Count >= 2
                    ? matches.Count - 1
                    : 0;
        var match = matches[numericIndex];
        projection = new LegacyRuntimeValueProjection(PrimarySlotId(operation, numericIndex),
            int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture), match.Index, match.Length);
        return true;
    }

    /// <summary>
    /// Compatibility projection for the numeric position historically modified by CardUpgradeGenerator.
    /// This intentionally differs from the DynamicVar projection for a few old modifier schemas; retaining both
    /// projections keeps the frozen behavior explicit until those anomalies receive a deliberate migration rule.
    /// </summary>
    public static bool TryProjectLegacyUpgradeValue(GeneratorOperation operation, string text,
        out LegacyRuntimeValueProjection? projection)
    {
        projection = null;
        var matches = Number.Matches(text);
        if (matches.Count == 0) return false;
        var numericIndex = operation.Template == "I:DrawAndBlockIfSkill" && matches.Count >= 2 ? 1 : 0;
        var match = matches[numericIndex];
        projection = new LegacyRuntimeValueProjection(PrimarySlotId(operation, numericIndex),
            int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture), match.Index, match.Length);
        return true;
    }

    /// <summary>
    /// Preserves ChaosOperationExecutor.EffectiveText's historical first-number replacement. It is kept separate
    /// because I:DrawAndBlockIfSkill historically renders/upgrades its Block slot but the runtime effective-text
    /// fallback changed its draw slot. R2 records this mismatch instead of silently choosing either behavior.
    /// </summary>
    public static bool TryProjectLegacyExecutionUpgradeValue(GeneratorOperation operation, string text,
        out LegacyRuntimeValueProjection? projection)
    {
        projection = null;
        var match = Number.Match(text);
        if (!match.Success) return false;
        projection = new LegacyRuntimeValueProjection(PrimarySlotId(operation, 0),
            int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture), match.Index, match.Length);
        return true;
    }

    public static string ReplaceLegacyProjectedValue(string text, LegacyRuntimeValueProjection projection,
        int value) => text[..projection.Start]
            + Math.Max(0, value).ToString(System.Globalization.CultureInfo.InvariantCulture)
            + text[(projection.Start + projection.Length)..];

    public static bool LegacyValueUsesX(GeneratorOperation operation) =>
        operation.ChineseText.Contains('X');

    public static string IncreaseLegacyXValue(string text) =>
        text.Replace("X", "X+1", StringComparison.Ordinal);

    public static int LegacyXOffset(string text) =>
        text.Contains("X+1", StringComparison.Ordinal) ? 1 : 0;

    /// <summary>
    /// Returns a named fixed numeric value from the structured operation contract. Generator, balance and
    /// legality code must use this API rather than inspect localized text. X-backed values intentionally return
    /// <paramref name="fallback"/> because their value is not known while a card pool is assembled.
    /// </summary>
    public static int FixedValue(GeneratorOperation operation, string slotId, int fallback = 0)
    {
        var slot = GetOrCompile(operation).Values.FirstOrDefault(value => value.Id == slotId);
        return slot is { Source: "fixed" } ? slot.BaseValue : fallback;
    }

    public static int FixedValue(ComponentAtom atom, string slotId, int fallback = 0)
    {
        var slot = GetOrCompile(atom).Values.FirstOrDefault(value => value.Id == slotId);
        return slot is { Source: "fixed" } ? slot.BaseValue : fallback;
    }

    public static bool TryGetFixedUpgradeValue(GeneratorOperation operation, out string? slotId, out int value)
    {
        slotId = UpgradeValueSlot(operation);
        var selectedSlotId = slotId;
        var slot = selectedSlotId is null ? null : GetOrCompile(operation).Values
            .FirstOrDefault(candidate => candidate.Id == selectedSlotId);
        var structured = slot is { Source: "fixed", Explicit: true };
        value = structured ? slot!.BaseValue + slot.Offset : 0;
        if (EnableLegacyEquivalenceAssertions)
        {
            var legacy = TryProjectLegacyUpgradeValue(operation, operation.ChineseText, out var projection)
                && projection is not null;
            var legacyValue = legacy ? projection!.BaseValue : 0;
            if (legacy != structured || legacy && legacyValue != value)
                throw new InvalidOperationException($"Fixed upgrade value drift for {operation.Template}:"
                    + $" legacy={legacy}:{legacyValue}, structured={structured}:{value}, slot={slotId},"
                    + $" text={operation.ChineseText}");
        }
        return structured;
    }

    public static bool TryGetPrimaryExplicitFixedValue(GeneratorOperation operation, out string? slotId,
        out int value)
    {
        var slot = GetOrCompile(operation).Values.FirstOrDefault(candidate =>
            candidate.Explicit && candidate.Source == "fixed");
        slotId = slot?.Id;
        value = slot is null ? 0 : slot.BaseValue + slot.Offset;
        if (EnableLegacyEquivalenceAssertions)
        {
            var match = Number.Match(operation.ChineseText);
            var legacy = match.Success;
            var legacyValue = legacy
                ? int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            if (legacy != (slot is not null) || legacy && legacyValue != value)
                throw new InvalidOperationException($"Primary explicit value drift for {operation.Template}:"
                    + $" legacy={legacy}:{legacyValue}, structured={slot is not null}:{value}, text={operation.ChineseText}");
        }
        return slot is not null;
    }

    public static IReadOnlyList<RuntimeValueSlot> ExplicitFixedValueSlots(GeneratorOperation operation) =>
        GetOrCompile(operation).Values.Where(candidate => candidate.Explicit && candidate.Source == "fixed")
            .ToArray();

    public static IReadOnlyList<RuntimeValueSlot> ExplicitFixedValueSlots(ComponentAtom atom) =>
        GetOrCompile(atom).Values.Where(candidate => candidate.Explicit && candidate.Source == "fixed")
            .ToArray();

    public static bool TryReplaceFixedValue(GeneratorOperation operation, string slotId, int newValue,
        out GeneratorOperation updated)
    {
        var spec = GetOrCompile(operation);
        var slotIndex = spec.Values.ToList().FindIndex(value => value.Id == slotId);
        if (slotIndex < 0 || spec.Values[slotIndex] is not { Source: "fixed", Explicit: true } slot)
        {
            updated = operation;
            return false;
        }
        newValue = Math.Max(0, newValue);
        if (slot.BaseValue + slot.Offset == newValue)
        {
            updated = operation;
            return false;
        }
        if (!TryRenderFixedValue(operation, slotId, newValue, out var text))
            throw new InvalidOperationException($"Cannot render RuntimeSpec slot {slotId} for {operation.Template}: "
                                                + operation.ChineseText);
        var updatedSpec = ReplaceFixedValueInSpec(spec, slotId, newValue);
        updatedSpec.Validate();
        ExternalOperationTextRegistry.RegisterNumericVariant(operation.Template, operation.ChineseText, text);
        updated = operation with { ChineseText = text, RuntimeSpec = updatedSpec };
        return true;
    }

    /// <summary>
    /// Projects one structured fixed slot into the localized card text without registering or changing runtime
    /// semantics. This is a renderer bridge for upgrade previews; semantic callers must choose the slot first.
    /// </summary>
    public static bool TryRenderFixedValue(GeneratorOperation operation, string slotId, int newValue,
        out string text)
    {
        var spec = GetOrCompile(operation);
        var slotIndex = spec.Values.ToList().FindIndex(value => value.Id == slotId);
        if (slotIndex < 0 || spec.Values[slotIndex] is not { Source: "fixed", Explicit: true })
        {
            text = operation.ChineseText;
            return false;
        }
        if (operation.LocalizedText is { } localized)
        {
            var renderedSpec = ReplaceFixedValueInSpec(spec, slotId, newValue);
            text = localized.RenderChinese(renderedSpec);
            return true;
        }
        var numericIndex = spec.Values.Take(slotIndex).Count(value => value.Explicit
            && (value.Source == "fixed" || value.Offset != 0));
        var matches = Number.Matches(operation.ChineseText);
        if (numericIndex >= matches.Count)
        {
            text = operation.ChineseText;
            return false;
        }
        var match = matches[numericIndex];
        text = operation.ChineseText[..match.Index]
            + Math.Max(0, newValue).ToString(System.Globalization.CultureInfo.InvariantCulture)
            + operation.ChineseText[(match.Index + match.Length)..];
        return true;
    }

    public static bool TryReplacePrimaryExplicitFixedValue(GeneratorOperation operation, int newValue,
        out GeneratorOperation updated)
    {
        if (!TryGetPrimaryExplicitFixedValue(operation, out var slotId, out _) || slotId is null)
        {
            updated = operation;
            return false;
        }
        return TryReplaceFixedValue(operation, slotId, newValue, out updated);
    }

    /// <summary>
    /// Returns the numeric literal represented by a slot while assembling a card: a fixed base value, or an
    /// explicit X offset such as the 1 in X+1. A plain X has no literal and therefore returns the fallback. This
    /// preserves the old generator's compile-time treatment of X without reading its rendered description.
    /// </summary>
    public static int StaticLiteralValue(GeneratorOperation operation, string slotId, int fallback = 0)
    {
        var slot = GetOrCompile(operation).Values.FirstOrDefault(value => value.Id == slotId);
        if (slot is null) return fallback;
        if (slot.Source == "fixed") return slot.BaseValue;
        return slot.Offset != 0 ? slot.Offset : fallback;
    }

    /// <summary>
    /// Compatibility bridge for callers whose historical contract was the first printed numeric value. The slot
    /// order is declared by the compiler and is language-independent; new code should prefer <see cref="FixedValue"/>.
    /// </summary>
    public static int? PrimaryFixedValue(GeneratorOperation operation)
    {
        var slot = GetOrCompile(operation).Values.FirstOrDefault();
        return slot is { Source: "fixed" } ? slot.BaseValue : null;
    }

    public static int PrimaryStaticLiteralValue(GeneratorOperation operation, int fallback = 0)
    {
        var slot = GetOrCompile(operation).Values.FirstOrDefault();
        if (slot is null) return fallback;
        if (slot.Source == "fixed") return slot.BaseValue;
        return slot.Offset != 0 ? slot.Offset : fallback;
    }

    internal static void ValidateBatchOneCoverage()
    {
        var catalogs = Enum.GetValues<GeneratedCharacter>()
            .Select(character => CharacterComponentCatalogs.Get(character, unlockComponentRoles: false))
            .Append(CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad, unlockComponentRoles: true));
        var failures = new List<string>();
        var compiled = new List<OperationRuntimeSpec>();
        foreach (var recipe in catalogs.SelectMany(catalog => catalog.Recipes))
        {
            foreach (var atom in recipe.Atoms.Where(atom => IsBatchOne(atom.Template)))
            {
                var operation = new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
                    new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget);
                if (!TryCompile(operation, out var spec, out var failure) || spec is null)
                    failures.Add($"{recipe.Id}:{atom.Template}:{atom.ChineseText}:{failure}");
                else
                    compiled.Add(spec);
            }
        }
        if (failures.Count > 0)
            throw new InvalidOperationException("RuntimeSpec batch-one coverage failed:\n" + string.Join("\n", failures));

        var probes = new[]
        {
            new GeneratorOperation("T:Apply", OperationScope.SingleEnemyOnly, "给予3层易伤。",
                new Dictionary<string, int>(), RequiresSingleTarget: true),
            new GeneratorOperation("T:Apply", OperationScope.SingleEnemyOnly, "使该敌人在本回合失去7点力量。",
                new Dictionary<string, int>(), RequiresSingleTarget: true),
            new GeneratorOperation("N:Create", OperationScope.NonTargeted,
                "将一张当前角色的升级过的随机牌加入手牌。", new Dictionary<string, int>()),
            new GeneratorOperation("N:Exhaust", OperationScope.NonTargeted,
                "消耗手牌中的2张牌。", new Dictionary<string, int>())
        };
        compiled.AddRange(probes.Select(CompileRequired));
        foreach (var spec in compiled)
        {
            var json = JsonSerializer.Serialize(spec);
            var restored = JsonSerializer.Deserialize<OperationRuntimeSpec>(json)
                ?? throw new InvalidOperationException("RuntimeSpec JSON round-trip produced null.");
            if (restored.StableSignature() != spec.StableSignature())
                throw new InvalidOperationException("RuntimeSpec JSON round-trip changed its stable signature.");
        }
        var upgradedRandom = CompileRequired(probes[2]);
        if (upgradedRandom.Opcode != "create_card" || upgradedRandom.Variant != "current_character_random"
            || !upgradedRandom.Flags.Contains("upgrade_generated"))
            throw new InvalidOperationException("Upgraded current-character generation did not compile structurally.");
        var exhaustTwo = CompileRequired(probes[3]);
        if (exhaustTwo.Values.Single(value => value.Id == "count").BaseValue != 2)
            throw new InvalidOperationException("Hand-exhaust count did not compile into the count slot.");
    }

    internal static void ValidateNumericBatchCoverage()
    {
        var catalogs = Enum.GetValues<GeneratedCharacter>()
            .Select(character => CharacterComponentCatalogs.Get(character, unlockComponentRoles: false))
            .Append(CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad, unlockComponentRoles: true));
        var operations = catalogs.SelectMany(catalog => catalog.Recipes)
            .SelectMany(recipe => recipe.Atoms)
            .Select(atom => new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
                new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget))
            .Where(IsNumericBatch)
            .ToArray();
        var failures = operations.Where(operation => !TryCompile(operation, out _, out _)).ToArray();
        if (failures.Length > 0)
            throw new InvalidOperationException("RuntimeSpec numeric-batch coverage failed:\n"
                + string.Join("\n", failures.Select(operation => $"{operation.Template}:{operation.ChineseText}")));
        foreach (var operation in operations)
        {
            var spec = CompileRequired(operation);
            if (spec.Values.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != spec.Values.Count)
                throw new InvalidOperationException($"Duplicate numeric slot in {operation.Template}: {operation.ChineseText}");
        }
        var area = CompileRequired(new GeneratorOperation("N:AllD", OperationScope.NonTargeted,
            "对所有敌人造成4点伤害2次。", new Dictionary<string, int>()));
        if (Value(area, "damage") != 4 || Value(area, "hits") != 2)
            throw new InvalidOperationException("Area multi-hit Damage slots were not compiled correctly.");
        var scaledBlock = CompileRequired(new GeneratorOperation("M:base", OperationScope.Modifier,
            "你每有1点力量，这张牌额外获得5点格挡。", new Dictionary<string, int>()));
        if (Value(scaledBlock, "strength_interval") != 1 || Value(scaledBlock, "block_per_interval") != 5)
            throw new InvalidOperationException("Strength-scaled Block slots were not compiled correctly.");
        var ordinaryX = CompileRequired(new GeneratorOperation("N:AllD", OperationScope.NonTargeted,
            "对所有敌人造成5点伤害X次。", new Dictionary<string, int>()));
        if (ordinaryX.Values.Single(value => value.Id == "hits").Source != "energy_x")
            throw new InvalidOperationException("Ordinary X hit count did not compile to the energy_x source.");
        var specialX = CompileRequired(new GeneratorOperation("N:RandomD", OperationScope.NonTargeted,
            "随机对敌人造成3点伤害X+1次。", new Dictionary<string, int>
            {
                [SpecialXCardConverter.Parameter] = SpecialXCardConverter.EnergyResource,
                [SpecialXCardConverter.ValueMaskParameter] = 1 << 1
            }));
        var specialHits = specialX.Values.Single(value => value.Id == "hits");
        if (specialHits.Source != "special_x" || specialHits.Offset != 1)
            throw new InvalidOperationException("Special X+1 hit count did not compile to its named source and offset.");
        var fallbackXPlusOne = CompileRequired(new GeneratorOperation("I:ProxyAtomic_MultiCast",
            OperationScope.Independent, "激发你最右侧的充能球X+1次。", new Dictionary<string, int>()));
        var fallbackXValue = fallbackXPlusOne.Values.Single();
        if (fallbackXValue.Source != "energy_x" || fallbackXValue.Offset != 1)
            throw new InvalidOperationException("Fallback X+1 count did not compile to energy_x plus its offset.");
        var dualcast = new GeneratorOperation("D:EvokeRightmostOrb", OperationScope.NonTargeted,
            "激发最右侧的充能球2次。", new Dictionary<string, int>());
        if (!TryReplaceFixedValue(dualcast, "amount", 4, out var quadcast)
            || FixedValue(quadcast, "amount") != 4
            || FixedValue(quadcast, "repeat_count") != 4)
            throw new InvalidOperationException(
                "Printed template amount did not keep its inferred repeat-count projection synchronized.");
        var discovery = CompileRequired(new GeneratorOperation("CL:ProxyAtomic_Discovery",
            OperationScope.Independent, "从3张随机当前角色牌中选择1张加入手牌。其本回合耗能为0。",
            new Dictionary<string, int>()));
        if (Value(discovery, "choices") != 3 || Value(discovery, "picks") != 1
            || discovery.Values.Any(value => value.BaseValue == 0))
            throw new InvalidOperationException("Card-choice marker zero leaked into a RuntimeSpec value slot.");
        var drawAndBlock = CompileRequired(new GeneratorOperation("I:DrawAndBlockIfSkill",
            OperationScope.Independent, "抽2张牌。如果抽到的是技能牌，获得7点格挡。",
            new Dictionary<string, int>()));
        if (Value(drawAndBlock, "draw") != 2 || Value(drawAndBlock, "block") != 7)
            throw new InvalidOperationException("Draw-and-Block numeric slots were not compiled correctly.");
    }

    internal static void ValidateLegacyDynamicProjection()
    {
        var probes = new[]
        {
            (Operation: new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly, "造成12点伤害。",
                new Dictionary<string, int>()), Text: "Deal 12 damage.", Slot: "damage", Value: 12),
            (Operation: new GeneratorOperation("I:DrawAndBlockIfSkill", OperationScope.Independent,
                "抽2张牌。如果抽到的是技能牌，获得7点格挡。", new Dictionary<string, int>()),
                Text: "Draw 2 cards. If a Skill is drawn, gain 7 Block.", Slot: "block", Value: 7),
            (Operation: new GeneratorOperation("M:base", OperationScope.Modifier,
                "你的消耗牌堆每有1张牌，这张牌就额外造成3点伤害。", new Dictionary<string, int>()),
                Text: "For every 1 card in your Exhaust Pile, this card deals 3 additional damage.",
                Slot: "amount", Value: 3),
            (Operation: new GeneratorOperation("M:base", OperationScope.Modifier,
                "你每有1点力量，这张牌额外获得5点格挡。", new Dictionary<string, int>()),
                Text: "For every 1 Strength, this card gains 5 additional Block.",
                Slot: "strength_interval", Value: 1)
        };
        foreach (var probe in probes)
        {
            if (!TryProjectLegacyDynamicValue(probe.Operation, probe.Text, out var projection)
                || projection is null || projection.SlotId != probe.Slot || projection.BaseValue != probe.Value)
                throw new InvalidOperationException("Legacy DynamicVar projection changed for "
                    + $"{probe.Operation.Template}: expected {probe.Slot}={probe.Value}, got "
                    + $"{projection?.SlotId ?? "<none>"}={projection?.BaseValue.ToString() ?? "<none>"}.");
            var replaced = probe.Text[..projection.Start] + "987" + probe.Text[(projection.Start + projection.Length)..];
            if (!replaced.Contains("987", StringComparison.Ordinal))
                throw new InvalidOperationException("Legacy DynamicVar projection returned an invalid text span.");
        }
        var trigger = new GeneratorOperation("A:whenCardPlayed", OperationScope.AbilityTrigger,
            "每当你打出1张牌时，", new Dictionary<string, int>());
        if (TryProjectLegacyDynamicValue(trigger, trigger.ChineseText, out _))
            throw new InvalidOperationException("Ability-trigger marker leaked into a DynamicVar slot.");
        var exhaustModifier = probes[2].Operation;
        if (!TryProjectLegacyUpgradeValue(exhaustModifier, exhaustModifier.ChineseText, out var upgrade)
            || upgrade is null || upgrade.BaseValue != 1 || upgrade.Start != exhaustModifier.ChineseText.IndexOf('1'))
            throw new InvalidOperationException("Legacy upgrade projection no longer preserves the first-number rule.");
    }

    internal static void ValidateTriggerCoverage()
    {
        var catalogs = Enum.GetValues<GeneratedCharacter>()
            .Select(character => CharacterComponentCatalogs.Get(character, unlockComponentRoles: false))
            .Append(CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad, unlockComponentRoles: true));
        var failures = new List<string>();
        var count = 0;
        foreach (var recipe in catalogs.SelectMany(catalog => catalog.Recipes))
        foreach (var atom in recipe.Atoms.Where(atom => atom.Scope is OperationScope.AbilityTrigger
                     or OperationScope.ConditionalTrigger or OperationScope.AbilityRule))
        {
            count++;
            var operation = new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
                new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget);
            if (!TryCompile(operation, out var spec, out var failure) || spec is null)
                failures.Add($"{recipe.Id}:{atom.Template}:{atom.ChineseText}:{failure}");
        }
        if (count == 0 || failures.Count > 0)
            throw new InvalidOperationException("RuntimeSpec trigger coverage failed:\n"
                + string.Join("\n", failures));
        var persistentNextAttack = new GeneratorOperation("C:for", OperationScope.ConditionalTrigger,
            "你打出的下一张攻击牌获得效果：", new Dictionary<string, int>());
        if (!TryCompile(persistentNextAttack, out var persistentSpec, out _)
            || persistentSpec?.Trigger?.Kind != "next_attack" || persistentSpec.Trigger.Lifetime != "combat")
            throw new InvalidOperationException("Persistent next-Attack trigger did not compile structurally.");
    }

    internal static void ValidateFullCatalogCoverage()
    {
        var catalogs = Enum.GetValues<GeneratedCharacter>()
            .Select(character => CharacterComponentCatalogs.Get(character, unlockComponentRoles: false))
            .Append(CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad, unlockComponentRoles: true));
        var failures = new List<string>();
        var count = 0;
        foreach (var recipe in catalogs.SelectMany(catalog => catalog.Recipes))
        foreach (var atom in recipe.Atoms)
        {
            count++;
            var operation = new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
                new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget);
            if (!TryCompile(operation, out var spec, out var failure) || spec is null)
                failures.Add($"{recipe.Id}:{atom.Template}:{atom.ChineseText}:{failure}");
        }
        if (count == 0 || failures.Count > 0)
            throw new InvalidOperationException("RuntimeSpec full-catalog coverage failed:\n"
                + string.Join("\n", failures));
    }

    internal static void ValidateLocalizationIndependence()
    {
        var original = new GeneratorOperation("N:RandomD", OperationScope.NonTargeted,
            "随机对敌人造成4点伤害2次。", new Dictionary<string, int>());
        var compiled = CompileRequired(original);
        var alteredProjection = original with
        {
            ChineseText = "LOCALIZED_TEXT_MAY_CHANGE_WITHOUT_CHANGING_SEMANTICS",
            RuntimeSpec = compiled
        };
        var restored = GetOrCompile(alteredProjection);
        if (restored.StableSignature() != compiled.StableSignature()
            || Value(restored, "damage") != 4 || Value(restored, "hits") != 2)
            throw new InvalidOperationException("Persisted RuntimeSpec still depends on localized operation text.");

        var upgraded = ApplyUpgradeDelta(restored, "hits", 1);
        if (Value(upgraded, "damage") != 4 || Value(upgraded, "hits") != 3)
            throw new InvalidOperationException("Structured multi-slot upgrade changed the wrong value slot.");

        ValidateStructuredLocalizationIndependence();

        var specialX = alteredProjection with
        {
            Parameters = new Dictionary<string, int>
            {
                [SpecialXCardConverter.Parameter] = SpecialXCardConverter.EnergyResource,
                [SpecialXCardConverter.ValueMaskParameter] = 1 << 1
            },
            RuntimeSpec = compiled with
            {
                Values =
                [
                    new RuntimeValueSlot("damage", 4),
                    new RuntimeValueSlot("hits", 0, "special_x", 1)
                ]
            }
        };
        if (UpgradeValueSlot(specialX) != "hits")
            throw new InvalidOperationException("Structured X upgrade did not select the X-backed slot.");

        var specialRepeat = CompileRequired(new GeneratorOperation("M:repeat", OperationScope.Modifier,
            "本场战斗中你每失去过一次生命，这张牌额外造成X次伤害。",
            new Dictionary<string, int>
            {
                [SpecialXCardConverter.Parameter] = SpecialXCardConverter.EnergyResource,
                [SpecialXCardConverter.ValueMaskParameter] = 1
            }));
        var repeatSlot = specialRepeat.Values.Single(value => value.Id == "hits_per_hp_loss_event");
        if (repeatSlot.Source != "special_x" || repeatSlot.BaseValue != 0)
            throw new InvalidOperationException("Special-X repeat modifier was recompiled as a fixed fallback value.");

    }

    internal static void ValidateStructuredLocalizationIndependence()
    {
        // Exercise every reviewed operation. Localized payloads are deliberately replaced with unrelated text while
        // the structured contract is retained; every production generation/balance/upgrade classification must
        // remain identical. This method itself never compiles either projection.
        foreach (var atom in Enum.GetValues<GeneratedCharacter>()
                     .SelectMany(character => CharacterComponentCatalogs.Get(character).Atoms))
        {
            var operation = new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
                new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget,
                RuntimeSpec: atom.RuntimeSpec);
            var localizedMutation = operation with
            {
                ChineseText = $"LOCALIZATION_PAYLOAD_{atom.SemanticId ?? atom.Template}_MUST_NOT_BE_PARSED"
            };
            if (SemanticProjection(operation) != SemanticProjection(localizedMutation))
                throw new InvalidOperationException($"Production semantics depend on localization payload: "
                    + $"{atom.SemanticId ?? atom.Template}.");
        }

        static string SemanticProjection(GeneratorOperation operation)
        {
            var atom = new ComponentAtom(operation.Template, operation.Scope, operation.ChineseText,
                operation.RequiresSingleTarget, CardReferenceRequirement.None)
                { RuntimeSpec = GetOrCompile(operation) };
            return string.Join('|',
                GetOrCompile(operation).StableSignature(),
                StructuralFieldKey(operation),
                StructuralRelationKey(operation),
                StructuralXEffectKindKey(operation),
                UpgradeValueSlot(operation) ?? string.Empty,
                NativeUpgradeValueModel.Classify(operation),
                CardEffectRules.IsNegativeEffect(operation),
                CardEffectRules.IsBeneficialEffect(operation),
                CardEffectRules.IsRestrictedEffect(operation),
                CardEffectRules.IsExtremeLifecycleDownside(operation),
                CardEffectRules.IsEnemyDamage(operation),
                CardEffectRules.IsCardCreationOrTransformation(operation),
                CardEffectRules.IsSelfCostChange(operation),
                CardEffectRules.IsDelayedEffect(operation),
                CardEffectRules.IsPersistentPowerFoundation(operation),
                CardEffectRules.EffectFamily(operation),
                EffectBalanceModel.IsCondition(atom),
                EffectBalanceModel.EstimatedEffectValue(operation),
                EffectBalanceModel.RelativeTriggerFrequency(operation)
                    .ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    internal static void ValidateSemanticProjectionEquivalence()
    {
        var operations = Enum.GetValues<GeneratedCharacter>()
            .Select(character => CharacterComponentCatalogs.Get(character, unlockComponentRoles: false))
            .Append(CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad, unlockComponentRoles: true))
            .SelectMany(catalog => catalog.Recipes)
            .SelectMany(recipe => recipe.Atoms)
            .Select(atom => new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
                new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget))
            .DistinctBy(operation => $"{operation.Template}|{operation.Scope}|{operation.ChineseText}")
            .ToArray();
        SpecialXCardConverter.ValidateLegacyEligibilityEquivalence(operations);
        var failures = new List<string>();

        var schemaRows = operations.Select(operation => new
        {
            Operation = operation,
            Legacy = $"{NumericTextSchema.Family(operation.Template)}|{NumericTextSchema.Fields(operation.ChineseText)}",
            Structured = StructuralFieldKey(operation)
        }).ToArray();
        foreach (var group in schemaRows.GroupBy(row => row.Legacy, StringComparer.Ordinal)
                     .Where(group => group.Select(row => row.Structured).Distinct(StringComparer.Ordinal).Count() > 1))
            failures.Add("field_schema_legacy_split:" + group.Key + ":"
                + string.Join(" || ", group.Select(row => $"{row.Operation.Template}:{row.Operation.ChineseText}=>{row.Structured}")));
        foreach (var group in schemaRows.GroupBy(row => row.Structured, StringComparer.Ordinal)
                     .Where(group => group.Select(row => row.Legacy).Distinct(StringComparer.Ordinal).Count() > 1))
            failures.Add("field_schema_structured_merge:" + group.Key + ":"
                + string.Join(" || ", group.Select(row => $"{row.Operation.Template}:{row.Operation.ChineseText}=>{row.Legacy}")));
        var xSchemaRows = schemaRows.Where(row => GetOrCompile(row.Operation).Values.Any(value =>
                value.Source is "energy_x" or "star_x" or "special_x"))
            .Select(row => new
            {
                row.Operation,
                Legacy = NumericTextSchema.Fields(row.Operation.ChineseText).Trim(),
                Structured = StructuralXEffectKindKey(row.Operation)
            }).ToArray();
        foreach (var group in xSchemaRows.GroupBy(row => row.Legacy, StringComparer.Ordinal)
                     .Where(group => group.Select(row => row.Structured).Distinct(StringComparer.Ordinal).Count() > 1))
            failures.Add("x_schema_legacy_split:" + group.Key + ":"
                + string.Join(" || ", group.Select(row => $"{row.Operation.Template}:{row.Operation.ChineseText}=>{row.Structured}")));
        foreach (var group in xSchemaRows.GroupBy(row => row.Structured, StringComparer.Ordinal)
                     .Where(group => group.Select(row => row.Legacy).Distinct(StringComparer.Ordinal).Count() > 1))
            failures.Add("x_schema_structured_merge:" + group.Key + ":"
                + string.Join(" || ", group.Select(row => $"{row.Operation.Template}:{row.Operation.ChineseText}=>{row.Legacy}")));
        void Check(string name, GeneratorOperation operation, bool legacy, bool structured)
        {
            if (legacy != structured)
                failures.Add($"{name}:{operation.Template}:{operation.ChineseText}:legacy={legacy}:spec={structured}");
        }

        foreach (var operation in operations)
        {
            var spec = CompileRequired(operation);
            Check("enemy_damage_amplification", operation,
                operation.Template == "A:ruleWeakEnemiesTakeMoreAttackDamage"
                || operation.ChineseText.StartsWith("处于虚弱状态的敌人受到的攻击伤害增加", StringComparison.Ordinal)
                || operation.ChineseText.StartsWith("拥有易伤的敌人受到的伤害增加", StringComparison.Ordinal),
                operation.Scope == OperationScope.AbilityRule
                && spec.Variant is "weak_enemy_attack_damage_bonus" or "vulnerable_enemy_damage_bonus");
            Check("hp_loss_repeat", operation,
                operation.Template == "M:repeat"
                && operation.ChineseText.Contains("每失去过一次生命", StringComparison.Ordinal),
                operation.Template == "M:repeat" && spec.Variant == "hp_loss_scaled");
            Check("enemy_strength_gain", operation,
                operation.Template == "T:Apply"
                && operation.ChineseText.Contains("敌人", StringComparison.Ordinal)
                && operation.ChineseText.Contains("获得", StringComparison.Ordinal)
                && operation.ChineseText.Contains("力量", StringComparison.Ordinal),
                operation.Template == "T:Apply" && spec.Variant == "strength_gain");
            Check("double_target_vulnerable", operation,
                operation.Template == "T:Apply"
                && operation.ChineseText.Contains("易伤层数翻倍", StringComparison.Ordinal),
                operation.Template == "T:Apply" && spec.Variant == "vulnerable_double");
            Check("exhaust_all_hand", operation,
                operation.Template == "N:Exhaust"
                && operation.ChineseText.Contains("消耗所有手牌", StringComparison.Ordinal),
                operation.Template == "N:Exhaust" && spec is
                    { Opcode: "exhaust_card", Variant: "all", SourceZone: "hand", CardFilter: "any" });
            Check("block_gained_trigger", operation,
                operation.Template == "A_WHEN_GAIN_BLOCK"
                || operation.ChineseText.Contains("每当你获得格挡", StringComparison.Ordinal)
                || operation.ChineseText.Contains("每当获得格挡", StringComparison.Ordinal),
                operation.Template == "A_WHEN_GAIN_BLOCK" || spec.Trigger?.Kind == "block_gained");
            Check("every_card_drawn_trigger", operation,
                operation.Template is "A:whenCardDrawnDuringTurn" or "C:untilTurnEndCardDrawn"
                || operation.ChineseText.Contains("每当你抽到一张牌", StringComparison.Ordinal)
                || operation.ChineseText.Contains("每抽到一张牌", StringComparison.Ordinal),
                spec.Trigger?.Kind is "card_drawn" or "card_drawn_during_turn");
            var hasNumeric = Number.IsMatch(operation.ChineseText);
            Check("mandatory_discard_or_exhaust", operation,
                hasNumeric && (operation.Template is "N:Discard" or "R:DiscardTopOfDraw"
                || operation.Template is "N:Exhaust" or "N_EXHAUST_SELECTED"
                    or "D:ExhaustSelectedHandCard" or "NCR:ExhaustSelectedDrawCard"
                && !operation.ChineseText.Contains("至多", StringComparison.Ordinal)
                && !operation.ChineseText.Contains("任意数量", StringComparison.Ordinal)),
                spec.Opcode is "discard_card" or "exhaust_card"
                && spec.Variant is "selected" or "random" or "top"
                && spec.Values.Count > 0 && spec.Flags.Contains("explicit_numeric")
                && !spec.Flags.Contains("up_to"));
            Check("permanent_negative", operation,
                operation.Template is "N:LoseDex" or "D:LoseFocus" or "D:LoseOrbSlots" or "NCR:LoseStrength"
                || operation.Template == "T:Apply"
                    && operation.ChineseText.Contains("力量", StringComparison.Ordinal)
                    && !operation.ChineseText.Contains("失去", StringComparison.Ordinal)
                    && (operation.ChineseText.Contains("敌人", StringComparison.Ordinal)
                        || operation.ChineseText.Contains("目标", StringComparison.Ordinal)),
                operation.Template is "N:LoseDex" or "D:LoseFocus" or "D:LoseOrbSlots" or "NCR:LoseStrength"
                || operation.Template == "T:Apply" && spec.Variant == "strength_gain");
            Check("negative_effect", operation,
                CardEffectRules.IsNegativeEffect(operation),
                IsIntrinsicNegative(operation) || DerivativeSlotCatalog.ProducesStatus(operation));
            Check("cost_increase", operation,
                operation.Template is "D:IncreaseThisCardCost" or "NCR:IncreaseAllCardCostsThisTurn"
                || operation.Template != "I:ProxyAtomic_Transfigure"
                    && (operation.ChineseText.Contains("耗能增加", StringComparison.Ordinal)
                        || operation.ChineseText.Contains("费用增加", StringComparison.Ordinal)
                        || operation.ChineseText.Contains("消耗增加", StringComparison.Ordinal)
                        && operation.ChineseText.Contains("蓝星", StringComparison.Ordinal)),
                IsCostIncrease(operation));
            Check("gold_gain", operation,
                operation.Template is "CL:GainGold" or "A:ProxyAtomic_Royalties"
                || operation.ChineseText.Contains("获得", StringComparison.Ordinal)
                    && operation.ChineseText.Contains("金币", StringComparison.Ordinal),
                operation.Template is "CL:GainGold" or "A:ProxyAtomic_Royalties");
            Check("restricted_effect", operation,
                operation.Template is "N:Heal" or "N_HEAL" or "I:GainMaxHp" or "CL:ProxyAtomic_Alchemize"
                    or "CL:GainGold" or "D:IncreaseThisCardBlockRun" or "NCR:IncreaseThisCardDamageRun"
                || operation.ChineseText.Contains("随机药水", StringComparison.Ordinal)
                || operation.ChineseText.Contains("金币", StringComparison.Ordinal)
                    && operation.ChineseText.Contains("获得", StringComparison.Ordinal),
                operation.Template is "N:Heal" or "N_HEAL" or "I:GainMaxHp" or "CL:ProxyAtomic_Alchemize"
                    or "CL:GainGold" or "A:ProxyAtomic_Royalties" or "D:IncreaseThisCardBlockRun"
                    or "NCR:IncreaseThisCardDamageRun");
            var firstNumber = Number.Match(operation.ChineseText);
            var duration = Regex.Match(operation.ChineseText,
                @"(?:(?:接下来(?:的)?|未来|随后|持续)\s*)?(?<turns>\d+)\s*个?回合(?:内|期间)?",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            Check("reducible_negative_duration", operation,
                CardEffectRules.IsNegativeEffect(operation) && firstNumber.Success && duration.Success
                && duration.Groups["turns"].Index == firstNumber.Index,
                (IsIntrinsicNegative(operation) || DerivativeSlotCatalog.ProducesStatus(operation))
                && spec.Values.FirstOrDefault(value => value.Id == "duration") is { Upgradable: true });

            if (FirstNumericConsumerTemplates.Contains(operation.Template))
            {
                var legacyPrimary = firstNumber.Success
                    ? int.Parse(firstNumber.Value, System.Globalization.CultureInfo.InvariantCulture)
                    : (int?)null;
                var structuredPrimary = PrimaryFixedValue(operation);
                if (legacyPrimary != structuredPrimary)
                    failures.Add($"primary_numeric:{operation.Template}:{operation.ChineseText}:"
                        + $"legacy={legacyPrimary?.ToString() ?? "<none>"}:"
                        + $"spec={structuredPrimary?.ToString() ?? "<none>"}");
            }

            var legacyHits = Regex.Match(operation.ChineseText, @"伤害\s*(\d+)\s*次",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            Check("intrinsic_multi_hit", operation,
                CardEffectRules.IsEnemyDamage(operation)
                && (operation.ChineseText.Contains('X')
                    || legacyHits.Success && int.Parse(legacyHits.Groups[1].Value) > 1),
                CardEffectRules.IsEnemyDamage(operation)
                && spec.Values.FirstOrDefault(value => value.Id == "hits") is { } hitSlot
                && (hitSlot.Source != "fixed" || hitSlot.BaseValue + hitSlot.Offset > 1));
            Check("fatal_condition", operation,
                operation.Scope == OperationScope.ConditionalTrigger
                && operation.ChineseText.StartsWith("斩杀时", StringComparison.Ordinal),
                spec.Condition?.Kind == "fatal");
            Check("uses_x", operation, operation.ChineseText.Contains('X'),
                spec.Flags.Any(flag => flag is "uses_energy_x" or "uses_star_x")
                || spec.Values.Any(value => value.Source is "energy_x" or "star_x" or "special_x"));
            Check("for_each_exhaust", operation,
                operation.Template == "D:ForEachExhaustedStatus"
                || operation.Template == "C:forEach"
                    && operation.ChineseText is "每消耗一张牌。" or "每消耗一张手牌中的非攻击牌时。",
                spec.Trigger?.Kind is "for_each_exhausted_status" or "for_each_exhausted_card"
                    or "for_each_exhausted_non_attack");
            Check("combat_base_damage_growth", operation,
                operation.ChineseText.StartsWith("在本场战斗中，此卡的基础伤害增加", StringComparison.Ordinal),
                operation.Template == "I:IncreaseDamageThisCombat");
            Check("plating", operation, operation.ChineseText.Contains("覆甲", StringComparison.Ordinal),
                spec.Opcode == "apply_power" && spec.Variant == "plating");
            var legacyPermanentStrengthOrDexterity = !operation.ChineseText.Contains("本回合", StringComparison.Ordinal)
                && (operation.ChineseText.Contains("力量", StringComparison.Ordinal)
                    || operation.ChineseText.Contains("敏捷", StringComparison.Ordinal))
                && (operation.ChineseText.Contains("获得", StringComparison.Ordinal)
                    || operation.ChineseText.Contains("失去", StringComparison.Ordinal));
            var structuredPermanentStrengthOrDexterity = operation.Template is
                    "N:Dex" or "N:LoseDex" or "D:GainStrength" or "D:GainDexterity"
                    or "NCR:LoseStrength" or "R:GainStrength" or "R:EnemiesLoseStrength"
                    or "NCR:TargetLoseStrength" or "N:StrengthPerTargetVulnerable" or "T:XStrengthLoss"
                || spec.Opcode == "modify_block" && spec.Variant == "strength_scaled"
                || spec.Opcode == "apply_power"
                    && spec.Variant is "strength" or "strength_gain" or "strength_loss";
            Check("permanent_strength_or_dexterity", operation, legacyPermanentStrengthOrDexterity,
                structuredPermanentStrengthOrDexterity);
            var legacyPositivePermanentStat = operation.Template is "N:Dex" or "N:StrengthPerTargetVulnerable"
                    or "D:GainStrength" or "D:GainDexterity" or "D:GainFocus" or "R:GainStrength"
                || operation.Template == "N:Self"
                    && !operation.ChineseText.Contains("本回合", StringComparison.Ordinal)
                    && operation.ChineseText.Contains("获得", StringComparison.Ordinal)
                    && operation.ChineseText.Contains("力量", StringComparison.Ordinal);
            var structuredPositivePermanentStat = operation.Template is "N:Dex" or "N:StrengthPerTargetVulnerable"
                    or "D:GainStrength" or "D:GainDexterity" or "D:GainFocus" or "R:GainStrength"
                || spec.Opcode == "apply_power" && spec.Variant == "strength";
            Check("positive_permanent_stat", operation, legacyPositivePermanentStat,
                structuredPositivePermanentStat);
            var legacyEnemyStrengthReduction = operation.Template is
                    "N:AllTempStrengthLoss" or "CL:TargetLoseStrengthThisTurn"
                    or "R:EnemiesLoseStrengthThisTurn" or "R:TargetLoseStrengthThisTurn"
                    or "R:EnemiesLoseStrength" or "NCR:TargetLoseStrength"
                    or "NCR:TargetLoseStrengthThisTurn" or "T:XStrengthLoss"
                || operation.Template == "T:Apply"
                    && operation.ChineseText.Contains("失去", StringComparison.Ordinal)
                    && operation.ChineseText.Contains("力量", StringComparison.Ordinal)
                || operation.ChineseText.Contains("失去", StringComparison.Ordinal)
                    && operation.ChineseText.Contains("力量", StringComparison.Ordinal)
                    && operation.ChineseText.Contains("敌人", StringComparison.Ordinal);
            var structuredEnemyStrengthReduction = operation.Template is
                    "N:AllTempStrengthLoss" or "CL:TargetLoseStrengthThisTurn"
                    or "R:EnemiesLoseStrengthThisTurn" or "R:TargetLoseStrengthThisTurn"
                    or "R:EnemiesLoseStrength" or "NCR:TargetLoseStrength"
                    or "NCR:TargetLoseStrengthThisTurn" or "T:XStrengthLoss"
                || spec.Opcode == "apply_power"
                    && spec.Target != "self"
                    && spec.Variant is "strength_loss" or "strength_loss_this_turn";
            Check("enemy_strength_reduction", operation, legacyEnemyStrengthReduction,
                structuredEnemyStrengthReduction);

            var legacyHover = new HashSet<string>(StringComparer.Ordinal);
            void LegacyHover(string id, string token)
            {
                if (operation.ChineseText.Contains(token, StringComparison.Ordinal)) legacyHover.Add(id);
            }
            LegacyHover("ethereal", "虚无"); LegacyHover("exhaust", "消耗");
            LegacyHover("retain", "保留"); LegacyHover("sly", "奇巧");
            LegacyHover("vulnerable", "易伤"); LegacyHover("weak", "虚弱");
            LegacyHover("poison", "中毒"); LegacyHover("strength", "力量");
            LegacyHover("dexterity", "敏捷"); LegacyHover("thorns", "荆棘");
            LegacyHover("intangible", "无实体"); LegacyHover("plating", "覆甲");
            LegacyHover("focus", "集中"); LegacyHover("doom", "厄运"); LegacyHover("vigor", "活力");
            LegacyHover("fatal", "斩杀"); LegacyHover("replay", "重放");
            if (operation.ChineseText.Contains("变化", StringComparison.Ordinal)
                || operation.ChineseText.Contains("变为", StringComparison.Ordinal)) legacyHover.Add("transform");
            LegacyHover("summon", "召唤"); LegacyHover("card_reward", "卡牌奖励");
            LegacyHover("forge", "锻造"); LegacyHover("evoke", "激发");
            if (operation.Template == "D:ReplayEventCard") legacyHover.Add("replay");
            if (operation.Template == "I:AddCardReward") legacyHover.Add("card_reward");
            if (operation.Template is "R:Forge" or "R:ForgePerPriorHit") legacyHover.Add("forge");
            if (operation.Template.StartsWith("D:Channel", StringComparison.Ordinal)
                || operation.Template is "I:ProxyAtomic_Tempest" or "I:ProxyAtomic_Voltaic")
                legacyHover.Add("channel");
            if (operation.Template.Contains("Evoke", StringComparison.Ordinal)) legacyHover.Add("evoke");
            var structuredHover = CardEffectRules.HoverSemanticTags(operation);
            if (!legacyHover.SetEquals(structuredHover))
                failures.Add($"hover_semantics:{operation.Template}:{operation.ChineseText}:"
                    + $"legacy={string.Join(',', legacyHover.Order())}:"
                    + $"spec={string.Join(',', structuredHover.Order())}");
            if (operation.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger)
                Check("trigger_choice_context", operation,
                    LegacyTriggerSupportsChoiceContext(operation),
                    CardEffectRules.TriggerSupportsChoiceContextBySpec(operation));
            Check("operation_choice_context", operation,
                LegacyOperationNeedsChoiceContext(operation),
                CardEffectRules.OperationNeedsChoiceContextBySpec(operation));
            var legacyDamageSuppressing = operation.Template != "R:DoubleEitherXAtThreshold"
                && (CardEffectRules.IsDelayedEffect(operation)
                    || operation.Template.Contains(":If", StringComparison.Ordinal)
                    || operation.Template.Contains(":if", StringComparison.Ordinal)
                    || operation.ChineseText.StartsWith("如果", StringComparison.Ordinal)
                    || operation.ChineseText.StartsWith("若", StringComparison.Ordinal));
            Check("damage_type_suppressing_condition", operation, legacyDamageSuppressing,
                CardEffectRules.IsDamageTypeSuppressingConditionBySpec(operation));
            var legacyRandomGeneration = operation.Template is
                    "CL:AddRandomAttackToHand" or "CL:AddRandomColorlessToHand"
                    or "CL:AddRandomZeroCostCardsToHand" or "NCR:AddRandomEtherealCardToHand"
                    or "D:AddRandomPowerToHand" or "R:AddRandomColorlessToHand" or "I_CREATE_RANDOM_ATTACK"
                    or "I:ProxyAtomic_WhiteNoise" or "CL:ProxyAtomic_Discovery" or "I:ProxyAtomic_Quasar"
                    or "CL:ProxyAtomic_Splash" or "N:CreateCurrentCharacterCardInHand"
                || operation.Template == "I:Create"
                    && operation.ChineseText.Contains("随机攻击牌", StringComparison.Ordinal)
                || operation.Template == "N:Create"
                    && operation.ChineseText.Contains("随机", StringComparison.Ordinal)
                    && operation.ChineseText.Contains("加入手牌", StringComparison.Ordinal);
            Check("random_card_generation", operation, legacyRandomGeneration,
                CardEffectRules.IsRandomCardGenerationBySpec(operation.Template, spec));
            var trimmed = operation.ChineseText.TrimStart();
            Check("conditional_damage_payoff", operation,
                trimmed.StartsWith("这张牌就造成", StringComparison.Ordinal)
                || trimmed.StartsWith("对该目标造成", StringComparison.Ordinal),
                CardEffectRules.IsConditionalDamageVariantBySpec(operation));
            Check("event_enemy_damage", operation,
                operation.ChineseText.Contains("被命中的敌人", StringComparison.Ordinal),
                CardEffectRules.IsHitEnemyDamageVariantBySpec(operation));
            var legacyRequiresEnemy = operation.Scope == OperationScope.SingleEnemyOnly
                || operation.RequiresSingleTarget
                || operation.ChineseText.Contains("目标易伤", StringComparison.Ordinal)
                || operation.ChineseText.Contains("目标的易伤", StringComparison.Ordinal)
                || operation.ChineseText.Contains("该目标", StringComparison.Ordinal)
                || operation.ChineseText.Contains("该敌人", StringComparison.Ordinal)
                || operation.ChineseText.Contains("敌人身上每有", StringComparison.Ordinal);
            Check("requires_single_enemy", operation, legacyRequiresEnemy,
                CardEffectRules.RequiresSingleEnemyTargetBySpec(operation));
            Check("explicit_random_enemy", operation,
                operation.ChineseText.Contains("随机", StringComparison.Ordinal)
                && operation.ChineseText.Contains("敌人", StringComparison.Ordinal),
                CardEffectRules.UsesExplicitRandomEnemyTargetBySpec(operation));
            var legacyEventEnemy = operation.ChineseText.Contains("该目标", StringComparison.Ordinal)
                || operation.ChineseText.Contains("该敌人", StringComparison.Ordinal)
                || operation.ChineseText.Contains("那名敌人", StringComparison.Ordinal)
                || operation.ChineseText.Contains("被命中的敌人", StringComparison.Ordinal);
            Check("explicit_event_enemy", operation, legacyEventEnemy,
                CardEffectRules.UsesExplicitEventEnemyTargetBySpec(operation));
            var legacyDelayed = operation.Template is
                    "R:NextTurn" or "NCR:NextTurn" or "D:NextTurnsStart" or "CL:AtNextTurnStart"
                    or "CL:AfterTurns" or "N:NextTurnBlock" or "N:NextTurnEnergy" or "N:NextTurnDraw"
                    or "N:KeepBlockNextTurn" or "D:NextTurnEnergy" or "NCR:NextTurnEnergy"
                    or "I:CopySelectedCardNextTurn" or "I:DoubleAttackDamageNextTurn"
                || operation.ChineseText.Contains("在下个回合", StringComparison.Ordinal)
                || operation.ChineseText.Contains("下个回合开始时", StringComparison.Ordinal)
                || operation.ChineseText.Contains("在接下来的", StringComparison.Ordinal);
            Check("delayed_effect", operation, legacyDelayed, CardEffectRules.IsDelayedEffect(operation));
            Check("hp_loss_reference", operation,
                operation.ChineseText.Contains("失去生命", StringComparison.Ordinal),
                spec.Flags.Contains("hp_loss_reference"));
            Check("immediate_block_phrase", operation,
                operation.ChineseText.Contains("获得", StringComparison.Ordinal)
                && operation.ChineseText.Contains("格挡", StringComparison.Ordinal)
                && !operation.ChineseText.Contains("下回合", StringComparison.Ordinal)
                && !operation.ChineseText.Contains("永久增加", StringComparison.Ordinal),
                spec.Flags.Contains("immediate_block_gain"));
            Check("requires_hand_phrase", operation,
                operation.ChineseText.Contains("手牌中的", StringComparison.Ordinal)
                || operation.ChineseText.Contains("手牌中随机", StringComparison.Ordinal),
                spec.Flags.Contains("requires_hand_cards"));
            Check("replenishes_hand_phrase", operation,
                operation.ChineseText.Contains("加入你的手牌", StringComparison.Ordinal)
                || operation.ChineseText.Contains("加入手牌", StringComparison.Ordinal)
                || operation.ChineseText.Contains("置入手牌", StringComparison.Ordinal)
                || operation.ChineseText.Contains("放入手牌", StringComparison.Ordinal),
                spec.Flags.Contains("replenishes_hand"));
            Check("exhaust_pile_turn_end_trigger", operation,
                operation.Template == "C:after"
                && operation.ChineseText.Contains("回合结束时", StringComparison.Ordinal)
                && operation.ChineseText.Contains("消耗牌堆", StringComparison.Ordinal),
                CardEffectRules.IsExhaustPileTurnEndTrigger(operation));
            var legacyAttackCostReduction = operation.Template is
                    "C:whileInCombat" or "C:whileInCombatSkillCostReduction"
                && (operation.ChineseText.Contains("每打出过一张攻击牌", StringComparison.Ordinal)
                    || operation.ChineseText.Contains("每打出过一张技能牌", StringComparison.Ordinal))
                && operation.ChineseText.Contains("耗能减少", StringComparison.Ordinal);
            Check("attack_cost_reduction", operation, legacyAttackCostReduction,
                CardEffectRules.IsAttackCostReductionRuleBySpec(operation));
            Check("current_block_damage_modifier", operation,
                operation.Template == "M:value"
                || operation.Scope == OperationScope.Modifier
                    && operation.ChineseText.Contains("当前格挡", StringComparison.Ordinal)
                    && operation.ChineseText.Contains("伤害", StringComparison.Ordinal),
                CardEffectRules.IsCurrentBlockDamageModifierBySpec(operation));
            var legacyNextAttackTrigger = operation.Template is
                    "C:grantNextAttacksThisTurn" or "C:grantNextAttack"
                || operation.Template is "C:untilTurnEnd" or "C:for"
                    && operation.ChineseText.Contains("下一张攻击牌", StringComparison.Ordinal);
            Check("next_attack_grant_trigger", operation, legacyNextAttackTrigger,
                CardEffectRules.IsNextAttackGrantTriggerBySpec(operation));
            var legacyNextAttackPayoff = operation.Template is "I:ReplayAttack" or "I:SetCostZero"
                || operation.Scope == OperationScope.Modifier
                    && (operation.Template == "M:DamagePerExhaustCard"
                        || operation.Template == "M:base"
                            && (operation.ChineseText.Contains("名称含“打击”", StringComparison.Ordinal)
                                || operation.ChineseText.Contains("该敌人每有一层易伤", StringComparison.Ordinal))
                            && operation.ChineseText.Contains("额外造成", StringComparison.Ordinal)
                            && operation.ChineseText.Contains("点伤害", StringComparison.Ordinal));
            Check("next_attack_grant_payoff", operation, legacyNextAttackPayoff,
                CardEffectRules.IsNextAttackGrantPayoff(operation));
            var legacyNeedsLinked = operation.Scope is
                    OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger
                && operation.Template is not ("C:whileInCombat" or "C:whileInCombatSkillCostReduction")
                && !operation.ChineseText.Contains("伤害降低50%", StringComparison.Ordinal);
            Check("trigger_needs_linked_effect", operation, legacyNeedsLinked,
                CardEffectRules.TriggerNeedsLinkedEffect(operation));
            var legacyPersistentStat = operation.Template is "N:Dex" or "N:Thorns" or "N:Intangible"
                || operation.Template is "D:GainStrength" or "D:GainDexterity" or "D:GainFocus"
                    or "D:GainOrbSlots"
                || operation.Template == "R:KingsSwordHitsAllEnemies"
                || operation.Template == "N:Self"
                    && !operation.ChineseText.Contains("本回合", StringComparison.Ordinal)
                    && (operation.ChineseText.Contains("覆甲", StringComparison.Ordinal)
                        || operation.ChineseText.Contains("力量", StringComparison.Ordinal));
            var legacyPowerFoundation = operation.Scope is
                    OperationScope.AbilityTrigger or OperationScope.AbilityRule
                || legacyPersistentStat || CardEffectRules.IsRestrictedEffect(operation)
                || CardEffectRules.IsCopyThisCardToDiscard(operation);
            Check("persistent_power_foundation", operation, legacyPowerFoundation,
                CardEffectRules.IsPersistentPowerFoundation(operation));
            var legacyDamageBudget = CardEffectRules.IsPrintedDamageReward(operation)
                || CardEffectRules.IsCombatBaseDamageIncrease(operation)
                || operation.Scope == OperationScope.Modifier
                    && operation.ChineseText.Contains("伤害", StringComparison.Ordinal);
            Check("damage_budget_effect", operation, legacyDamageBudget,
                CardEffectRules.IsDamageBudgetEffect(operation));
            var legacyEffectFamily = CardEffectRules.IsEnemyDamage(operation) ? "damage"
                : operation.ChineseText.Contains("格挡", StringComparison.Ordinal) ? "block"
                : operation.ChineseText.Contains("抽", StringComparison.Ordinal)
                    && operation.ChineseText.Contains("牌", StringComparison.Ordinal) ? "draw"
                : operation.Template is "N:E" or "N:NextTurnEnergy" or "D:GainEnergy" or "D:NextTurnEnergy"
                    or "NCR:GainEnergy" or "NCR:NextTurnEnergy" or "R:GainEnergy" ? "energy"
                : operation.ChineseText.Contains("蓝星", StringComparison.Ordinal)
                    && operation.ChineseText.Contains("获得", StringComparison.Ordinal) ? "stars"
                : operation.ChineseText.Contains("中毒", StringComparison.Ordinal) ? "poison"
                : operation.ChineseText.Contains("灾厄", StringComparison.Ordinal) ? "doom"
                : operation.ChineseText.Contains("易伤", StringComparison.Ordinal) ? "vulnerable"
                : operation.ChineseText.Contains("虚弱", StringComparison.Ordinal) ? "weak"
                : operation.ChineseText.Contains("召唤", StringComparison.Ordinal) ? "summon"
                : operation.ChineseText.Contains("充能球", StringComparison.Ordinal) ? "orb"
                : operation.ChineseText.Contains("铸造", StringComparison.Ordinal) ? "forge"
                : operation.ChineseText.Contains("力量", StringComparison.Ordinal) ? "strength"
                : operation.ChineseText.Contains("敏捷", StringComparison.Ordinal) ? "dexterity"
                : operation.ChineseText.Contains("集中", StringComparison.Ordinal) ? "focus"
                : operation.ChineseText.Contains("覆甲", StringComparison.Ordinal) ? "plating"
                : operation.ChineseText.Contains("荆棘", StringComparison.Ordinal) ? "thorns"
                : DerivativeSlotCatalog.IsSlotOperation(operation.Template) ? "derivative"
                : string.Empty;
            if (legacyEffectFamily != CardEffectRules.EffectFamily(operation))
                failures.Add($"effect_family:{operation.Template}:{operation.ChineseText}:"
                    + $"legacy={legacyEffectFamily}:spec={CardEffectRules.EffectFamily(operation)}");
            if (NativeUpgradeValueModel.LegacyClassifyForAudit(operation)
                != NativeUpgradeValueModel.Classify(operation))
                failures.Add($"upgrade_family:{operation.Template}:{operation.ChineseText}:"
                    + $"legacy={NativeUpgradeValueModel.LegacyClassifyForAudit(operation)}:"
                    + $"spec={NativeUpgradeValueModel.Classify(operation)}");
            var legacyNeedsExternalCardSlot = operation.Template != "A:rulePlayedSkillsGainSly"
                && operation.Template != "M:TriggeredAttackDamagePercent"
                && !operation.Template.StartsWith("I:Create", StringComparison.Ordinal)
                && operation.Template != "I:UpgradeThatCard"
                && operation.Template != "CL:PutEventCardOnDrawTop"
                && operation.Template is not ("D:ReplayEventCard" or "D:ReturnEventCardToHand")
                && !operation.ChineseText.Contains("那张攻击牌", StringComparison.Ordinal)
                && !operation.ChineseText.Contains("对一名随机敌人打出这张牌", StringComparison.Ordinal)
                && !operation.ChineseText.Contains("将该攻击牌额外打出", StringComparison.Ordinal)
                && !operation.ChineseText.Contains("那张非攻击牌", StringComparison.Ordinal)
                && !operation.ChineseText.Contains("此牌", StringComparison.Ordinal)
                && !operation.ChineseText.Contains("这张牌", StringComparison.Ordinal)
                && !operation.ChineseText.Contains("那张技能牌", StringComparison.Ordinal)
                && (operation.ChineseText.Contains("该牌", StringComparison.Ordinal)
                    || operation.ChineseText.Contains("该技能", StringComparison.Ordinal)
                    || operation.ChineseText.Contains("该攻击", StringComparison.Ordinal)
                    || operation.ChineseText.Contains("那张牌", StringComparison.Ordinal)
                    || operation.Template.Contains("UpgradeThatCard", StringComparison.Ordinal)
                    || operation.ChineseText.Contains("exhausted(card)", StringComparison.Ordinal));
            Check("needs_external_card_slot", operation, legacyNeedsExternalCardSlot,
                CardEffectRules.NeedsExternalCardSlot(operation));
            var legacyPrintedDamageSlot = CardEffectRules.IsEnemyDamage(operation)
                && Regex.IsMatch(operation.ChineseText, @"\d+点伤害", RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100));
            var structuredPrintedDamageSlot = CardEffectRules.IsEnemyDamage(operation)
                && CardEffectRules.PrintedDamageValueSlot(operation) is not null;
            Check("printed_damage_slot", operation, legacyPrintedDamageSlot, structuredPrintedDamageSlot);
            var legacyPrintedBlockSlot = CardEffectRules.IsBeneficialEffect(operation)
                && Regex.IsMatch(operation.ChineseText, @"\d+点格挡", RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100));
            var structuredPrintedBlockSlot = CardEffectRules.IsBeneficialEffect(operation)
                && CardEffectRules.PrintedBlockValueSlot(operation) is not null;
            Check("printed_block_slot", operation, legacyPrintedBlockSlot, structuredPrintedBlockSlot);
            var legacyHasFrequency = EffectBalanceModel.TryLegacyCalibratedTriggerFrequencyForAudit(
                operation.Template, operation.ChineseText, out var legacyFrequency);
            var structuredHasFrequency = EffectBalanceModel.TryStructuredTriggerFrequencyForAudit(
                operation, out var structuredFrequency);
            if (legacyHasFrequency != structuredHasFrequency
                || legacyHasFrequency && Math.Abs(legacyFrequency - structuredFrequency) > 0.000001d)
                failures.Add($"trigger_frequency:{operation.Template}:{operation.ChineseText}:"
                    + $"legacy={(legacyHasFrequency ? legacyFrequency : double.NaN)}:"
                    + $"spec={(structuredHasFrequency ? structuredFrequency : double.NaN)}");
            var legacyAllNumbers = Number.Matches(operation.ChineseText).Cast<Match>()
                .Select(match => int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            var structuredExplicitFixed = spec.Values
                .Where(value => value.Explicit && value.Source == "fixed")
                .Select(value => value.BaseValue).ToArray();
            var legacyScalableNumbers = legacyAllNumbers.Where(value => value != 0).ToArray();
            var structuredScalableNumbers = structuredExplicitFixed.Where(value => value != 0).ToArray();
            if (!legacyScalableNumbers.SequenceEqual(structuredScalableNumbers))
                failures.Add($"explicit_fixed_sequence:{operation.Template}:{operation.ChineseText}:"
                    + $"legacy={string.Join(',', legacyScalableNumbers)}:"
                    + $"spec={string.Join(',', structuredScalableNumbers)}");
            var legacyFirstForValue = legacyAllNumbers.FirstOrDefault(1);
            var structuredFirstForValue = structuredExplicitFixed.FirstOrDefault(1);
            if (legacyFirstForValue != structuredFirstForValue)
                failures.Add($"effect_value_first:{operation.Template}:{operation.ChineseText}:"
                    + $"legacy={legacyFirstForValue}:spec={structuredFirstForValue}");
            var legacyHitMatch = Regex.Match(operation.ChineseText, @"(\d+)次",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            var legacyEffectHits = legacyHitMatch.Success
                ? Math.Clamp(int.Parse(legacyHitMatch.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture), 1, 8) : 1;
            var structuredEffectHits = Math.Clamp(spec.Values.FirstOrDefault(value =>
                value.Id is "hits" or "repeat_count")?.BaseValue ?? 1, 1, 8);
            if (legacyEffectHits != structuredEffectHits)
                failures.Add($"effect_value_hits:{operation.Template}:{operation.ChineseText}:"
                    + $"legacy={legacyEffectHits}:spec={structuredEffectHits}");
        }
        if (failures.Count > 0)
            throw new InvalidOperationException("RuntimeSpec semantic-equivalence audit failed:\n"
                + string.Join("\n", failures));
    }

    private static bool LegacyTriggerSupportsChoiceContext(GeneratorOperation trigger)
    {
        if (trigger.Template == "A:turnStart") return true;
        if (trigger.Template is "NCR:NextTurn" or "R:NextTurn" or "D:NextTurnsStart"
            or "A:whenEnergyCostAtLeast" or "A:whenEnergySpent" or "A:whenOneStarSpent"
            or "A:whenOstyLosesHp" or "D:ForEachEnergySpentThisTurn") return false;
        return !trigger.ChineseText.Contains("获得格挡", StringComparison.Ordinal)
            && !trigger.ChineseText.Contains("失去生命", StringComparison.Ordinal)
            && !trigger.ChineseText.Contains("生成一张", StringComparison.Ordinal)
            && !trigger.ChineseText.Contains("花费或获得蓝星", StringComparison.Ordinal);
    }

    private static bool LegacyOperationNeedsChoiceContext(GeneratorOperation operation)
    {
        if (CardEffectRules.IsAtomicChoiceProxy(operation.Template)
            || operation.Template is "N_SELECT_HAND_CARD" or "N_SELECT_HAND_ATTACK" or "I:Upgrade"
            or "N:Discard" or "CL:TransformSelectedHandCards" or "CL:ExhaustUpToHandCards"
            or "CL:MoveSelectedSkillDrawToHand" or "CL:MoveSelectedAttackDrawToHand"
            or "CL:ChooseFromRandomDrawCards" or "CL:ChooseDrawCardToHand"
            or "R:MoveDiscardCardToDrawTop" or "R:PlaySelectedSkillMultipleTimes"
            or "R:PutSelectedHandCardsOnDraw" or "R:PutSelectedHandCardOnDraw"
            or "R:CopySelectedColorlessCard" or "NCR:ExhaustSelectedDrawCard"
            or "NCR:MoveDiscardCardToHand" or "D:MoveDiscardCardToHand"
            or "I:GrantSlyToHandSkillThisTurn" or "I:CopySelectedCardNextTurn"
            or "I:PlayTopCardAndExhaust" or "I:PlayTopXCards" or "CL:PlayTopDrawCard"
            or "D:AutoPlayRandomAttackFromDraw" or "I:AutoPlayRandomAttackFromHand"
            or "I:PlayAtRandomEnemy") return true;
        return operation.Template is "N:Exhaust" or "N_EXHAUST_SELECTED" or "D:ExhaustSelectedHandCard"
                && operation.ChineseText.Contains("手牌", StringComparison.Ordinal)
                && operation.ChineseText.Contains("牌", StringComparison.Ordinal)
                && !operation.ChineseText.Contains("随机", StringComparison.Ordinal)
                && !operation.ChineseText.Contains("所有", StringComparison.Ordinal)
            || operation.ChineseText is "将弃牌堆中的一张牌放到抽牌堆顶部。" or "升级手牌中的一张牌。"
            || operation.ChineseText.Contains("选择", StringComparison.Ordinal);
    }

    internal static string NumericCoverageReport()
    {
        var output = new StringBuilder("character\tultimate\trecipe\ttemplate\ttext\tcompiled\topcode\tvariant\tvalues\tfailure\n");
        var catalogs = Enum.GetValues<GeneratedCharacter>()
            .Select(character => (Character: character, Ultimate: false,
                Catalog: CharacterComponentCatalogs.Get(character, unlockComponentRoles: false)))
            .Append((Character: GeneratedCharacter.Ironclad, Ultimate: true,
                Catalog: CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad, unlockComponentRoles: true)));
        foreach (var entry in catalogs)
        foreach (var recipe in entry.Catalog.Recipes.OrderBy(recipe => recipe.Id, StringComparer.Ordinal))
        foreach (var atom in recipe.Atoms)
        {
            var operation = new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
                new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget);
            if (!IsNumericBatch(operation)) continue;
            var success = TryCompile(operation, out var spec, out var failure);
            output.Append(entry.Character).Append('\t').Append(entry.Ultimate).Append('\t')
                .Append(recipe.Id).Append('\t').Append(atom.Template).Append('\t')
                .Append(atom.ChineseText.Replace("\r", "\\r", StringComparison.Ordinal)
                    .Replace("\n", "\\n", StringComparison.Ordinal).Replace('\t', ' ')).Append('\t')
                .Append(success).Append('\t').Append(spec?.Opcode ?? string.Empty).Append('\t')
                .Append(spec?.Variant ?? string.Empty).Append('\t')
                .Append(spec is null ? string.Empty : string.Join(',', spec.Values.Select(value =>
                    $"{value.Id}={value.BaseValue}@{value.Source}+{value.Offset}")))
                .Append('\t').Append(failure).Append('\n');
        }
        return output.ToString();
    }

    internal static string CoverageReport()
    {
        var output = new StringBuilder("character\tultimate\trecipe\ttemplate\ttext\tcompiled\topcode\tvariant\tsignature\tfailure\n");
        var catalogs = Enum.GetValues<GeneratedCharacter>()
            .Select(character => (Character: character, Ultimate: false,
                Catalog: CharacterComponentCatalogs.Get(character, unlockComponentRoles: false)))
            .Append((Character: GeneratedCharacter.Ironclad, Ultimate: true,
                Catalog: CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad, unlockComponentRoles: true)));
        foreach (var entry in catalogs)
        foreach (var recipe in entry.Catalog.Recipes.OrderBy(recipe => recipe.Id, StringComparer.Ordinal))
        foreach (var atom in recipe.Atoms.Where(atom => IsBatchOne(atom.Template)))
        {
            var operation = new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
                new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget);
            var success = TryCompile(operation, out var spec, out var failure);
            output.Append(entry.Character).Append('\t').Append(entry.Ultimate).Append('\t')
                .Append(recipe.Id).Append('\t').Append(atom.Template).Append('\t')
                .Append(atom.ChineseText.Replace("\r", "\\r", StringComparison.Ordinal)
                    .Replace("\n", "\\n", StringComparison.Ordinal).Replace('\t', ' ')).Append('\t')
                .Append(success).Append('\t')
                .Append(spec?.Opcode ?? string.Empty).Append('\t').Append(spec?.Variant ?? string.Empty).Append('\t')
                .Append(spec?.StableSignature() ?? string.Empty).Append('\t').Append(failure).Append('\n');
        }
        return output.ToString();
    }

    private static OperationRuntimeSpec? CompileTargetPower(GeneratorOperation operation)
    {
        var text = operation.ChineseText;
        if (text.Contains("易伤层数翻倍", StringComparison.Ordinal))
            return Spec("apply_power", "vulnerable_double", "selected_enemy");
        if (text.Contains("易伤", StringComparison.Ordinal))
            return Spec("apply_power", "vulnerable", "selected_enemy", values: [Amount(operation, 1)]);
        if (text.Contains("虚弱", StringComparison.Ordinal))
            return Spec("apply_power", "weak", "selected_enemy", values: [Amount(operation, 1)]);
        if (text.Contains("失去", StringComparison.Ordinal) && text.Contains("力量", StringComparison.Ordinal))
            return Spec("apply_power", text.Contains("本回合", StringComparison.Ordinal)
                    ? "strength_loss_this_turn" : "strength_loss",
                "selected_enemy", values: [Amount(operation, 1)]);
        if (text.Contains("力量", StringComparison.Ordinal))
            return Spec("apply_power", "strength_gain", "selected_enemy", values: [Amount(operation, 1)]);
        return null;
    }

    private static OperationRuntimeSpec? CompileSelfPower(GeneratorOperation operation)
    {
        var text = operation.ChineseText;
        if (operation.Template == "N:StrengthPerTargetVulnerable"
            || text.Contains("目标敌人身上每有一层易伤", StringComparison.Ordinal)
            || text.Contains("获得等同于目标易伤层数", StringComparison.Ordinal))
            return Spec("apply_power", "strength_per_target_vulnerable", "self",
                flags: ["requires_selected_enemy"], values: [Amount(operation, 1)]);
        if (text.Contains("覆甲", StringComparison.Ordinal))
            return Spec("apply_power", "plating", "self", values: [Amount(operation, 1)]);
        if (text.Contains("力量", StringComparison.Ordinal))
            return Spec("apply_power", text.Contains("本回合", StringComparison.Ordinal)
                    ? "strength_this_turn" : "strength",
                "self", values: [Amount(operation, 1)]);
        return null;
    }

    private static OperationRuntimeSpec? CompileCreate(GeneratorOperation operation)
    {
        var text = operation.ChineseText;
        if (operation.Template == "N:CreateCurrentCharacterCardInHand"
            || operation.Template == "N:Create"
            && text.Contains("当前角色", StringComparison.Ordinal)
            && text.Contains("随机", StringComparison.Ordinal)
            && text.Contains("牌", StringComparison.Ordinal)
            && text.Contains("手牌", StringComparison.Ordinal))
            return Spec("create_card", "current_character_random", "generated_card",
                sourceZone: "current_character_pool", destinationZone: "hand",
                flags: text.Contains("升级过", StringComparison.Ordinal) ? ["upgrade_generated"] : [],
                values: [Count(1, explicitValue: false)]);
        if (text.Contains("此牌", StringComparison.Ordinal) && text.Contains("复制", StringComparison.Ordinal))
            return Spec("create_copy", "this_card", "self_card", destinationZone: "discard",
                values: [Count(1, explicitValue: false)]);
        if (text.Contains("那张攻击牌", StringComparison.Ordinal))
            return Spec("create_copy", "referenced_attack", "referenced_card", destinationZone: "hand",
                cardFilter: "attack", values: [Count(1, explicitValue: false)]);
        return null;
    }

    private static OperationRuntimeSpec? CompileMove(GeneratorOperation operation)
    {
        var text = operation.ChineseText;
        if (text.Contains("弃牌堆", StringComparison.Ordinal)
            && text.Contains("抽牌堆顶部", StringComparison.Ordinal))
            return Spec("move_card", "selected", "selected_card", sourceZone: "discard",
                destinationZone: "draw", flags: ["destination_top"],
                values: [Count(1, explicitValue: false)]);
        if (text.Contains("弃牌堆", StringComparison.Ordinal)
            && text.Contains("随机攻击牌", StringComparison.Ordinal)
            && text.Contains("手牌", StringComparison.Ordinal))
            return Spec("move_card", "random", "random_card", sourceZone: "discard",
                destinationZone: "hand", cardFilter: "attack",
                values: [Count(1, explicitValue: false)]);
        return null;
    }

    private static OperationRuntimeSpec? CompileExhaust(GeneratorOperation operation)
    {
        var text = operation.ChineseText;
        if (text.Contains("所有非攻击牌", StringComparison.Ordinal))
            return Spec("exhaust_card", "all", "all_cards", sourceZone: "hand", cardFilter: "non_attack");
        if (text.Contains("所有手牌", StringComparison.Ordinal))
            return Spec("exhaust_card", "all", "all_cards", sourceZone: "hand");
        if (text.Contains("那张技能牌", StringComparison.Ordinal))
            return Spec("exhaust_card", "referenced", "referenced_card", sourceZone: "hand",
                cardFilter: "skill", values: [Count(1, explicitValue: false)]);
        if (text.Contains("随机", StringComparison.Ordinal))
            return Spec("exhaust_card", "random", "random_card", sourceZone: "hand",
                cardFilter: text.Contains("攻击牌", StringComparison.Ordinal) ? "attack" : "any",
                flags: Number.IsMatch(text) ? ["explicit_numeric"] : [],
                values: [Count(FirstNumber(text, 1), Number.IsMatch(text))]);
        if (text.Contains("手牌", StringComparison.Ordinal) && text.Contains("牌", StringComparison.Ordinal))
            return Spec("exhaust_card", "selected", "selected_card", sourceZone: "hand",
                flags: new[]
                    {
                        "requires_player_choice",
                        Number.IsMatch(text) ? "explicit_numeric" : null,
                        text.Contains("至多", StringComparison.Ordinal)
                        || text.Contains("任意数量", StringComparison.Ordinal) ? "up_to" : null
                    }.Where(flag => flag is not null).Select(flag => flag!).ToArray(),
                values: [Count(FirstNumber(text, 1), Number.IsMatch(text))]);
        return null;
    }

    private static OperationRuntimeSpec CompileMultiDamage(GeneratorOperation operation)
    {
        var matches = Number.Matches(operation.ChineseText).Cast<Match>().ToArray();
        var damageUsesX = SpecialXCardConverter.ValueUsesSpecialX(operation, 0)
            || Regex.IsMatch(operation.ChineseText, @"造成X点伤害", RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        var hitsUsesX = SpecialXCardConverter.ValueUsesSpecialX(operation, 1)
            || Regex.IsMatch(operation.ChineseText, @"伤害X(?:\+1)?次", RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        var damage = damageUsesX
            ? new RuntimeValueSlot("damage", 0, SpecialXCardConverter.ValueUsesSpecialX(operation, 0)
                ? "special_x" : "energy_x")
            : new RuntimeValueSlot("damage", matches.FirstOrDefault()?.Value is { } first
                ? int.Parse(first, System.Globalization.CultureInfo.InvariantCulture) : 0);
        var fixedHits = Regex.Match(operation.ChineseText, @"伤害(\d+)次", RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        var hits = hitsUsesX
            ? new RuntimeValueSlot("hits", 0, SpecialXCardConverter.ValueUsesSpecialX(operation, 1)
                ? "special_x" : "energy_x", operation.ChineseText.Contains("X+1", StringComparison.Ordinal) ? 1 : 0)
            : new RuntimeValueSlot("hits", fixedHits.Success
                    ? int.Parse(fixedHits.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 1,
                Explicit: fixedHits.Success);
        return Spec("deal_damage", operation.Template == "N:AllD" ? "all" : "random",
            operation.Template == "N:AllD" ? "all_enemies" : "random_enemy", values: [damage, hits]);
    }

    private static OperationRuntimeSpec CompileTargetDamage(GeneratorOperation operation)
    {
        var damageUsesX = SpecialXCardConverter.ValueUsesSpecialX(operation, 0)
            || Regex.IsMatch(operation.ChineseText, @"造成X点伤害", RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        var damage = damageUsesX
            ? new RuntimeValueSlot("damage", 0, SpecialXCardConverter.ValueUsesSpecialX(operation, 0)
                ? "special_x" : "energy_x", LegacyXOffset(operation.ChineseText))
            : new RuntimeValueSlot("damage", FirstNumber(operation.ChineseText, 0));
        var usesXHits = operation.Template is "T:DX" or "T:D_EnergyX";
        var values = new List<RuntimeValueSlot> { damage };
        if (usesXHits)
            values.Add(new RuntimeValueSlot("hits", 0, "energy_x", LegacyXOffset(operation.ChineseText)));
        var thresholdMatch = Regex.Match(operation.ChineseText, @"至少(?:为)?(\d+)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (thresholdMatch.Success)
            values.Add(new RuntimeValueSlot("threshold",
                int.Parse(thresholdMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                Upgradable: false));
        var target = operation.ChineseText.Contains("被命中的敌人", StringComparison.Ordinal)
            ? "event_enemy" : "selected_enemy";
        var flags = new[]
        {
            operation.ChineseText.Contains("翻倍", StringComparison.Ordinal) ? "legacy_inline_double_x" : null,
            operation.ChineseText.TrimStart().StartsWith("这张牌就造成", StringComparison.Ordinal)
            || operation.ChineseText.TrimStart().StartsWith("对该目标造成", StringComparison.Ordinal)
                ? "conditional_damage_payoff" : null
        }.Where(flag => flag is not null).Select(flag => flag!).ToArray();
        return Spec("deal_damage", operation.Template == "T:D_EnergyX" ? "selected_energy_x_threshold" : "selected",
            target, flags: flags, values: values);
    }

    private static OperationRuntimeSpec CompileRandomPoison(GeneratorOperation operation)
    {
        var matches = Number.Matches(operation.ChineseText).Cast<Match>().ToArray();
        var amount = matches.FirstOrDefault()?.Value is { } first
            ? int.Parse(first, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var hitsMatch = Regex.Match(operation.ChineseText, @"中毒(\d+)次", RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        var hits = hitsMatch.Success
            ? int.Parse(hitsMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
        return Spec("apply_power", "poison", "random_enemy", values:
            [new RuntimeValueSlot("amount", amount),
                new RuntimeValueSlot("hits", hits, Explicit: hitsMatch.Success)]);
    }

    private static OperationRuntimeSpec CompileProxyXDamage(GeneratorOperation operation)
    {
        var source = operation.Template.StartsWith("N:ProxyDamage_Atomic_StarX_", StringComparison.Ordinal)
            ? "star_x" : "energy_x";
        var target = source == "star_x" ? "random_enemy" : "selected_enemy";
        var variant = source == "star_x" ? "random_star_x_hits" : "selected_energy_x_hits";
        var damage = FirstNumber(operation.ChineseText, 0);
        return Spec("deal_damage", variant, target, values:
            [new RuntimeValueSlot("damage", damage),
                new RuntimeValueSlot("hits", 0, source, LegacyXOffset(operation.ChineseText))]);
    }

    private static OperationRuntimeSpec CompileEnergyXCondition(GeneratorOperation operation) =>
        Spec("condition", "energy_x_at_least", "self", flags: ["uses_energy_x"],
            values: [new RuntimeValueSlot("threshold", FirstNumber(operation.ChineseText, 1), Upgradable: false)],
            condition: new RuntimeConditionSpec("energy_x_at_least", "self", "threshold"));

    private static OperationRuntimeSpec CompileSimpleAction(GeneratorOperation operation)
    {
        RuntimeValueSlot Value(string id, int fallback = 1)
        {
            var usesSpecialX = SpecialXCardConverter.ValueUsesSpecialX(operation,
                SpecialXCardConverter.PrimaryValueIndex(operation));
            var usesEnergyX = !usesSpecialX && operation.ChineseText.Contains('X');
            return usesSpecialX || usesEnergyX
                ? new RuntimeValueSlot(id, 0, usesSpecialX ? "special_x" : "energy_x",
                    LegacyXOffset(operation.ChineseText))
                : new RuntimeValueSlot(id, FirstNumber(operation.ChineseText, fallback));
        }

        return operation.Template switch
        {
            "N:B" or "N_BLOCK" => Spec("gain_block", "immediate", "self", values: [Value("block")]),
            "N:Draw" => Spec("draw_cards", "immediate", "self", values: [Value("draw")]),
            "N:E" or "D:GainEnergy" or "NCR:GainEnergy" or "R:GainEnergy" =>
                Spec("gain_energy", "immediate", "self", values: [Value("energy")]),
            "N:NextTurnEnergy" or "D:NextTurnEnergy" or "NCR:NextTurnEnergy" =>
                Spec("gain_energy", "next_turn", "self", values: [Value("energy")]),
            "R:GainStars" => Spec("gain_stars", "immediate", "self", values: [Value("stars")]),
            "N:HP-" => Spec("lose_hp", "immediate", "self", values: [Value("hp_loss")]),
            "N:Discard" => Spec("discard_card", "selected", "selected_card", sourceZone: "hand",
                flags: Number.IsMatch(operation.ChineseText) ? ["explicit_numeric"] : [],
                values: [Value("count") with { Explicit = Number.IsMatch(operation.ChineseText) }]),
            "N:DiscardAll" => Spec("discard_card", "all", "all_cards", sourceZone: "hand"),
            "N:Heal" or "N_HEAL" => Spec("heal", "immediate", "self", values: [Value("amount")]),
            "I:GainMaxHp" => Spec("gain_max_hp", "immediate", "self", values: [Value("amount")]),
            "N:LoseDex" => Spec("apply_power", "dexterity_loss", "self", values: [Value("amount")]),
            "D:LoseFocus" => Spec("apply_power", "focus_loss", "self", values: [Value("amount")]),
            "D:LoseTemporaryFocus" => Spec("apply_power", "focus_loss_this_turn", "self",
                values: [Value("amount")]),
            "D:LoseOrbSlots" => Spec("modify_orb_slots", "loss", "self", values: [Value("amount")]),
            "NCR:LoseStrength" => Spec("apply_power", "strength_loss", "self", values: [Value("amount")]),
            "NCR:ApplySelfDoom" => Spec("apply_power", "doom", "self", values: [Value("amount")]),
            _ => throw new InvalidOperationException($"Unsupported simple action {operation.Template}.")
        };
    }

    private static OperationRuntimeSpec CompileCardPayment(GeneratorOperation operation)
    {
        var text = operation.ChineseText;
        var flags = new[]
            {
                Number.IsMatch(text) ? "explicit_numeric" : null,
                text.Contains("至多", StringComparison.Ordinal)
                || text.Contains("任意数量", StringComparison.Ordinal) ? "up_to" : null
            }.Where(flag => flag is not null).Select(flag => flag!).ToArray();
        var count = new RuntimeValueSlot("count", FirstNumber(text, 1),
            Explicit: Number.IsMatch(text));
        return operation.Template switch
        {
            "N_EXHAUST_SELECTED" or "D:ExhaustSelectedHandCard" =>
                Spec("exhaust_card", "selected", "selected_card", sourceZone: "hand", flags: flags,
                    values: [count]),
            "NCR:ExhaustSelectedDrawCard" =>
                Spec("exhaust_card", "selected", "selected_card", sourceZone: "draw", flags: flags,
                    values: [count]),
            "R:DiscardTopOfDraw" =>
                Spec("discard_card", "top", "top_card", sourceZone: "draw", destinationZone: "discard",
                    flags: flags, values: [count]),
            _ => throw new InvalidOperationException($"Unsupported card payment {operation.Template}.")
        };
    }

    private static OperationRuntimeSpec CompileRandomZeroCostCards(GeneratorOperation operation) =>
        Spec("create_card", "random_zero_cost", "generated_card", "current_character_pool", "hand",
            "cost_zero", values: [Count(FirstNumber(operation.ChineseText, 1)),
                new RuntimeValueSlot("cost_marker", 0, Upgradable: false)]);

    private static OperationRuntimeSpec CompileCostZero(GeneratorOperation operation, string target) =>
        Spec("modify_cost", "set_zero", target, flags: ["set_cost_zero"],
            values: [new RuntimeValueSlot("cost_marker", 0, Upgradable: false)]);

    private static OperationRuntimeSpec CompileCardChoice(GeneratorOperation operation)
    {
        var numbers = Number.Matches(operation.ChineseText).Cast<Match>()
            .Select(match => int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var variant = operation.Template switch
        {
            "I:ProxyAtomic_Quasar" => "random_colorless",
            "CL:ProxyAtomic_Splash" => "random_other_character_attack",
            _ => "random_current_character"
        };
        var source = operation.Template switch
        {
            "I:ProxyAtomic_Quasar" => "colorless_pool",
            "CL:ProxyAtomic_Splash" => "other_character_pools",
            _ => "current_character_pool"
        };
        var filter = operation.Template == "CL:ProxyAtomic_Splash" ? "attack" : "any";
        var flags = operation.Template is "CL:ProxyAtomic_Discovery" or "CL:ProxyAtomic_Splash"
            ? new[] { "set_cost_zero_this_turn" }
            : Array.Empty<string>();
        return Spec("choose_generated_card", variant, "generated_card", source, "hand", filter, flags,
            [new RuntimeValueSlot("choices", numbers.ElementAtOrDefault(0)),
                new RuntimeValueSlot("picks", Math.Max(1, numbers.ElementAtOrDefault(1)), Upgradable: false)]);
    }

    private static OperationRuntimeSpec CompileDrawAndDiscardNonZero(GeneratorOperation operation) =>
        Spec("draw_and_discard", "nonzero_cost", "self", cardFilter: "cost_nonzero",
            values: [new RuntimeValueSlot("draw", FirstNumber(operation.ChineseText, 1)),
                new RuntimeValueSlot("cost_marker", 0, Upgradable: false)]);

    private static OperationRuntimeSpec CompileDrawAndBlockIfSkill(GeneratorOperation operation)
    {
        var drawUsesX = SpecialXCardConverter.ValueUsesSpecialX(operation, 0)
            || Regex.IsMatch(operation.ChineseText, @"抽X(?:\+1)?张牌", RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        var drawMatch = Regex.Match(operation.ChineseText, @"抽(\d+)张牌", RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        var blockMatch = Regex.Match(operation.ChineseText, @"获得(\d+)点格挡", RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        var draw = drawUsesX
            ? new RuntimeValueSlot("draw", 0, SpecialXCardConverter.ValueUsesSpecialX(operation, 0)
                ? "special_x" : "energy_x", LegacyXOffset(operation.ChineseText))
            : new RuntimeValueSlot("draw", drawMatch.Success
                ? int.Parse(drawMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0);
        var block = new RuntimeValueSlot("block", blockMatch.Success
            ? int.Parse(blockMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0);
        return Spec("draw_cards", "block_if_skill_drawn", "self", values: [draw, block]);
    }

    private static OperationRuntimeSpec CompileDoomThreshold(GeneratorOperation operation)
    {
        var numbers = Number.Matches(operation.ChineseText).Cast<Match>()
            .Select(match => int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (operation.Template == "NCR:ForEachDoomThreshold")
            return Spec("trigger", "doom_threshold", "selected_enemy",
                values: [new RuntimeValueSlot("threshold", numbers.ElementAtOrDefault(0), Upgradable: false)],
                trigger: new RuntimeTriggerSpec("doom_threshold", "immediate", "threshold"));

        // Schema <=7 could store the original unsplit line in the payoff operation. Preserve both values so the
        // live migration path can execute it without reading the localized sentence again.
        return numbers.Length >= 2
            ? Spec("modify_power", "doom_per_threshold", "selected_enemy", values:
                [new RuntimeValueSlot("threshold", numbers[0], Upgradable: false),
                    new RuntimeValueSlot("bonus", numbers[1])])
            : Spec("modify_power", "doom_per_threshold", "selected_enemy",
                values: [new RuntimeValueSlot("bonus", numbers.ElementAtOrDefault(0))]);
    }

    private static OperationRuntimeSpec? CompileBaseModifier(GeneratorOperation operation)
    {
        var numbers = Number.Matches(operation.ChineseText).Cast<Match>()
            .Select(match => int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        // Older validation fixtures and a few migrated snapshots used M:base for the flat-hit modifier that is
        // normally stored as M:repeat. Compile the semantic shape, but do not broaden next-Attack grant legality.
        if (operation.ChineseText.Contains("额外造成", StringComparison.Ordinal)
            && operation.ChineseText.Contains("次伤害", StringComparison.Ordinal))
            return Spec("modify_hits", "flat_extra", "selected_enemy",
                values: [new RuntimeValueSlot("extra_hits", numbers.FirstOrDefault())]);
        if (operation.ChineseText.Contains("力量", StringComparison.Ordinal)
            && operation.ChineseText.Contains("额外获得", StringComparison.Ordinal)
            && operation.ChineseText.Contains("格挡", StringComparison.Ordinal))
            return Spec("modify_block", "strength_scaled", "self", values:
                [new RuntimeValueSlot("strength_interval", numbers.ElementAtOrDefault(0), Upgradable: false),
                    new RuntimeValueSlot("block_per_interval", numbers.ElementAtOrDefault(1))]);
        if (operation.ChineseText.Contains("易伤", StringComparison.Ordinal)
            && operation.ChineseText.Contains("伤害", StringComparison.Ordinal))
            return Spec("modify_damage", "vulnerable_scaled", "selected_enemy",
                values: [new RuntimeValueSlot("damage_per_vulnerable", numbers.LastOrDefault())]);
        if (operation.ChineseText.Contains("名称含", StringComparison.Ordinal)
            && operation.ChineseText.Contains("打击", StringComparison.Ordinal))
            return Spec("modify_damage", "strike_count_scaled", "selected_enemy",
                values: [new RuntimeValueSlot("damage_per_strike", numbers.LastOrDefault())]);
        if (operation.ChineseText.Contains("消耗牌堆", StringComparison.Ordinal)
            && operation.ChineseText.Contains("伤害", StringComparison.Ordinal))
            return Spec("modify_damage", "exhaust_pile_scaled", "selected_enemy",
                values: [new RuntimeValueSlot("damage_per_card", numbers.LastOrDefault())]);
        if (operation.ChineseText.Contains("当前格挡", StringComparison.Ordinal)
            && operation.ChineseText.Contains("伤害", StringComparison.Ordinal))
            return Spec("modify_damage", "current_block", "self");
        return null;
    }

    private static OperationRuntimeSpec? CompileRepeatModifier(GeneratorOperation operation)
    {
        if (operation.ChineseText.Contains("失去过一次生命", StringComparison.Ordinal))
            return Spec("modify_hits", "hp_loss_scaled", "selected_enemy",
                values: [ScalarValue(operation, "hits_per_hp_loss_event", 1)]);
        if (operation.ChineseText.Contains("额外造成", StringComparison.Ordinal))
            return Spec("modify_hits", "flat_extra", "selected_enemy",
                values: [ScalarValue(operation, "extra_hits", 1)]);
        return null;
    }

    private static OperationRuntimeSpec? CompileAbilityTrigger(GeneratorOperation operation)
    {
        var kind = operation.Template switch
        {
            "A:turnStart" => "turn_start",
            "A:turnEnd" => "turn_end",
            "A:firstCardPlayedEachTurn" => "first_card_played_each_turn",
            "A:firstZeroCostAttackPlayedEachTurn" => "first_zero_cost_attack_played_each_turn",
            "A:whenCardDrawnDuringTurn" => "card_drawn_during_turn",
            "A:whenCardPlayed" => "card_played",
            "A:whenAttackDealsDamage" => operation.ChineseText.Contains("一名敌人", StringComparison.Ordinal)
                ? "attack_damaged_enemy" : "attack_dealt_damage",
            "A:whenCardGenerated" => "card_generated",
            "A_WHEN_GAIN_BLOCK" => "block_gained",
            "A:whenDebuffApplied" => "enemy_debuff_applied",
            "A:whenDoomApplied" => "doom_applied",
            "A:whenEnergyCostAtLeast" => "energy_cost_at_least_card_played",
            "NCR:WheneverHighCostCardPlayed" => "energy_cost_at_least_card_played",
            "A:whenEtherealDrawn" => "ethereal_card_drawn",
            "A:whenEtherealPlayed" => "ethereal_card_played",
            "A:whenFirstStatusDrawn" => "first_status_drawn_each_turn",
            "A:whenLightningEvoked" => "lightning_orb_evoked",
            "A:whenOstyLosesHp" => "osty_hp_lost",
            "A:whenPowerPlayed" => "power_played",
            "A:whenSkillPlayed" => "skill_played",
            "A:whenSoulPlayed" => "derivative_played",
            "A:whenStarsChanged" => "stars_spent_or_gained",
            "A:whenStatusGenerated" => "status_generated",
            "NCR:FirstAttackPlayedEachTurn" => "first_attack_played_each_turn",
            "A:whenEnergySpent" => "energy_spent_threshold",
            "A:whenOneStarSpent" => "stars_spent_threshold",
            // These Necrobinder triggers are normally assembled as conditional triggers, but old snapshots and
            // validation fixtures may carry the ability-trigger scope. Their stable event identity is independent
            // of that legacy routing detail.
            "NCR:WheneverCardPlayedThisTurn" => "card_played",
            "NCR:WheneverOstyAttacksTargetThisTurn" => "osty_attacks_target",
            "CL:AfterTurns" => "turns_elapsed",
            "CL:EveryCardsDrawn" => "cards_drawn_threshold",
            "CL:EveryCardsPlayedThisTurn" => "cards_played_this_turn_threshold",
            "CL:FirstAttackOrSkillEachTurn" => "first_attack_or_skill_each_turn",
            "CL:WheneverAttackPlayed" => "attack_played",
            "CL:WheneverDrawPileShuffled" => "draw_pile_shuffled",
            "A:when" => CompileLegacyWhenKind(operation.ChineseText),
            _ => null
        };
        if (kind is null) return null;
        var lifetime = kind is "turns_elapsed" ? "delayed"
            : kind.Contains("each_turn", StringComparison.Ordinal) ? "combat"
            : "combat";
        var hasThreshold = operation.Template is "A:whenEnergyCostAtLeast" or "NCR:WheneverHighCostCardPlayed"
            or "A:whenEnergySpent"
            or "A:whenOneStarSpent" or "CL:AfterTurns" or "CL:EveryCardsDrawn"
            or "CL:EveryCardsPlayedThisTurn" || kind == "nth_attack_played_this_turn";
        var values = hasThreshold
            ? new[] { new RuntimeValueSlot("threshold", FirstNumber(operation.ChineseText, 1)) }
            : kind == "first_zero_cost_attack_played_each_turn"
                ? new[] { new RuntimeValueSlot("cost_marker", 0, Upgradable: false) }
                : Array.Empty<RuntimeValueSlot>();
        return Spec("trigger", "event", "self", values: values,
            trigger: new RuntimeTriggerSpec(kind, lifetime,
                hasThreshold ? "threshold" : null));
    }

    private static string? CompileLegacyWhenKind(string text)
    {
        if (text.Contains("名字中有", StringComparison.Ordinal) && text.Contains("打击", StringComparison.Ordinal))
            return "strike_card_drawn";
        if (text.Contains("获得格挡", StringComparison.Ordinal)) return "block_gained";
        if (text.Contains("施加易伤", StringComparison.Ordinal)) return "vulnerable_applied";
        if (text.Contains("回合内失去生命", StringComparison.Ordinal)) return "owner_hp_lost_during_turn";
        if (text.Contains("牌被消耗", StringComparison.Ordinal)) return "card_exhausted";
        if (Regex.IsMatch(text, @"第\d+张攻击牌", RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)))
            return "nth_attack_played_this_turn";
        if (text.Contains("技能牌", StringComparison.Ordinal)) return "skill_played";
        return null;
    }

    private static OperationRuntimeSpec? CompileConditionalTrigger(GeneratorOperation operation)
    {
        var kind = operation.Template switch
        {
            "C:untilTurnEndCardDrawn" => "card_drawn",
            "C:forEachDiscarded" => "for_each_discarded_card",
            "C:grantNextAttack" => "next_attack",
            "C:for" => "next_attack",
            "C:grantNextAttacksThisTurn" => "next_attacks_this_turn",
            "C:whileInCombat" => "attack_played_cost_reduction",
            "C:whileInCombatSkillCostReduction" => "skill_played_cost_reduction",
            "C:ifFatal" or "D:IfFatal" or "R:IfFatal" or "CL:IfFatal" => "fatal",
            "C:ifLastDrawnSkill" => "last_drawn_card_is_skill",
            "C:ifTargetPoisoned" => "target_has_poison",
            "C:ifTargetVulnerable" => "target_has_vulnerable",
            "C:playableIfDrawPileEmpty" => "draw_pile_empty",
            "CL:AtNextTurnStart" or "NCR:NextTurn" or "R:NextTurn" => "next_turn_start",
            "CL:IfHandEmpty" => "hand_empty",
            "CL:IfNoAttacksInHand" => "no_attacks_in_hand",
            "D:ForEachExhaustedStatus" => "for_each_exhausted_status",
            "D:IfCardsPlayedBelow" => "cards_played_this_turn_below",
            "D:IfEnemyIntendsAttack" => "enemy_intends_attack",
            "D:NextTurnsStart" => "next_turns_start",
            "NCR:IfDoomAppliedThisTurn" => "doom_applied_this_turn",
            "NCR:IfFirstPlayThisTurn" => "first_play_of_this_card_this_turn",
            "NCR:IfOstyAlive" => "osty_alive",
            "NCR:IfOstyAttackedThisTurn" => "osty_attacked_this_turn",
            "NCR:WheneverCardPlayedThisTurn" => "card_played",
            "NCR:WheneverOstyAttacksTargetThisTurn" => "osty_attacks_target",
            "legacy:wheneverCardUsed" => "card_played",
            "legacy:unblockedDamage" => "attack_dealt_damage",
            "R:AtTurnStartIfInExhaust" => "turn_start_if_self_in_exhaust",
            "C:untilTurnEnd" => CompileUntilTurnEndKind(operation.ChineseText),
            "C:after" => operation.ChineseText.Contains("回合结束", StringComparison.Ordinal)
                ? "turn_end_if_self_in_exhaust" : "self_exhausted",
            "C:forEach" => operation.ChineseText.Contains("非攻击牌", StringComparison.Ordinal)
                ? "for_each_exhausted_non_attack" : "for_each_exhausted_card",
            "C:if" => CompileLegacyConditionKind(operation.ChineseText),
            _ => null
        };
        if (kind is null) return null;
        var isCondition = operation.Template is "C:if" or "C:ifFatal" or "C:ifLastDrawnSkill"
            or "C:ifTargetPoisoned" or "C:ifTargetVulnerable" or "C:playableIfDrawPileEmpty"
            or "CL:IfFatal" or "CL:IfHandEmpty"
            or "CL:IfNoAttacksInHand" or "D:IfCardsPlayedBelow" or "D:IfEnemyIntendsAttack" or "D:IfFatal"
            or "NCR:IfDoomAppliedThisTurn" or "NCR:IfFirstPlayThisTurn" or "NCR:IfOstyAlive"
            or "NCR:IfOstyAttackedThisTurn" or "R:IfFatal";
        var hasThreshold = operation.Template is "C:grantNextAttacksThisTurn" or "D:IfCardsPlayedBelow"
            || kind == "exhaust_pile_minimum";
        var hasDuration = operation.Template == "D:NextTurnsStart";
        var hasPercentage = kind == "vulnerable_enemy_damage_reduction";
        var values = hasThreshold
            ? new[] { new RuntimeValueSlot("threshold", FirstNumber(operation.ChineseText, 1)) }
            : hasDuration
                ? new[] { new RuntimeValueSlot("duration", FirstNumber(operation.ChineseText, 1)) }
                : hasPercentage
                    ? new[] { new RuntimeValueSlot("percentage", FirstNumber(operation.ChineseText, 50)) }
                    : kind is "attack_played_cost_reduction" or "skill_played_cost_reduction"
                        ? new[] { new RuntimeValueSlot("amount", FirstNumber(operation.ChineseText, 1)) }
                    : Array.Empty<RuntimeValueSlot>();
        if (isCondition)
            return Spec("condition", kind, "self", values: values,
                condition: new RuntimeConditionSpec(kind, "self", hasThreshold ? "threshold" : null));
        var lifetime = operation.Template switch
        {
            "C:untilTurnEnd" or "C:untilTurnEndCardDrawn" or "C:grantNextAttacksThisTurn" => "this_turn",
            "CL:AtNextTurnStart" or "NCR:NextTurn" or "R:NextTurn" => "next_turn",
            "D:NextTurnsStart" => "next_n_turns",
            "C:for" or "C:grantNextAttack" => "combat",
            _ => "immediate"
        };
        return Spec("trigger", "event", "self", values: values,
            trigger: new RuntimeTriggerSpec(kind, lifetime, hasThreshold ? "threshold" : null,
                hasDuration ? "duration" : null));
    }

    private static string? CompileUntilTurnEndKind(string text)
    {
        if (text.Contains("下一张攻击牌", StringComparison.Ordinal)) return "next_attack";
        if (text.Contains("受到一次攻击", StringComparison.Ordinal)) return "attack_received";
        if (text.Contains("打出一张攻击牌", StringComparison.Ordinal)) return "attack_played";
        if (text.Contains("打出一张牌", StringComparison.Ordinal)) return "card_played";
        if (text.Contains("敌人对你造成的伤害降低", StringComparison.Ordinal))
            return "vulnerable_enemy_damage_reduction";
        return null;
    }

    private static string? CompileLegacyConditionKind(string text)
    {
        if (text.StartsWith("斩杀时", StringComparison.Ordinal)) return "fatal";
        if (Regex.IsMatch(text, @"至少有\d+张", RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100))) return "exhaust_pile_minimum";
        if (text.Contains("本回合曾消耗", StringComparison.Ordinal)) return "card_exhausted_this_turn";
        if (text.Contains("本回合失去过生命", StringComparison.Ordinal)) return "owner_lost_hp_this_turn";
        if (text.Contains("拥有易伤", StringComparison.Ordinal)) return "target_has_vulnerable";
        return null;
    }

    private static OperationRuntimeSpec? CompileAbilityRule(GeneratorOperation operation)
    {
        var variant = operation.Template switch
        {
            "A:rule" when operation.ChineseText.Contains("格挡不再", StringComparison.Ordinal) =>
                "retain_block_between_turns",
            "A:rule" when operation.ChineseText.Contains("技能牌", StringComparison.Ordinal)
                              && operation.ChineseText.Contains("耗能变为0", StringComparison.Ordinal) =>
                "skills_cost_zero",
            "A:rule" when operation.ChineseText.Contains("第一次通过卡牌获得的格挡翻倍", StringComparison.Ordinal) =>
                "first_card_block_doubled_each_turn",
            "A:rule" when operation.ChineseText.Contains("拥有易伤的敌人受到的伤害增加", StringComparison.Ordinal) =>
                "vulnerable_enemy_damage_bonus",
            "A:rulePoisonExtraTriggers" => "poison_extra_triggers",
            "A:ruleShivBonusDamage" => "derivative_bonus_damage",
            "A:ruleUnblockedAttackPoison" => "unblocked_attack_applies_poison",
            "A:ruleShivsHitAll" => "derivative_hits_all",
            "A:rulePlayedSkillsGainSly" => "played_skills_gain_sly",
            "A:ruleShivsRetainAndFirstBonus" => "derivative_retain_first_bonus",
            "A:ruleShivsRetain" => "derivative_retain",
            "A:ruleFirstShivBonusDamage" => "first_derivative_bonus_damage",
            "A:ruleWeakEnemiesTakeMoreAttackDamage" => "weak_enemy_attack_damage_bonus",
            "A:ruleRetainHand" => "retain_hand_at_turn_end",
            "A:VoidFormFirstCardsFree" => "first_cards_free_each_turn",
            "CL:DieOnUnblockedAttack" => "die_on_unblocked_attack",
            "R:KingsSwordHitsAllEnemies" => "kings_sword_hits_all",
            _ when operation.Template.StartsWith("A:ProxyAtomic_", StringComparison.Ordinal) =>
                SanitizeTemplate(operation.Template),
            _ => null
        };
        if (variant is null) return null;
        return Spec("combat_rule", variant, "self",
            values: TryProjectLegacyUpgradeValue(operation, operation.ChineseText, out var projection)
                    && projection is not null
                ? [new RuntimeValueSlot(projection.SlotId, projection.BaseValue)]
                : []);
    }

    private static OperationRuntimeSpec CompileTemplateFallback(GeneratorOperation operation)
    {
        var opcode = operation.Scope switch
        {
            OperationScope.SingleEnemyOnly => "template_target_action",
            OperationScope.NonTargeted => "template_self_action",
            OperationScope.Modifier => "template_modifier",
            OperationScope.Independent => "template_independent_action",
            OperationScope.AbilityRule => "template_combat_rule",
            OperationScope.AbilityTrigger => "template_trigger",
            OperationScope.ConditionalTrigger => "template_condition",
            _ => "template_action"
        };
        var target = operation.Scope == OperationScope.SingleEnemyOnly ? "selected_enemy" : "self";
        IReadOnlyList<RuntimeValueSlot> values = [];
        var usesX = LegacyValueUsesX(operation);
        var hasProjection = TryProjectLegacyUpgradeValue(operation, operation.ChineseText, out var projection)
                            && projection is not null;
        if (usesX)
        {
            var specialX = SpecialXCardConverter.ValueUsesSpecialX(operation,
                SpecialXCardConverter.PrimaryValueIndex(operation));
            values = [new RuntimeValueSlot(hasProjection ? projection!.SlotId : "amount", 0,
                specialX ? "special_x" : "energy_x", LegacyXOffset(operation.ChineseText),
                !hasProjection || !CardEffectRules.IsNonUpgradeableNumericMarker(operation)
                || operation.Template is "NCR:IncreaseAllCardCostsThisTurn" or "R:ReturnAfterSkillsPlayed")];
        }
        else if (hasProjection)
            values = [new RuntimeValueSlot(projection!.SlotId, projection.BaseValue, "fixed", 0,
                !CardEffectRules.IsNonUpgradeableNumericMarker(operation)
                || operation.Template is "NCR:IncreaseAllCardCostsThisTurn" or "R:ReturnAfterSkillsPlayed")];
        return Spec(opcode, SanitizeTemplate(operation.Template), target,
            flags: operation.RequiresSingleTarget ? ["requires_single_target"] : [], values: values);
    }

    private static string SanitizeTemplate(string template)
    {
        var builder = new StringBuilder(template.Length + 8);
        foreach (var character in template)
        {
            if (character is >= 'A' and <= 'Z') builder.Append(char.ToLowerInvariant(character));
            else if (character is >= 'a' and <= 'z' or >= '0' and <= '9') builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != '_') builder.Append('_');
        }
        return builder.ToString().Trim('_');
    }

    private static OperationRuntimeSpec Spec(string opcode, string variant, string target,
        string sourceZone = "none", string destinationZone = "none", string cardFilter = "any",
        IReadOnlyList<string>? flags = null, IReadOnlyList<RuntimeValueSlot>? values = null,
        RuntimeConditionSpec? condition = null, RuntimeTriggerSpec? trigger = null) =>
        new(OperationRuntimeSpec.CurrentSchemaVersion, opcode, variant, target, sourceZone, destinationZone,
            cardFilter, (flags ?? []).Order(StringComparer.Ordinal).ToArray(),
            // Preserve declaration order: slot zero is the operation's legacy/semantic primary printed value.
            // StableSignature sorts by ID independently, so multiplayer identity remains order-insensitive.
            (values ?? []).ToArray(), condition, trigger);

    private static RuntimeValueSlot Amount(GeneratorOperation operation, int fallback)
    {
        var specialX = SpecialXCardConverter.ValueUsesSpecialX(operation,
            SpecialXCardConverter.PrimaryValueIndex(operation));
        var energyX = !specialX && operation.ChineseText.Contains('X');
        return specialX || energyX
            ? new RuntimeValueSlot("amount", 0, specialX ? "special_x" : "energy_x",
                LegacyXOffset(operation.ChineseText))
            : new RuntimeValueSlot("amount", FirstNumber(operation.ChineseText, fallback));
    }

    private static RuntimeValueSlot ScalarValue(GeneratorOperation operation, string slotId, int fallback,
        int numericIndex = 0)
    {
        var specialX = SpecialXCardConverter.ValueUsesSpecialX(operation, numericIndex);
        var energyX = !specialX && operation.ChineseText.Contains('X');
        return specialX || energyX
            ? new RuntimeValueSlot(slotId, 0, specialX ? "special_x" : "energy_x",
                LegacyXOffset(operation.ChineseText))
            : new RuntimeValueSlot(slotId, FirstNumber(operation.ChineseText, fallback));
    }

    private static RuntimeValueSlot Count(int value, bool explicitValue = true) =>
        new("count", value, Explicit: explicitValue);

    private static readonly HashSet<string> FirstNumericConsumerTemplates = new(StringComparer.Ordinal)
    {
        "N:E", "N:NextTurnEnergy", "D:GainEnergy", "D:NextTurnEnergy", "NCR:GainEnergy",
        "NCR:NextTurnEnergy", "R:GainEnergy", "R:GainStars", "D:IncreaseThisCardCost",
        "NCR:IncreaseAllCardCostsThisTurn", "CL:GainGold", "A:ProxyAtomic_Royalties",
        "R:PlaySelectedSkillMultipleTimes", "D:ChannelLightning", "D:ChannelFrost", "D:ChannelDark",
        "D:ChannelPlasma", "D:ChannelGlass", "D:ChannelRandom", "D:IncreaseAllClaws",
        "A:whenEnergyCostAtLeast", "NCR:WheneverHighCostCardPlayed", "D:NextTurnsStart", "CL:AfterTurns",
        "C:grantNextAttacksThisTurn", "N:Draw", "N_DRAW", "N:B", "N_BLOCK", "N:HP-",
        "D:LoseTemporaryFocus", "D:LoseFocus", "N:LoseDex", "D:LoseOrbSlots", "NCR:LoseStrength"
    };

    private static int FirstNumber(string text, int fallback)
    {
        var match = Number.Match(text);
        return match.Success ? int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture) : fallback;
    }

    private static int Value(OperationRuntimeSpec spec, string id) =>
        spec.Values.Single(value => value.Id == id).BaseValue;

    private static string PrimarySlotId(GeneratorOperation operation, int numericIndex) => operation.Template switch
    {
        "T:D" or "T:DX" or "T:D_EnergyX" or "N:AllD" or "N:RandomD" or "N:RetaliateDamage"
            or "NCR:OstyDamage" or "NCR:OstyAllDamage" or "CL:RollingAllDamage"
            or "D:RepeatPerEnergySpentThisTurn" =>
                numericIndex == 0 ? "damage" : "hits",
        "N:B" or "N_BLOCK" or "N:NextTurnBlock" => "block",
        "N:Draw" or "N_DRAW" or "N:NextTurnDraw" => "draw",
        "N:E" or "N:NextTurnEnergy" or "D:GainEnergy" or "D:NextTurnEnergy" or "NCR:GainEnergy"
            or "NCR:NextTurnEnergy" or "R:GainEnergy" => "energy",
        "N:HP-" => "hp_loss",
        "N:RandomPoison" => numericIndex == 0 ? "amount" : "hits",
        "D:DrawAndDiscardNonZero" => "draw",
        "I:DrawAndBlockIfSkill" => numericIndex == 0 ? "draw" : "block",
        "CL:AddRandomZeroCostCardsToHand" => "count",
        "CL:ProxyAtomic_Discovery" or "CL:ProxyAtomic_Splash" or "I:ProxyAtomic_Quasar" =>
            numericIndex == 0 ? "choices" : "picks",
        "M:base" when operation.ChineseText.Contains("力量", StringComparison.Ordinal)
            && operation.ChineseText.Contains("额外获得", StringComparison.Ordinal)
            && operation.ChineseText.Contains("格挡", StringComparison.Ordinal) =>
                numericIndex == 0 ? "strength_interval" : "block_per_interval",
        _ => "amount"
    };
}
