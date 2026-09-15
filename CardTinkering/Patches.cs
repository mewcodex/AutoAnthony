using System.Reflection;
using AutoAnthony;
using ChaosCardGenerator;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.sts2.Core.Nodes.TopBar;
using static AutoAnthonyCardTinkering.TinkeringText;

namespace AutoAnthonyCardTinkering;

[ModInitializer(nameof(Init))]
public static class CardTinkeringBootstrap
{
    private static bool _initialized;
    public static void Init()
    {
        if (_initialized) return;
        _initialized = true;
        AutoAnthonyBalanceAdjustmentApi.RegisterDefinitionChangedListener(
            TinkeringStateStore.RebaseAfterBalanceAdjustment);
        var harmony = new Harmony("autoanthony.card-tinkering.v111");
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes()
                     .Where(type => type.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0))
        {
            try { new PatchClassProcessor(harmony, type).Patch(); }
            catch (Exception exception) { Log.Error($"[CardTinkering] Patch failed for {type.FullName}: {exception}"); }
        }
        Log.Info("[CardTinkering] Auto Anthonyology: Card Tinkering initialized.");
    }
}

// Chimera builds its generated card pool in a prefix to these methods and only publishes RunContent.Active after
// generation finishes. Enter suppression one priority step before Chimera's Priority.First prefix so Card
// Tinkering's generation predicate cannot affect that pool during the otherwise invisible initialization window.
[HarmonyPatch(typeof(NGame), nameof(NGame.StartNewSingleplayerRun))]
internal static class ChimeraSingleplayerStartupPatch
{
    [HarmonyPriority(Priority.First + 1)]
    private static void Prefix(CharacterModel character, out bool __state) =>
        __state = ChimeraCompatibility.BeginRunStartupIfChimera(character);

    private static Exception? Finalizer(Exception? __exception, bool __state)
    {
        ChimeraCompatibility.EndRunStartup(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(NGame), nameof(NGame.StartNewMultiplayerRun))]
internal static class ChimeraMultiplayerStartupPatch
{
    [HarmonyPriority(Priority.First + 1)]
    private static void Prefix(StartRunLobby lobby, out bool __state) =>
        __state = lobby.Players.Any(player =>
            ChimeraCompatibility.BeginRunStartupIfChimera(player.character));

    private static Exception? Finalizer(Exception? __exception, bool __state)
    {
        ChimeraCompatibility.EndRunStartup(__state);
        return __exception;
    }
}

internal static class XEndpointGenerationPolicy
{
    internal static bool Accepts(GeneratedCard card)
    {
        // Numeric Random deliberately projects values after the ordinary balance envelope. Reapplying the editor's
        // X endpoint ceiling here would silently erase that mode's high rolls during pool generation.
        if (ChaosRunDefinitions.ActiveNumericRandomMode) return true;
        if (!CardTinkeringApi.IsVariableX(card)) return true;
        return TinkeringValue.TryXEndpointPricing(card, out var x0, out var x3)
               && x0.NetValue <= x0.OrdinaryUpperBound + 0.0001d
               && x3.NetValue <= x3.OrdinaryUpperBound + 0.0001d;
    }
}

[HarmonyPatch]
internal static class XEndpointGenerationAcceptancePatch
{
    private static MethodBase TargetMethod() => AccessTools.DeclaredMethod(
        AccessTools.TypeByName("ChaosCardGenerator.ComponentAssemblyGenerator"),
        "TryFinalizeUniqueCard")!;

    private static void Prefix(ref Func<GeneratedCard, bool>? accept)
    {
        if (!CardTinkeringFeatureGate.BaseEnabled) return;
        var original = accept;
        accept = original is null
            ? XEndpointGenerationPolicy.Accepts
            : card => XEndpointGenerationPolicy.Accepts(card) && original(card);
    }
}

internal static class SavePayloadHelpers
{
    internal const string CardProperty = nameof(CardTinkeringSaveCarrier.CardTinkeringCardPayload);

    internal static string? ReadCard(SerializableCard save) => save.Props?.strings?
        .FirstOrDefault(item => item.name == CardProperty).value;

    internal static void WriteCard(SerializableCard save, string payload)
    {
        save.Props ??= new SavedProperties();
        save.Props.strings ??= [];
        save.Props.strings.RemoveAll(item => item.name == CardProperty);
        save.Props.strings.Add(new SavedProperties.SavedProperty<string>(CardProperty, payload));
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.ToSerializable))]
internal static class CardStateSavePatch
{
    private static void Postfix(CardModel __instance, SerializableCard __result)
    {
        if (ChimeraCompatibility.IsSuppressed(__instance)) return;
        if (__instance is ChaosCardModel card && TinkeringStateStore.TryGet(card, out var state))
            SavePayloadHelpers.WriteCard(__result, TinkeringStateStore.EncodeCard(state,
                includeCombatUsage: TinkeringStateStore.IsLiveCombatCard(card)));
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.FromSerializable))]
internal static class CardStateLoadPatch
{
    private static void Prefix(SerializableCard save, out string? __state) =>
        __state = ChimeraCompatibility.IsSuppressed() ? null : SavePayloadHelpers.ReadCard(save);

    private static void Postfix(CardModel __result, string? __state)
    {
        if (__result is not ChaosCardModel card || string.IsNullOrWhiteSpace(__state)) return;
        try
        {
            TinkeringStateStore.SetLoaded(card, TinkeringStateStore.DecodeCard(__state));
            if (card.CombatState is not null) InvalidCardRuntime.DisableIfNeeded(card);
        }
        catch (Exception exception) { Log.Warn($"[CardTinkering] Ignored malformed card state: {exception.Message}"); }
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.ToSave))]
internal static class RunStateSavePatch
{
    private static void Postfix(RunManager __instance, SerializableRun __result)
    {
        var run = __instance.DebugOnlyGetState();
        if (run is null) return;
        var id = ModelDb.Modifier<CardTinkeringSaveCarrier>().Id;
        __result.Modifiers.RemoveAll(modifier => modifier.Id == id);
        if (ChimeraCompatibility.IsSuppressed(run)) return;
        var carrier = (CardTinkeringSaveCarrier)ModelDb.Modifier<CardTinkeringSaveCarrier>().ToMutable();
        carrier.CardTinkeringRunPayload = TinkeringStateStore.EncodeRun(TinkeringStateStore.GetRun(run));
        __result.Modifiers.Add(carrier.ToSerializable());
    }
}

[HarmonyPatch(typeof(RunState), nameof(RunState.FromSerializable))]
internal static class RunStateLoadPatch
{
    private static void Prefix(SerializableRun save, out SerializableModifier[] __state)
    {
        var id = ModelDb.Modifier<CardTinkeringSaveCarrier>().Id;
        __state = save.Modifiers.Where(modifier => modifier.Id == id).ToArray();
        // Extract the editor payload before the game creates RunState. The live Modifiers property may be a read-only
        // wrapper, and leaving this invisible carrier inside it makes NTopBar expose an empty modifier strip and call
        // First() on zero children while continuing a run.
        save.Modifiers.RemoveAll(modifier => modifier.Id == id);
    }

    private static void Postfix(SerializableRun save, RunState __result, SerializableModifier[] __state)
    {
        // NMainMenu retains and may resave this SerializableRun after FromSerializable returns. Restore the serialized
        // carrier there while deliberately keeping it out of RunState.Modifiers.
        save.Modifiers.AddRange(__state);
        if (ChimeraCompatibility.IsSuppressed(__result)) return;
        var payload = __state.Select(modifier => modifier.Props?.strings?
                .FirstOrDefault(item => item.name == nameof(CardTinkeringSaveCarrier.CardTinkeringRunPayload)).value)
            .LastOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(payload)) return;
        try { TinkeringStateStore.SetRun(__result, TinkeringStateStore.DecodeRun(payload)); }
        catch (Exception exception) { Log.Warn($"[CardTinkering] Ignored malformed run state: {exception.Message}"); }
    }
}

[HarmonyPatch(typeof(NGame), nameof(NGame.LoadRun))]
internal static class TrainingResumeLoadingPatch
{
    private const string LayerName = "AutoAnthonyCardTinkeringResumeLoading";
    private sealed record LoadingState(CanvasLayer Layer, ulong StartedAt);

    private static void Prefix(RunState runState, out LoadingState? __state)
    {
        __state = null;
        if (ChimeraCompatibility.IsSuppressed(runState)
            || !CardTinkeringFeatureGate.BaseEnabled
            || RunManager.Instance.SavedMapsToLoad is not { } savedMaps
            || !savedMaps.TryGetValue(runState.CurrentActIndex, out var savedMap)
            || !TrainingActMap.IsTrainingPoint(savedMap, runState.CurrentMapCoord)) return;

        var game = NGame.Instance;
        if (!GodotObject.IsInstanceValid(game)) return;
        var oldLayer = game.GetNodeOrNull<CanvasLayer>(LayerName);
        if (GodotObject.IsInstanceValid(oldLayer)) oldLayer!.QueueFree();

        var layer = new CanvasLayer { Name = LayerName, Layer = 1000 };
        var backdrop = new ColorRect
        {
            Color = new Color("211b2b"),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(backdrop);

        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(center);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(620, 176) };
        var panelStyle = new StyleBoxFlat
        {
            BgColor = new Color("315743"),
            BorderColor = new Color("8bd9a4"),
            BorderWidthLeft = 3,
            BorderWidthTop = 3,
            BorderWidthRight = 3,
            BorderWidthBottom = 3,
            CornerRadiusTopLeft = 18,
            CornerRadiusTopRight = 18,
            CornerRadiusBottomRight = 18,
            CornerRadiusBottomLeft = 18
        };
        panel.AddThemeStyleboxOverride("panel", panelStyle);
        var content = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        content.AddThemeConstantOverride("separation", 12);
        var title = new Label
        {
            Text = Localize("卡牌工匠台", "Card Workbench"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        title.AddThemeFontSizeOverride("font_size", 40);
        title.AddThemeColorOverride("font_color", new Color("fff6e2"));
        content.AddChild(title);
        var status = new Label
        {
            Text = Localize("正在恢复训练室…", "Restoring Training Room…"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        status.AddThemeFontSizeOverride("font_size", 25);
        status.AddThemeColorOverride("font_color", new Color("bcebc9"));
        content.AddChild(status);
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 32);
        margin.AddThemeConstantOverride("margin_right", 32);
        margin.AddThemeConstantOverride("margin_top", 24);
        margin.AddThemeConstantOverride("margin_bottom", 24);
        margin.AddChild(content);
        panel.AddChild(margin);
        center.AddChild(panel);
        game.AddChild(layer);
        __state = new LoadingState(layer, Time.GetTicksMsec());
        Log.Info("[CardTinkering] Showing the Training Room resume screen while run assets load.");
    }

    private static void Postfix(ref Task __result, LoadingState? __state)
    {
        if (__state is not null) __result = RemoveAfterLoad(__result, __state);
    }

    private static async Task RemoveAfterLoad(Task original, LoadingState state)
    {
        try
        {
            await original;
        }
        finally
        {
            Log.Info($"[CardTinkering] Training Room resume screen covered "
                     + $"{Time.GetTicksMsec() - state.StartedAt:N0} ms of loading.");
            if (GodotObject.IsInstanceValid(state.Layer))
            {
                var tree = state.Layer.GetTree();
                if (tree is null)
                {
                    state.Layer.QueueFree();
                }
                else
                {
                    // Keep the panel through the first part of the native fade so there is no empty black frame
                    // between LoadRun completing and the fully built workbench becoming visible.
                    var timer = tree.CreateTimer(.45d, processAlways: true, processInPhysics: false,
                        ignoreTimeScale: true);
                    timer.Timeout += () =>
                    {
                        if (GodotObject.IsInstanceValid(state.Layer) && !state.Layer.IsQueuedForDeletion())
                            state.Layer.QueueFree();
                    };
                }
            }
        }
    }
}

[HarmonyPatch(typeof(NTopBarModifier), nameof(NTopBarModifier.Create))]
internal static class HideSaveCarrierModifierPatch
{
    private static bool Prefix(ModifierModel modifier, ref NTopBarModifier? __result)
    {
        if (modifier is not CardTinkeringSaveCarrier) return true;
        __result = null;
        return false;
    }
}

[HarmonyPatch(typeof(NTopBar), "UpdateNavigation")]
internal static class EmptyTopBarModifierNavigationPatch
{
    private static readonly FieldInfo? ModifiersContainerField =
        AccessTools.Field(typeof(NTopBar), "_modifiersContainer");

    private static void Prefix(NTopBar __instance)
    {
        if (ChimeraCompatibility.IsSuppressed()) return;
        if (ModifiersContainerField?.GetValue(__instance) is not Control
            { Visible: true } modifierContainer
            || modifierContainer.GetChildren().OfType<Control>().Any()) return;
        modifierContainer.Visible = false;
        Log.Warn("[CardTinkering] Prevented navigation through an empty top-bar modifier strip.");
    }
}

[HarmonyPatch(typeof(RunState), nameof(RunState.CloneCard))]
internal static class RunCloneStatePatch
{
    private static void Postfix(CardModel mutableCard, CardModel __result) => Copy(mutableCard, __result);
    internal static void Copy(CardModel source, CardModel result)
    {
        if (ChimeraCompatibility.IsSuppressed(source) || ChimeraCompatibility.IsSuppressed(result)) return;
        if (source is not ChaosCardModel from || result is not ChaosCardModel to) return;
        var hasState = TinkeringStateStore.TryGet(from, out var state);
        // Clone/dupe implementations may copy the serialized payload after they have already materialized the
        // canonical slot's caches. Reapply the definition once on the finished mutable clone so operation indices,
        // DynamicVars, cost and keywords all describe the same edited card. An invalid combat view deliberately
        // has cleared tags/operations which no longer satisfy the immutable shell check; its original payload is
        // retained by cloning and DisableIfNeeded rebuilds the inert view immediately afterwards.
        if (from.HasTinkeredDefinition && (!hasState || !state.Invalid))
            to.ApplyTinkeredDefinition(from.Generated);
        if (hasState) TinkeringStateStore.SetRuntimeClone(to, state);
    }
}

[HarmonyPatch("MegaCrit.Sts2.Core.Combat.CombatState", "CloneCard")]
internal static class CombatCloneStatePatch
{
    private static void Postfix(CardModel mutableCard, CardModel __result)
    {
        RunCloneStatePatch.Copy(mutableCard, __result);
        if (__result is ChaosCardModel card) InvalidCardRuntime.DisableIfNeeded(card);
    }
}

// Self-copy effects call CardModel.CreateClone directly rather than RunState/CombatState.CloneCard. The generated
// definition is already cloned by the core SavedProperty pipeline, but the editor's per-component run state lives in
// a ConditionalWeakTable and must be copied explicitly as well.
[HarmonyPatch(typeof(CardModel), nameof(CardModel.CreateClone))]
internal static class CardCreateCloneStatePatch
{
    private static void Postfix(CardModel __instance, CardModel __result)
    {
        RunCloneStatePatch.Copy(__instance, __result);
        if (__result is ChaosCardModel card) InvalidCardRuntime.DisableIfNeeded(card);
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.CreateCloneForPlayer))]
internal static class CardCreateCloneForPlayerStatePatch
{
    private static void Postfix(CardModel __instance, CardModel __result)
    {
        RunCloneStatePatch.Copy(__instance, __result);
        if (__result is ChaosCardModel card) InvalidCardRuntime.DisableIfNeeded(card);
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.CreateDupe))]
internal static class CardCreateDupeStatePatch
{
    private static void Postfix(CardModel __instance, CardModel __result)
    {
        RunCloneStatePatch.Copy(__instance, __result);
        if (__result is ChaosCardModel card) InvalidCardRuntime.DisableIfNeeded(card);
    }
}

internal static class InvalidCardRuntime
{
    private static readonly FieldInfo? TinkeredDefinitionField =
        AccessTools.Field(typeof(ChaosCardModel), "_tinkeredDefinition");
    private static readonly MethodInfo? RebuildCachedCardStateMethod =
        AccessTools.Method(typeof(ChaosCardModel), "RebuildCachedCardState");

    internal static void DisableIfNeeded(ChaosCardModel card)
    {
        if (ChimeraCompatibility.IsSuppressed(card)) return;
        if (!TinkeringStateStore.TryGet(card, out var state) || !state.Invalid) return;
        var generated = card.Generated;
        var upgrade = generated.Upgrade is null ? null : generated.Upgrade with
        {
            Effects = [],
            UpgradedChineseDescription = "没有效果。",
            UpgradedEnglishDescription = "No effect.",
            AddedKeywords = [],
            RemovedKeywords = [],
            AddedCustomKeywords = [],
            RemovedCustomKeywords = []
        };
        var disabled = OperationRuntimeSpecCompiler.Attach(generated with
        {
            ChineseDescription = "没有效果。",
            EnglishDescription = "No effect.",
            Tags = [],
            CustomKeywords = [],
            Operations = [],
            Upgrade = upgrade
        });
        if (TinkeredDefinitionField is null || RebuildCachedCardStateMethod is null)
        {
            Log.Error("[CardTinkering] Could not disable an invalid combat card because the core cache fields changed.");
            return;
        }
        // Do not overwrite TinkeredDefinitionPayload: that serialized payload remains the editable, out-of-combat
        // definition. A combat reload restores it first and this patch then creates the same inert runtime view.
        TinkeredDefinitionField.SetValue(card, disabled);
        RebuildCachedCardStateMethod.Invoke(card, null);
    }
}

[HarmonyPatch(typeof(ChaosCardModel), nameof(ChaosCardModel.AfterCardEnteredCombat))]
internal static class InvalidCardEnteredCombatPatch
{
    private static void Prefix(ChaosCardModel __instance) => InvalidCardRuntime.DisableIfNeeded(__instance);
}

[HarmonyPatch]
internal static class InvalidCardAddedToCombatPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => AccessTools.TypeByName(
            "MegaCrit.Sts2.Core.Combat.CombatState")
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(method => method.Name == "AddCard"
                         && method.GetParameters().FirstOrDefault()?.ParameterType == typeof(CardModel));

    private static void Postfix(CardModel card)
    {
        if (card is ChaosCardModel generated) InvalidCardRuntime.DisableIfNeeded(generated);
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.UpgradeInternal))]
internal static class ComponentUpgradeStatePatch
{
    private static void Postfix(CardModel __instance)
    {
        if (ChimeraCompatibility.IsSuppressed(__instance)) return;
        if (__instance is ChaosCardModel card)
            TinkeringStateStore.SetInstalledComponentsUpgraded(card, upgraded: true);
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.DowngradeInternal))]
internal static class ComponentDowngradeStatePatch
{
    private static void Postfix(CardModel __instance)
    {
        if (ChimeraCompatibility.IsSuppressed(__instance)) return;
        if (__instance is ChaosCardModel card)
            TinkeringStateStore.SetInstalledComponentsUpgraded(card, upgraded: false);
    }
}

/// <summary>
/// ApplyTinkeredDefinition rebuilds the generated card's cached cost, keywords and dynamic variables. Native card
/// enchantments belong to the shell, not to any operation whose text they happen to modify or append, so their
/// cached mutations must be replayed after every editor rebuild. Derivative-card enchantment metadata remains on
/// GeneratorOperation and is deliberately untouched here.
/// </summary>
[HarmonyPatch(typeof(ChaosCardModel), nameof(ChaosCardModel.ApplyTinkeredDefinition))]
internal static class PreserveShellEnchantmentPatch
{
    private static void Postfix(ChaosCardModel __instance)
    {
        if (ChimeraCompatibility.IsSuppressed(__instance)) return;
        if (__instance.Enchantment is not { } enchantment) return;
        enchantment.ModifyCard();
        __instance.FinalizeUpgradeInternal();
    }
}

[HarmonyPatch]
internal static class ExecuteSingleUseAndGrowthPatch
{
    private sealed record ExecutionSnapshot(bool Allowed, bool TrackGrowth, bool TrackSingleUse,
        int Damage, int Block);
    private static readonly MethodInfo DependencyCondition = AccessTools.DeclaredMethod(
        AccessTools.TypeByName("AutoAnthony.ChaosOperationExecutor"), "DependencyConditionMatches")!;

    private static MethodBase TargetMethod() => AccessTools.DeclaredMethod(
        AccessTools.TypeByName("AutoAnthony.ChaosOperationExecutor"), "Execute")!;

    private static bool Prefix(ChaosCardModel __0, int __1, ref Task __result, out ExecutionSnapshot __state)
    {
        if (ChimeraCompatibility.IsSuppressed(__0))
        {
            __state = new ExecutionSnapshot(true, false, false, __0.ExtraDamage, __0.ExtraBlock);
            return true;
        }
        var operation = (uint)__1 < (uint)__0.Generated.Operations.Count
            ? __0.Generated.Operations[__1] : null;
        var trackGrowth = operation is not null && TrainingSession.IsPermanentGrowth(operation);
        if (trackGrowth) TinkeringStateStore.PreparePermanentGrowth(__0);
        __state = new ExecutionSnapshot(true, trackGrowth, false, __0.ExtraDamage, __0.ExtraBlock);
        if (operation is null || !TrainingSession.IsSingleUse(operation))
            return true;
        // The core executor itself returns without applying an operation when neither the card nor its owner has a
        // combat state. Check that before touching persistent one-shot state. Editor previews are marked explicitly
        // as an additional boundary because a post-combat screen can temporarily retain the owner's combat state.
        if (TinkeringStateStore.IsEditorPreview(__0) || !TinkeringStateStore.IsLiveCombatCard(__0))
            return true;
        var conditionMatches = (bool)(DependencyCondition.Invoke(null, [__0, __1]) ?? true);
        if (!conditionMatches) return true;
        if (TinkeringStateStore.CanExecuteSingleUse(__0, __1))
        {
            __state = __state with { TrackSingleUse = true };
            return true;
        }
        __state = __state with { Allowed = false };
        __result = Task.CompletedTask;
        return false;
    }

    private static void Postfix(ChaosCardModel __0, int __1, ref Task __result, ExecutionSnapshot __state)
    {
        if (!__state.Allowed || !__state.TrackGrowth && !__state.TrackSingleUse) return;
        __result = RecordAfter(__result, __0, __1, __state.TrackSingleUse,
            __state.Damage, __state.Block);
    }

    private static async Task RecordAfter(Task original, ChaosCardModel card, int operationIndex,
        bool markSingleUse, int beforeDamage, int beforeBlock)
    {
        await original;
        if (markSingleUse)
            TinkeringStateStore.MarkSingleUseExecuted(card, operationIndex);
        if (TrainingSession.IsPermanentGrowth(card.Generated.Operations[operationIndex]))
            TinkeringStateStore.RecordPermanentGrowth(card, operationIndex,
                card.ExtraDamage - beforeDamage, card.ExtraBlock - beforeBlock);
    }
}

/// <summary>
/// Most X-valued Damage and Block operations enter DamageAndHits/ApplyBlockModifiers after resolving X. Two legacy
/// compound paths consume OperationAmount directly instead, so an X slot has no DynamicVar on which the core preview
/// hook can layer permanent growth. Add only the missing aggregate on those direct paths; ordinary fixed slots retain
/// their existing DynamicVar behavior and the unified paths must not be touched or they would apply growth twice.
/// </summary>
[HarmonyPatch]
internal static class DirectXPermanentGrowthPatch
{
    private static MethodBase TargetMethod() => AccessTools.DeclaredMethod(
        typeof(ChaosCardModel), "OperationAmount")!;

    private static void Postfix(ChaosCardModel __instance, int operationIndex, ref int __result)
    {
        if (ChimeraCompatibility.IsSuppressed(__instance)) return;
        if ((uint)operationIndex >= (uint)__instance.Generated.Operations.Count) return;
        var operation = __instance.Generated.Operations[operationIndex];
        var valueSlot = operation.Template switch
        {
            "N:RetaliateDamage" => "damage",
            "I:DrawAndBlockIfSkill" => "block",
            _ => null
        };
        if (valueSlot is null) return;
        var slot = OperationRuntimeSpecCompiler.GetOrCompile(operation).Values
            .FirstOrDefault(candidate => candidate.Id == valueSlot);
        if (slot?.Source is not ("special_x" or "energy_x" or "star_x")) return;

        var growth = operation.Template == "N:RetaliateDamage"
            ? __instance.ExtraDamage
            : __instance.ExtraBlock;
        __result = (int)Math.Clamp((long)__result + Math.Max(0, growth), 0L, int.MaxValue);
    }
}

[HarmonyPatch]
internal static class OncePerCombatRedGlowPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(ChaosCardModel), "get_ShouldGlowRedInternal")!;

    private static void Postfix(ChaosCardModel __instance, ref bool __result)
    {
        if (ChimeraCompatibility.IsSuppressed(__instance)) return;
        if (!__result) __result = TinkeringStateStore.HasUnavailableSingleUse(__instance);
    }
}

[HarmonyPatch]
internal static class RuntimeDescriptionStatusPatch
{
    private static MethodBase TargetMethod() => AccessTools.DeclaredMethod(
        AccessTools.TypeByName("AutoAnthony.ChaosRuntimeDescriptionCache"), "GetFormatted")!;

    private static void Postfix(ChaosCardModel __0, bool __1, ref string __result)
    {
        if (ChimeraCompatibility.IsSuppressed(__0)) return;
        if (__0.CombatState is not null || !TinkeringStateStore.TryGet(__0, out var state)) return;
        var lines = new List<string>();
        if (state.Invalid)
            lines.Add(__1
                ? "[color=#ff7272]这张卡在战斗中失效。[/color]"
                : "[color=#ff7272]This card is disabled in combat.[/color]");
        if (state.Eternal) lines.Add(__1 ? "[gold]永恒[/gold]" : "[gold]Eternal[/gold]");
        for (var index = 0; index < state.Components.Count && index < __0.Generated.Operations.Count; index++)
        {
            var progress = state.Components[index];
            var operation = __0.Generated.Operations[index];
            var label = __1 ? CardDescriptionRenderer.Render([operation])
                : EnglishCardDescriptionRenderer.Render([operation]);
            if (progress.PermanentDamage > 0)
                lines.Add(__1 ? $"（{label}：永久伤害累计 +{progress.PermanentDamage}）"
                    : $"({label}: permanent damage +{progress.PermanentDamage})");
            if (progress.PermanentBlock > 0)
                lines.Add(__1 ? $"（{label}：永久格挡累计 +{progress.PermanentBlock}）"
                    : $"({label}: permanent Block +{progress.PermanentBlock})");
        }
        if (lines.Count > 0)
        {
            var prefixCount = state.Invalid ? 1 : 0;
            var prefix = prefixCount > 0 ? lines[0] + "\n" : string.Empty;
            var suffix = lines.Skip(prefixCount).ToArray();
            __result = prefix + __result + (suffix.Length > 0 ? "\n" + string.Join('\n', suffix) : string.Empty);
        }
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyGeneratedMapLate))]
internal static class TrainingMapPatch
{
    private static void Postfix(IRunState runState, int actIndex, ref ActMap __result)
    {
        if (ChimeraCompatibility.IsSuppressed(runState) || !CardTinkeringFeatureGate.BaseEnabled) return;
        if (runState is not RunState concrete || actIndex is < 0 or > 2) return;
        if (TrainingActMap.IsTrainingMap(__result)) return;
        if (TinkeringStateStore.HasCompleted(concrete, actIndex)) return;
        if (TrainingActMap.TryCreate(__result, out var wrapped))
        {
            __result = wrapped;
            Log.Info($"[CardTinkering] Added the pre-Boss Training Room to Act {actIndex + 1}.");
        }
    }
}

[HarmonyPatch]
internal static class TrainingRoomTypeRollPatch
{
    private static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(RunManager), "RollRoomTypeFor",
        [typeof(MapPointType), typeof(IEnumerable<RoomType>)])!;

    private static bool Prefix(RunManager __instance, MapPointType __0, ref RoomType __result)
    {
        var run = __instance.DebugOnlyGetState();
        if (__0 != MapPointType.Unknown || run is null
            || ChimeraCompatibility.IsSuppressed(run)
            || !TrainingActMap.IsTrainingPoint(run.Map, run.CurrentMapPoint)) return true;
        // Unknown nodes normally consume the question-room odds roll before CreateRoom is called. A Training Room
        // must be deterministic, including while restoring the current room from a save.
        __result = RoomType.Event;
        return false;
    }
}

[HarmonyPatch]
internal static class TrainingRoomCreationPatch
{
    private static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(RunManager), "CreateRoom",
        [typeof(RoomType), typeof(MapPointType), typeof(AbstractModel)])!;

    private static bool Prefix(RunManager __instance, MapPointType mapPointType, ref AbstractRoom __result)
    {
        var run = __instance.DebugOnlyGetState();
        if (run is null
            || ChimeraCompatibility.IsSuppressed(run)
            || !TrainingActMap.IsTrainingPoint(run.Map, run.CurrentMapPoint)) return true;
        TrainingEventLocalization.Install();
        __result = new EventRoom(ModelDb.Event<CardTinkeringEvent>());
        Log.Info($"[CardTinkering] Forced Training Room creation at {run.CurrentMapPoint!.coord} "
            + $"(requested point type: {mapPointType}).");
        return false;
    }
}

[HarmonyPatch(typeof(EventModel), nameof(EventModel.CreateInitialPortrait))]
internal static class TrainingPortraitPatch
{
    private static bool Prefix(EventModel __instance, ref Godot.Texture2D __result)
    {
        if (__instance is not CardTinkeringEvent || ChimeraCompatibility.IsSuppressed()) return true;
        __result = PreloadManager.Cache.GetTexture2D(CardTinkeringEvent.PortraitPath);
        return false;
    }
}

[HarmonyPatch(typeof(NEventRoom), nameof(NEventRoom._Ready))]
internal static class TrainingEventUiPatch
{
    private static readonly FieldInfo EventField = AccessTools.Field(typeof(NEventRoom), "_event");

    private static void Postfix(NEventRoom __instance)
    {
        if (ChimeraCompatibility.IsSuppressed()
            || EventField.GetValue(__instance) is not CardTinkeringEvent training) return;
        TrainingEventLocalization.Install();
        // _Ready is retained as an early attachment path. CardTinkeringEvent.AfterEventStarted retries after the
        // event node has definitely entered the scene tree, which also covers other mods replacing room setup.
        TrainingEventUi.Attach(training);
    }
}

[HarmonyPatch(typeof(NEventRoom), nameof(NEventRoom.Create))]
internal static class TrainingEventUiCreationPatch
{
    private static void Postfix(EventModel eventModel, IRunState? runState, NEventRoom? __result)
    {
        if (eventModel is not CardTinkeringEvent training || runState is not RunState run || __result is null) return;
        if (ChimeraCompatibility.IsSuppressed(run)) return;
        TrainingEventLocalization.Install();
        TrainingEventUi.Attach(__result, training, run);
    }
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Open))]
internal static class BlockUnfinishedTrainingMapPatch
{
    private static bool Prefix(NMapScreen __instance, ref NMapScreen __result)
    {
        var run = RunManager.Instance.DebugOnlyGetState();
        if (ChimeraCompatibility.IsSuppressed(run)) return true;
        if (run?.CurrentRoom is not EventRoom room
            || room.LocalMutableEvent is not CardTinkeringEvent { IsFinished: false })
            return true;
        __result = __instance;
        Log.Info("[CardTinkering] Blocked map opening while the local Training Room editor is unfinished.");
        return false;
    }
}
