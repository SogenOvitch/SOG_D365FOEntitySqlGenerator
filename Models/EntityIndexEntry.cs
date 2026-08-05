namespace D365EntitySqlGenerator.Models;

/// <summary>Lightweight entry used to power the search box.</summary>
public sealed class EntityIndexEntry
{
    public string EntityName { get; set; } = "";
    public string Package { get; set; } = "";
    public string Model { get; set; } = "";
    public string FilePath { get; set; } = "";

    /// <summary>Root datasource table (first &lt;Table&gt; in the entity query).</summary>
    public string RootTable { get; set; } = "";

    /// <summary>All datasource tables in the entity (distinct), for "search by any datasource".</summary>
    public List<string> DataSourceTables { get; set; } = new();
}
