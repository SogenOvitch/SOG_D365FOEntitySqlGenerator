using System.Xml.Linq;

namespace D365EntitySqlGenerator.Services;

/// <summary>
/// D365 metadata XML puts elements in no namespace (root declares only xmlns:i, and nested
/// blocks re-declare xmlns=""). We therefore match purely on local names and read the
/// i:type discriminator from the XSI namespace.
/// </summary>
internal static class XmlHelpers
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    public static XElement? El(this XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    public static IEnumerable<XElement> Els(this XElement? parent, string localName) =>
        parent?.Elements().Where(e => e.Name.LocalName == localName) ?? Enumerable.Empty<XElement>();

    /// <summary>Text of a direct child element, or a fallback if missing/blank.</summary>
    public static string Val(this XElement? parent, string localName, string fallback = "")
    {
        var e = parent.El(localName);
        return e == null || string.IsNullOrEmpty(e.Value) ? fallback : e.Value.Trim();
    }

    /// <summary>Reads a Yes/No child element. Missing → <paramref name="defaultYes"/>.</summary>
    public static bool Bool(this XElement? parent, string localName, bool defaultYes)
    {
        var e = parent.El(localName);
        if (e == null) return defaultYes;
        return string.Equals(e.Value.Trim(), "Yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The i:type discriminator (local part), e.g. "AxDataEntityViewMappedField".</summary>
    public static string TypeOf(this XElement e) =>
        e.Attribute(Xsi + "type")?.Value ?? "";
}
