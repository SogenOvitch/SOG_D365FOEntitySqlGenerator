using System.IO;
using System.Xml.Linq;
using D365EntitySqlGenerator.Models;

namespace D365EntitySqlGenerator.Services;

/// <summary>Parses AxDataEntityView XML into <see cref="EntityInfo"/> (structure only; table
/// resolution happens later in <see cref="MetadataService"/>).</summary>
public sealed class EntityParser
{
    public EntityInfo Parse(string filePath, string package, string model)
    {
        var root = XDocument.Load(filePath).Root
                   ?? throw new InvalidDataException($"Empty XML: {filePath}");

        var stagingTable = root.Val("DataManagementStagingTable");
        var primaryKey = root.Val("PrimaryKey");
        var entity = new EntityInfo
        {
            Name = root.Val("Name"),
            Package = package,
            Model = model,
            FilePath = filePath,
            IsReadOnly = root.Bool("IsReadOnly", defaultYes: false),
            // DM-enabled entities carry the tag as Yes plus a staging table; otherwise treat as not enabled.
            DataManagementEnabled = root.Bool("DataManagementEnabled", defaultYes: false)
                                    && stagingTable.Length > 0,
            StagingTable = stagingTable,
            PrimaryKey = primaryKey,
        };

        // Primary key fields: PrimaryKey → Keys/AxDataEntityViewKey[Name] → Fields/AxDataEntityViewKeyField/DataField.
        if (primaryKey.Length > 0)
        {
            var key = root.El("Keys").Els("AxDataEntityViewKey")
                .FirstOrDefault(k => string.Equals(k.Val("Name"), primaryKey, StringComparison.OrdinalIgnoreCase));
            foreach (var kf in key.El("Fields").Els("AxDataEntityViewKeyField"))
            {
                var df = kf.Val("DataField");
                if (df.Length > 0) entity.PrimaryKeyFields.Add(df);
            }
        }

        // Entity fields: the direct child <Fields> whose items are AxDataEntityViewField.
        var fieldsEl = root.Els("Fields")
            .FirstOrDefault(f => f.Els("AxDataEntityViewField").Any());
        foreach (var f in fieldsEl.Els("AxDataEntityViewField"))
        {
            var type = f.TypeOf();
            var isMapped = type == "AxDataEntityViewMappedField";
            entity.Fields.Add(new EntityFieldInfo
            {
                Name = f.Val("Name"),
                IsMapped = isMapped,
                DataSource = isMapped ? f.Val("DataSource") : "",
                DataField = isMapped ? f.Val("DataField") : "",
                UnmappedType = isMapped ? "" : StripUnmappedPrefix(type),
                AllowEdit = f.Bool("AllowEdit", defaultYes: true),
                AllowEditOnCreate = f.Bool("AllowEditOnCreate", defaultYes: true),
            });
        }

        // Datasource tree under ViewMetadata/DataSources.
        var dsRoot = root.El("ViewMetadata").El("DataSources").El("AxQuerySimpleRootDataSource");
        if (dsRoot != null)
        {
            entity.RootDataSource = ParseDataSource(dsRoot, isRoot: true, parent: null, entity);
        }

        return entity;
    }

    private EntityDataSourceInfo ParseDataSource(
        XElement el, bool isRoot, EntityDataSourceInfo? parent, EntityInfo entity)
    {
        var ds = new EntityDataSourceInfo
        {
            Name = el.Val("Name"),
            Table = el.Val("Table"),
            IsRoot = isRoot,
            IsReadOnly = el.Bool("IsReadOnly", defaultYes: false),
            ApplyDateFilter = el.Bool("ApplyDateFilter", defaultYes: false),
            JoinMode = el.Val("JoinMode"),
            Parent = parent,
        };

        foreach (var r in el.El("Relations").Els("AxQuerySimpleDataSourceRelation"))
        {
            var joinName = r.Val("JoinRelationName");
            ds.Relations.Add(new DataSourceRelationInfo
            {
                JoinRelationName = joinName.Length > 0 ? joinName : null,
                Field = NullIfBlank(r.Val("Field")),
                JoinDataSource = NullIfBlank(r.Val("JoinDataSource")),
                RelatedField = NullIfBlank(r.Val("RelatedField")),
            });
        }

        foreach (var rg in el.El("Ranges").Els("AxQuerySimpleDataSourceRange"))
        {
            ds.Ranges.Add(new DataSourceRangeInfo
            {
                Field = rg.Val("Field"),
                Value = rg.Val("Value"),
                Tags = rg.Val("Tags"),
            });
        }

        // Register (aliases are unique within an entity query).
        if (ds.Name.Length > 0)
            entity.DataSourcesByName[ds.Name] = ds;

        foreach (var child in el.El("DataSources").Els("AxQuerySimpleEmbeddedDataSource"))
            ds.Children.Add(ParseDataSource(child, isRoot: false, parent: ds, entity));

        return ds;
    }

    private static string StripUnmappedPrefix(string type)
    {
        const string prefix = "AxDataEntityViewUnmappedField";
        return type.StartsWith(prefix, StringComparison.Ordinal)
            ? type[prefix.Length..]
            : type;
    }

    private static string? NullIfBlank(string s) => string.IsNullOrEmpty(s) ? null : s;
}
