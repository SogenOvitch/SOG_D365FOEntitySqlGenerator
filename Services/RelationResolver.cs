using D365EntitySqlGenerator.Models;

namespace D365EntitySqlGenerator.Services;

public sealed class JoinResolution
{
    /// <summary>ON conditions, already formatted as "alias.col = alias.col" or "alias.col = value".</summary>
    public List<string> Conditions { get; } = new();

    /// <summary>True when a named relation could not be resolved (nested entity / view / missing table).</summary>
    public bool Unresolved { get; set; }

    public List<string> Notes { get; } = new();
}

/// <summary>
/// Turns a datasource's relations into SQL ON conditions.
/// Two relation forms:
///  - explicit:  JoinDataSource.Field = thisDataSource.RelatedField
///  - named:     resolved from thisDataSource's AxTable relation (Field → RelatedField on RelatedTable)
/// </summary>
public sealed class RelationResolver
{
    public JoinResolution Resolve(EntityDataSourceInfo ds)
    {
        var result = new JoinResolution();

        foreach (var rel in ds.Relations)
        {
            // A relation node often carries BOTH an explicit Field/RelatedField pair and a
            // JoinRelationName label. The explicit pair is the already-resolved join, so prefer it;
            // only fall back to resolving the named table relation when no explicit pair is present.
            if (rel.Field != null && rel.RelatedField != null)
                ResolveExplicit(ds, rel, result);
            else if (rel.IsNamed)
                ResolveNamed(ds, rel, result);
            else
                result.Notes.Add("incomplete relation");
        }

        return result;
    }

    private static void ResolveExplicit(EntityDataSourceInfo ds, DataSourceRelationInfo rel, JoinResolution result)
    {
        // JoinDataSource.Field = thisDataSource.RelatedField ; JoinDataSource omitted → parent.
        var otherAlias = rel.JoinDataSource ?? ds.Parent?.Name ?? "";
        if (rel.Field != null && rel.RelatedField != null && otherAlias.Length > 0)
            result.Conditions.Add($"{Q(otherAlias)}.{rel.Field} = {Q(ds.Name)}.{rel.RelatedField}");
        else
            result.Notes.Add("incomplete relation");
    }

    private void ResolveNamed(EntityDataSourceInfo ds, DataSourceRelationInfo rel, JoinResolution result)
    {
        var name = rel.JoinRelationName!;
        var parent = ds.Parent;

        // Resolve the relation in priority order. A relation may be owned by the datasource's own
        // table (FK on the child → parent) or by the parent's table (FK on the parent → this child).
        // JoinRelationName matches a relation's Name OR its RelatedTableRole; when it matches
        // neither, fall back to the (flagged) relation whose RelatedTable points at the other table.
        //   1) Name on child        2) Name on parent
        //   3) RelatedTableRole on child   4) RelatedTableRole on parent   (all precise)
        //   5) table-pair on child   6) table-pair on parent               (best-guess, "verify")
        TableRelationInfo? tableRel;
        bool childOwns;

        if ((tableRel = ds.Table_?.FindRelation(name)) != null) childOwns = true;
        else if ((tableRel = parent?.Table_?.FindRelation(name)) != null) childOwns = false;
        else if ((tableRel = ds.Table_?.FindRelationByRole(name)) != null) childOwns = true;
        else if ((tableRel = parent?.Table_?.FindRelationByRole(name)) != null) childOwns = false;
        else if ((tableRel = MatchByRelatedTable(ds.Table_, parent?.Table, result, "child")) != null) childOwns = true;
        else if ((tableRel = MatchByRelatedTable(parent?.Table_, ds.Table, result, "parent")) != null) childOwns = false;
        else
        {
            result.Unresolved = true;
            result.Notes.Add($"relation '{name}' not found on {ds.Table} or {parent?.Table}");
            return;
        }

        // Child owns:  child.Field = related.RelatedField.  Parent owns:  parent.Field = child.RelatedField.
        var fieldSideAlias = childOwns ? ds.Name : parent?.Name ?? "";
        var relatedSideAlias = childOwns
            ? (FindRelatedAlias(ds, tableRel.RelatedTable) ?? parent?.Name ?? "")
            : ds.Name;

        foreach (var c in tableRel.Constraints)
        {
            switch (c.Kind)
            {
                case RelationConstraintKind.Field:
                    if (fieldSideAlias.Length > 0 && relatedSideAlias.Length > 0)
                        result.Conditions.Add($"{Q(fieldSideAlias)}.{c.Field} = {Q(relatedSideAlias)}.{c.RelatedField}");
                    break;
                case RelationConstraintKind.Fixed:
                    if (fieldSideAlias.Length > 0)
                        result.Conditions.Add($"{Q(fieldSideAlias)}.{c.Field} = {c.Value}");
                    break;
                case RelationConstraintKind.RelatedFixed:
                    if (relatedSideAlias.Length > 0)
                        result.Conditions.Add($"{Q(relatedSideAlias)}.{c.RelatedField} = {c.Value}");
                    break;
            }
        }
    }

    /// <summary>
    /// Fallback when the JoinRelationName matches no relation by name: pick the relation on
    /// <paramref name="owner"/> whose RelatedTable equals <paramref name="otherTable"/> (the other
    /// datasource's table). Emits a note, and flags ambiguity when more than one candidate exists.
    /// </summary>
    private static TableRelationInfo? MatchByRelatedTable(
        TableInfo? owner, string? otherTable, JoinResolution result, string side)
    {
        if (owner == null || string.IsNullOrEmpty(otherTable)) return null;
        var matches = owner.Relations
            .Where(r => string.Equals(r.RelatedTable, otherTable, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0) return null;

        result.Notes.Add(matches.Count == 1
            ? $"join matched by table via {side} relation '{matches[0].Name}' (verify)"
            : $"ambiguous join: {matches.Count} {side} relations to {otherTable}; using '{matches[0].Name}' (verify)");
        return matches[0];
    }

    /// <summary>Walk parent chain first, then any known datasource, for one whose Table matches.</summary>
    private static string? FindRelatedAlias(EntityDataSourceInfo ds, string relatedTable)
    {
        if (relatedTable.Length == 0) return null;
        for (var p = ds.Parent; p != null; p = p.Parent)
            if (string.Equals(p.Table, relatedTable, StringComparison.OrdinalIgnoreCase))
                return p.Name;
        return null;
    }

    /// <summary>Datasource aliases are PascalCase identifiers in D365; emit them plain for readability.</summary>
    private static string Q(string alias) => alias;
}
