using System.IO;
using System.Xml.Linq;
using D365EntitySqlGenerator.Models;

namespace D365EntitySqlGenerator.Services;

/// <summary>Parses AxTable XML into <see cref="TableInfo"/>.</summary>
public sealed class TableParser
{
    public TableInfo Parse(string filePath)
    {
        var root = XDocument.Load(filePath).Root
                   ?? throw new InvalidDataException($"Empty XML: {filePath}");
        return Parse(root);
    }

    public TableInfo Parse(XElement root)
    {
        var table = new TableInfo
        {
            Name = root.Val("Name"),
            // AxTable also carries a <TableType>; SaveDataPerCompany is what governs DataAreaId.
            SaveDataPerCompany = root.Bool("SaveDataPerCompany", defaultYes: true),
        };

        foreach (var f in root.El("Fields").Els("AxTableField"))
        {
            var name = f.Val("Name");
            if (name.Length == 0) continue;
            table.Fields[name] = new TableFieldInfo
            {
                Name = name,
                AllowEdit = f.Bool("AllowEdit", defaultYes: true),
                AllowEditOnCreate = f.Bool("AllowEditOnCreate", defaultYes: true),
            };
        }

        foreach (var r in root.El("Relations").Els("AxTableRelation"))
        {
            var rel = new TableRelationInfo
            {
                Name = r.Val("Name"),
                RelatedTable = r.Val("RelatedTable"),
                RelatedTableRole = r.Val("RelatedTableRole"),
                EdtRelation = r.Bool("EDTRelation", defaultYes: false),
            };

            foreach (var c in r.El("Constraints").Els("AxTableRelationConstraint"))
            {
                var kind = c.TypeOf() switch
                {
                    "AxTableRelationConstraintFixed" => RelationConstraintKind.Fixed,
                    "AxTableRelationConstraintRelatedFixed" => RelationConstraintKind.RelatedFixed,
                    _ => RelationConstraintKind.Field,
                };
                rel.Constraints.Add(new TableRelationConstraint
                {
                    Kind = kind,
                    Field = c.Val("Field"),
                    RelatedField = c.Val("RelatedField"),
                    Value = c.Val("Value", c.Val("ValueStr")),
                });
            }

            table.Relations.Add(rel);
        }

        return table;
    }
}
