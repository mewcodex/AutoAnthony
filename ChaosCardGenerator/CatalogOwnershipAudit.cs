namespace ChaosCardGenerator;

/// <summary>Reports how reviewed source occurrences collapse into reusable structural component schemas.</summary>
internal static class CatalogOwnershipAudit
{
    internal static string Run()
    {
        var catalogs = Enum.GetValues<GeneratedCharacter>()
            .Select(character => CharacterComponentCatalogs.Get(character))
            .ToArray();
        var occurrences = catalogs.SelectMany(catalog => catalog.Recipes.SelectMany(recipe =>
                recipe.Atoms.Select(atom => new Occurrence(catalog.Character, recipe.Id, atom))))
            .ToArray();
        var shared = occurrences.GroupBy(item => item.Atom.SchemaKey, StringComparer.Ordinal)
            .Select(group => new
            {
                SchemaKey = group.Key,
                Roles = group.Select(item => item.Character).Distinct().Order().ToArray(),
                Occurrences = group.ToArray()
            })
            .Where(group => group.Roles.Length > 1)
            .OrderByDescending(group => group.Occurrences.Length)
            .ThenBy(group => group.SchemaKey, StringComparer.Ordinal)
            .ToArray();

        var lines = new List<string>
        {
            $"recipes={catalogs.Sum(catalog => catalog.Recipes.Count)}; "
                + $"sourceOccurrences={occurrences.Length}; "
                + $"structuralSchemas={occurrences.Select(item => item.Atom.SchemaKey).Distinct(StringComparer.Ordinal).Count()}; "
                + $"crossRoleSchemas={shared.Length}"
        };
        lines.AddRange(catalogs.Select(catalog =>
            $"{catalog.Character}: recipes={catalog.Recipes.Count}; "
            + $"sourceOccurrences={catalog.Recipes.Sum(recipe => recipe.Atoms.Count)}; "
            + $"structuralSchemas={catalog.Atoms.Count}"));
        lines.AddRange(shared.Take(25).Select(group =>
            $"shared roles={string.Join(',', group.Roles)}; occurrences={group.Occurrences.Length}; "
            + $"template={group.Occurrences[0].Atom.Template}; "
            + $"route={group.Occurrences[0].Atom.RuntimeSpec?.Opcode}/{group.Occurrences[0].Atom.RuntimeSpec?.Variant}"));
        return string.Join(Environment.NewLine, lines);
    }

    private sealed record Occurrence(GeneratedCharacter Character, string RecipeId, ComponentAtom Atom);
}
