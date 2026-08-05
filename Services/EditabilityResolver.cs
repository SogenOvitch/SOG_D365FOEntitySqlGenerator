using D365EntitySqlGenerator.Models;

namespace D365EntitySqlGenerator.Services;

public sealed class EditabilityResult
{
    /// <summary>True → the destination alias must be prefixed with RDD_ (not importable on create).</summary>
    public bool Rdd { get; set; }

    /// <summary>Blocking reasons (drive the RDD_ prefix), e.g. "staging field not editable on create".</summary>
    public List<string> Reasons { get; } = new();

    /// <summary>Diagnostic notes that don't block import (e.g. "staging field not found").</summary>
    public List<string> Notes { get; } = new();
}

/// <summary>
/// Decides whether an entity field is importable on create. Per spec, only AllowEditOnCreate=No
/// blocks (AllowEdit=No alone is fine). Three places are checked — entity field, staging table
/// field, target (backing) table field — plus read-only propagation from datasource / entity.
/// </summary>
public sealed class EditabilityResolver
{
    private readonly MetadataService _meta;

    public EditabilityResolver(MetadataService meta) => _meta = meta;

    public EditabilityResult Evaluate(
        EntityInfo entity, EntityFieldInfo field, EntityDataSourceInfo? ds, TableInfo? stagingTable)
    {
        var r = new EditabilityResult();

        // Read-only propagation → blanket RDD_.
        if (entity.IsReadOnly)
            r.Reasons.Add("entity read-only");
        if (ds is { IsReadOnly: true })
            r.Reasons.Add($"datasource {ds.Name} read-only");

        // 1) Entity field level.
        if (!field.AllowEditOnCreate)
            r.Reasons.Add("entity field not editable on create");

        // 2) Staging table level (only meaningful when the entity has a staging table).
        if (entity.DataManagementEnabled && entity.StagingTable.Length > 0)
        {
            var sf = stagingTable?.FindField(field.Name);
            if (sf == null)
                r.Notes.Add("staging field not found");
            else if (!sf.AllowEditOnCreate)
                r.Reasons.Add("staging field not editable on create");
        }

        // 3) Target (backing) table level.
        if (ds is { IsNestedEntity: true })
        {
            r.Notes.Add("nested entity");
        }
        else if (ds?.Table_ != null && field.DataField.Length > 0)
        {
            var tf = ds.Table_.FindField(field.DataField);
            if (tf == null)
                r.Notes.Add("target field not found");
            else if (!tf.AllowEditOnCreate)
                r.Reasons.Add($"{ds.Table_.Name} field not editable on create");
        }

        r.Rdd = r.Reasons.Count > 0;
        return r;
    }
}
