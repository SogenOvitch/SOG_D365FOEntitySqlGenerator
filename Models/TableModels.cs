namespace D365EntitySqlGenerator.Models;

/// <summary>A field defined on an AxTable.</summary>
public sealed class TableFieldInfo
{
    public string Name { get; init; } = "";

    /// <summary>AllowEdit. D365 omits the tag when it is Yes, so the default is true.</summary>
    public bool AllowEdit { get; init; } = true;

    /// <summary>AllowEditOnCreate. Omitted when Yes, so the default is true.</summary>
    public bool AllowEditOnCreate { get; init; } = true;
}

public enum RelationConstraintKind
{
    /// <summary>this.Field = related.RelatedField</summary>
    Field,
    /// <summary>this.Field = fixed Value</summary>
    Fixed,
    /// <summary>related.RelatedField = fixed Value</summary>
    RelatedFixed
}

/// <summary>One constraint inside an AxTableRelation.</summary>
public sealed class TableRelationConstraint
{
    public RelationConstraintKind Kind { get; init; }
    public string Field { get; init; } = "";        // field on the table owning the relation
    public string RelatedField { get; init; } = "";  // field on the related table
    public string Value { get; init; } = "";         // literal value for Fixed / RelatedFixed
}

/// <summary>A relation defined on an AxTable (used to resolve named datasource joins).</summary>
public sealed class TableRelationInfo
{
    public string Name { get; init; } = "";
    public string RelatedTable { get; init; } = "";

    /// <summary>The RelatedTableRole property. A datasource's JoinRelationName matches EITHER a
    /// relation's Name or its RelatedTableRole.</summary>
    public string RelatedTableRole { get; init; } = "";

    public bool EdtRelation { get; init; }
    public List<TableRelationConstraint> Constraints { get; } = new();
}

/// <summary>Parsed AxTable metadata (only what the generator needs).</summary>
public sealed class TableInfo
{
    public string Name { get; init; } = "";

    /// <summary>SaveDataPerCompany. Omitted when Yes, so the default is true (company-specific → has DataAreaId).</summary>
    public bool SaveDataPerCompany { get; init; } = true;

    public Dictionary<string, TableFieldInfo> Fields { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<TableRelationInfo> Relations { get; } = new();

    public bool HasDataAreaId => SaveDataPerCompany;

    public TableFieldInfo? FindField(string name) =>
        Fields.TryGetValue(name, out var f) ? f : null;

    public TableRelationInfo? FindRelation(string name) =>
        Relations.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    public TableRelationInfo? FindRelationByRole(string role) =>
        Relations.FirstOrDefault(r => string.Equals(r.RelatedTableRole, role, StringComparison.OrdinalIgnoreCase));
}
