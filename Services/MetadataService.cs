using D365EntitySqlGenerator.Models;

namespace D365EntitySqlGenerator.Services;

/// <summary>
/// Loads a fully-resolved <see cref="EntityInfo"/>: parses the entity, its datasource tree, and
/// the backing AxTables (cached). Also exposes the staging table for the editability checks.
/// </summary>
public sealed class MetadataService
{
    private readonly MetadataIndex _index;
    private readonly EntityParser _entityParser = new();
    private readonly TableParser _tableParser = new();
    private readonly Dictionary<string, TableInfo?> _tableCache = new(StringComparer.OrdinalIgnoreCase);

    public MetadataService(MetadataIndex index) => _index = index;

    public MetadataIndex Index => _index;

    /// <summary>Parse + resolve an entity by name. Returns null if not found.</summary>
    public EntityInfo? LoadEntity(string entityName)
    {
        var file = _index.EntityFile(entityName);
        return file == null ? null : LoadEntityFromFile(file);
    }

    public EntityInfo LoadEntityFromFile(string file)
    {
        // Package/model are only cosmetic here; derive from the index entry if present.
        var entry = _index.Entities.FirstOrDefault(e =>
            string.Equals(e.FilePath, file, StringComparison.OrdinalIgnoreCase));
        var entity = _entityParser.Parse(file, entry?.Package ?? "", entry?.Model ?? "");

        if (entity.RootDataSource != null)
            ResolveDataSource(entity.RootDataSource);

        return entity;
    }

    private void ResolveDataSource(EntityDataSourceInfo ds)
    {
        if (ds.Table.Length > 0)
        {
            if (_index.TableExists(ds.Table))
            {
                ds.Table_ = GetTable(ds.Table);
                ds.IsNestedEntity = false;
            }
            else if (_index.EntityExists(ds.Table))
            {
                ds.IsNestedEntity = true;   // datasource backed by another entity (option B: opaque)
            }
            // else: an AxView or unknown object → opaque physical table, Table_ stays null.
        }

        foreach (var child in ds.Children)
            ResolveDataSource(child);
    }

    /// <summary>Parse an AxTable by name (cached). Null if not an AxTable (view/entity/missing).</summary>
    public TableInfo? GetTable(string name)
    {
        if (_tableCache.TryGetValue(name, out var cached))
            return cached;

        TableInfo? info = null;
        var file = _index.TableFile(name);
        if (file != null)
        {
            try { info = _tableParser.Parse(file); }
            catch { info = null; }
        }
        _tableCache[name] = info;
        return info;
    }
}
