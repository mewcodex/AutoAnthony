# Potion event type selection

Run after building AutoAnthony:

```powershell
dotnet run --project tests/FutureOfPotionsCoverageRegression -c Release -p:Sts2GameDir="<game directory>"
```

Enumerates 13,182 candidate-count combinations for the event's type selector. Existing valid choices
stay unchanged, built-in choices require three distinct candidates, and Common/Token potions cannot
choose Power. The historical external-pool threshold remains unchanged. Native reward population is
covered by the isolated `NativeCardVersionRegression` engine plugin.
