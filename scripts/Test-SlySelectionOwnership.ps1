$ErrorActionPreference = 'Stop'
# Compile the production selector against a small command double. This verifies command ownership, not Godot rendering.
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../ChaosMode/ChaosOperationExecutor.cs') -Raw
$start = $source.IndexOf('    private static async Task<IEnumerable<CardModel>> SelectFromHandIfAny(')
$end = $source.IndexOf('    private static async Task<IEnumerable<CardModel>> SelectFromHandForDiscardIfAny(', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'Selector not found.' }
$selector = $source.Substring($start, $end - $start).Replace('private static async', 'public static async').Replace('null!', 'null')
$slyStart = $source.IndexOf('else if (operation.Template == "I:GrantSlyToHandSkillThisTurn")')
$slyEnd = $source.IndexOf('else if (', $slyStart + 10)
if ($slyStart -lt 0 -or !$source.Substring($slyStart, $slyEnd - $slyStart).Contains('restoreAfterSelection: true')) {
    throw 'Grant Sly must release selection ownership before subsequent effects.'
}
$support = @'
#nullable enable annotations
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
public class AbstractModel { public string Id = "source"; }
public class CardModel : AbstractModel { }
public class Player { public List<CardModel> Cards = new List<CardModel>(); }
public class PlayerChoiceContext { }
public class ThrowingPlayerChoiceContext : PlayerChoiceContext { }
public struct CardSelectorPrefs { public int MinSelect; public int MaxSelect; }
public class PileType {
    public static PileType Hand = new PileType();
    public Player GetPile(Player player) { return player; }
}
public static class Log { public static void Warn(string message) { throw new Exception(message); } }
public static class CardSelectCmd {
    public static int Calls;
    public static AbstractModel Source;
    public static Task<IEnumerable<CardModel>> FromHand(PlayerChoiceContext context, Player player,
        CardSelectorPrefs prefs, Func<CardModel, bool> filter, AbstractModel source) {
        Calls++; Source = source;
        return Task.FromResult(player.Cards.Where(filter ?? (_ => true)).Take(prefs.MinSelect));
    }
}
public static class SelectionRegression {
    static CardSelectorPrefs ClampSelectionPrefs(CardSelectorPrefs prefs, int count) { return prefs; }
    static IEnumerable<CardModel> SelectAutomatically(Player player, List<CardModel> cards, CardSelectorPrefs prefs) {
        return cards.Take(prefs.MinSelect);
    }
    static List<CardModel> CompleteMandatorySelection(List<CardModel> candidates, CardModel[] selected, int min, int max) {
        return selected.ToList();
    }
    static void CloseFailedHandSelection(Player player, AbstractModel source) { }
    public static void Run() {
        var player = new Player();
        player.Cards.Add(new CardModel()); player.Cards.Add(new CardModel());
        var context = new PlayerChoiceContext();
        var source = new AbstractModel();
        var prefs = new CardSelectorPrefs { MinSelect = 1, MaxSelect = 1 };
        var selected = SelectFromHandIfAny(context, player, prefs, null, source, true).GetAwaiter().GetResult();
        if (selected.Single() != player.Cards[0] || CardSelectCmd.Source != null)
            throw new Exception("In-place edits retained source-owned hand visuals.");
        SelectFromHandIfAny(context, player, prefs, null, source).GetAwaiter().GetResult();
        if (CardSelectCmd.Source != source) throw new Exception("Moving effects lost native holder ownership.");
        int calls = CardSelectCmd.Calls;
        SelectFromHandIfAny(context, player, prefs, _ => false, source, true).GetAwaiter().GetResult();
        SelectFromHandIfAny(new ThrowingPlayerChoiceContext(), player, prefs, null, source, true).GetAwaiter().GetResult();
        if (CardSelectCmd.Calls != calls) throw new Exception("Empty/automatic selection opened a UI choice.");
    }
'@
Add-Type -TypeDefinition ($support + $selector + '}')
[SelectionRegression]::Run()
Write-Output 'Sly selection ownership regression passed (command routing; no live UI).'
