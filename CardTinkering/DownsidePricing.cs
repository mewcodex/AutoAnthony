namespace AutoAnthonyCardTinkering;

/// <summary>Display projection of downside terms already calculated by CardTinkeringApi.</summary>
internal sealed record ComponentDownsidePricing(bool IsNegative, double GlobalMultiplier, double FlatValue);
