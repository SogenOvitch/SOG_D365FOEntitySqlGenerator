namespace D365EntitySqlGenerator.Models;

/// <summary>A field exposed by the data entity.</summary>
public sealed class EntityFieldInfo
{
    public string Name { get; init; } = "";          // entity field name = destination alias

    /// <summary>True for AxDataEntityViewMappedField (real source column).
    /// False for AxDataEntityViewUnmappedField* (computed / view-method).</summary>
    public bool IsMapped { get; init; }

    public string DataSource { get; init; } = "";    // entity datasource (alias) name; empty for unmapped
    public string DataField { get; init; } = "";     // source column on that datasource; empty for unmapped

    /// <summary>For unmapped fields, the concrete type suffix (String/Enum/Real/Int/...).</summary>
    public string UnmappedType { get; init; } = "";

    // Entity-level editability. Omitted in XML when Yes → default true.
    public bool AllowEdit { get; init; } = true;
    public bool AllowEditOnCreate { get; init; } = true;

    public bool IsComputed => !IsMapped;
}

/// <summary>A join relation between two datasources inside the entity query.</summary>
public sealed class DataSourceRelationInfo
{
    /// <summary>Named form: a relation name defined on THIS datasource's table.</summary>
    public string? JoinRelationName { get; init; }

    // Explicit form. Semantics: JoinDataSource.Field = thisDataSource.RelatedField
    public string? Field { get; init; }
    public string? JoinDataSource { get; init; }   // null => the immediate parent datasource
    public string? RelatedField { get; init; }

    public bool IsNamed => !string.IsNullOrEmpty(JoinRelationName);
}

/// <summary>A baked filter (Range) on a datasource.</summary>
public sealed class DataSourceRangeInfo
{
    public string Field { get; init; } = "";
    public string Value { get; init; } = "";
    public string Tags { get; init; } = "";
}

/// <summary>A node in the entity's datasource tree (root or embedded).</summary>
public sealed class EntityDataSourceInfo
{
    public string Name { get; init; } = "";          // datasource alias → used as the SQL alias
    public string Table { get; init; } = "";          // physical table, or a nested-entity name
    public bool IsRoot { get; init; }
    public bool IsReadOnly { get; init; }

    /// <summary>ApplyDateFilter=Yes → the datasource is date-effective and must be filtered so that
    /// the execution date falls between ValidFrom and ValidTo. Missing/No → false.</summary>
    public bool ApplyDateFilter { get; init; }

    public string JoinMode { get; init; } = "";      // InnerJoin / OuterJoin / ExistsJoin / NotExistsJoin

    public List<DataSourceRelationInfo> Relations { get; } = new();
    public List<DataSourceRangeInfo> Ranges { get; } = new();
    public List<EntityDataSourceInfo> Children { get; } = new();

    public EntityDataSourceInfo? Parent { get; set; }

    // Resolved after parsing against the metadata index:
    public bool IsNestedEntity { get; set; }         // Table refers to another entity, not a physical table
    public TableInfo? Table_ { get; set; }            // resolved AxTable (null for nested entities / missing)
}

/// <summary>Parsed AxDataEntityView metadata.</summary>
public sealed class EntityInfo
{
    public string Name { get; init; } = "";
    public string Package { get; init; } = "";
    public string Model { get; init; } = "";
    public string FilePath { get; init; } = "";

    public bool IsReadOnly { get; init; }
    public bool DataManagementEnabled { get; init; }
    public string StagingTable { get; init; } = "";

    public List<EntityFieldInfo> Fields { get; } = new();
    public EntityDataSourceInfo? RootDataSource { get; set; }

    /// <summary>All datasources flattened, keyed by alias (Name).</summary>
    public Dictionary<string, EntityDataSourceInfo> DataSourcesByName { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public EntityDataSourceInfo? FindDataSource(string name) =>
        DataSourcesByName.TryGetValue(name, out var ds) ? ds : null;
}
