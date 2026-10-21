using System.Runtime.CompilerServices;
using AutoAnthony;
using ChaosCardGenerator;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

void Check(bool condition,string message) {if(!condition)throw new Exception(message);}
void Field(object value,Type owner,string name,object contents)=>AccessTools.Field(owner,name).SetValue(value,contents);
var source=(ChaosCardModel)RuntimeHelpers.GetUninitializedObject(typeof(ChaosRegentCard000));
var definition=new GeneratedCard(1,GeneratedCardType.Attack,TargetMode.SingleEnemy,GeneratedRarity.Common,
    "",[],[],Character:GeneratedCharacter.Regent,StarCost:2);
Field(source,typeof(ChaosCardModel),"_freeformDefinition",definition);
Field(source,typeof(CardModel),"_canonicalInstance",source);
Field(source,typeof(CardModel),"_dynamicVars",new DynamicVarSet([]));
Field(source,typeof(CardModel),"_keywords",new HashSet<CardKeyword>());
Field(source,typeof(CardModel),"_temporaryStarCosts",new List<TemporaryCardCost>());
Field(source,typeof(CardModel),"_baseStarCost",-1);
Field(source,typeof(CardModel),"_starCostSet",true);
Check(source.BaseStarCost==2,"Canonical view does not use the current definition");
if(!args.Contains("--baseline")) {
    var patch=typeof(ChaosCardModel).Assembly.GetType("AutoAnthony.Patches.GeneratedCardInitialStarCostPatch",true)!;
    new Harmony("autoanthony.tests.generated-star-cost").Patch(AccessTools.Method(typeof(CardModel),nameof(CardModel.ToMutable)),
        postfix:new HarmonyMethod(AccessTools.Method(patch,"Postfix")));
}
var card=(ChaosCardModel)source.ToMutable();
Check(card.BaseStarCost==2,$"A newly created Regent reward lost its Star cost: expected 2, actual {card.BaseStarCost}");
Console.WriteLine("Fresh reward uses current generated Star cost rather than inherited stale canonical fields.");
foreach(var stale in new[]{-1,0,5}) {
    Field(source,typeof(CardModel),"_baseStarCost",stale);
    Field(source,typeof(CardModel),"_starCostSet",true);
    var reward=(ChaosCardModel)source.ToMutable();
    Check(reward.CurrentStarCost==2&&reward.GetStarCostWithModifiers()==2,
        $"Reward display/payment inherited stale {stale} Stars");
}
foreach(var stars in new[]{-1,0,1,4}) {
    Field(source,typeof(ChaosCardModel),"_freeformDefinition",definition with {StarCost=stars});
    Check(source.ToMutable().BaseStarCost==stars,$"New fixed Star cost {stars} was altered");
}
Field(source,typeof(ChaosCardModel),"_freeformDefinition",definition with {StarCost=0,HasStarCostX=true});
var starX=(ChaosCardModel)source.ToMutable();
Check(starX.HasStarCostX&&starX.BaseStarCost==0,"X-Star shell was changed");
var upgrade=new CardUpgradePlan(1,[],"",[],"",UpgradedStarCost:1);
Field(source,typeof(ChaosCardModel),"_freeformDefinition",definition with {StarCost=3,Upgrade=upgrade});
card=(ChaosCardModel)source.ToMutable();
card.UpgradeInternal();
Check(card.BaseStarCost==1,"Star-cost upgrade not applied exactly once");
card.SetStarCostThisTurn(0);
var combatClone=(ChaosCardModel)card.ClonePreservingMutability();
Check(combatClone.BaseStarCost==1&&combatClone.CurrentStarCost==0,"Mutable clone lost upgrade/temporary free cost");
((List<TemporaryCardCost>)AccessTools.Field(typeof(CardModel),"_temporaryStarCosts").GetValue(combatClone)!).Clear();
Check(combatClone.CurrentStarCost==1,"Temporary cost expiry changed the upgraded base cost");
Field(combatClone,typeof(CardModel),"_baseStarCost",-1);
Field(combatClone,typeof(CardModel),"_starCostSet",true);
AccessTools.Method(typeof(ChaosCardModel),"AfterDeserialized").Invoke(combatClone,[]);
Check(combatClone.BaseStarCost==1,"Read restoration disagrees with the upgraded Star cost");
AccessTools.Method(typeof(ChaosCardModel),"ResetInitialStarCostCache").Invoke(null,[source]);
Check(!(bool)AccessTools.Field(typeof(CardModel),"_starCostSet").GetValue(source)!,"Pool cache was not invalidated");
var native=(CardModel)RuntimeHelpers.GetUninitializedObject(typeof(CardModel).Assembly.GetType(
    "MegaCrit.Sts2.Core.Models.Cards.StrikeIronclad",true)!);
Field(native,typeof(CardModel),"_canonicalInstance",native);
Field(native,typeof(CardModel),"_dynamicVars",new DynamicVarSet([]));
Field(native,typeof(CardModel),"_keywords",new HashSet<CardKeyword>());
Field(native,typeof(CardModel),"_temporaryStarCosts",new List<TemporaryCardCost>());
Field(native,typeof(CardModel),"_baseStarCost",7);
Field(native,typeof(CardModel),"_starCostSet",true);
Check(native.ToMutable().BaseStarCost==7,"Non-generated card costs were altered");
Console.WriteLine("PASS: stale -1/0/positive caches; current display/payment; no/zero/fixed/X Stars; upgrades; mutable clone and temporary free cost; read restoration; pool cache invalidation; native cards unchanged. No game/saves touched.");
