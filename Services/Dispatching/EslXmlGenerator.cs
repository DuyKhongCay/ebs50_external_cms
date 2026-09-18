using System.Text;
using System.Xml.Linq;

namespace ebs50_backend.Services.Dispatching;

/// <summary>
/// Generates Opticon-compliant XML files following the EslImageInfo schema.
/// </summary>
internal static class EslXmlGenerator
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    /// <summary>
    /// Generates XML document string for Opticon External CMS with dynamic image filename.
    /// Uses MAC address as the unique ID to eliminate cross-tag ID collisions and state fallback in EBS-50.
    /// </summary>
    /// <param name="macAddress">8-digit hexadecimal MAC address (used as unique Product ID and Label).</param>
    /// <param name="imageFileName">Dynamic target image filename (e.g. MAC_timestamp.png).</param>
    /// <param name="note">Informational note describing status and model.</param>
    /// <returns>Formatted XML string.</returns>
    public static string GenerateEslImageInfoXml(string macAddress, string imageFileName, string note)
    {
        return GenerateEslImageInfoXml(macAddress, imageFileName, uniqueId: macAddress, note);
    }

    /// <summary>
    /// Generates XML document string for Opticon External CMS with custom unique ID and dynamic image filename.
    /// </summary>
    /// <param name="macAddress">8-digit hexadecimal MAC address.</param>
    /// <param name="imageFileName">Dynamic target image filename.</param>
    /// <param name="uniqueId">Unique product identifier (defaults to MAC address if null/empty).</param>
    /// <param name="note">Status description or label note.</param>
    /// <returns>Formatted XML string.</returns>
    public static string GenerateEslImageInfoXml(string macAddress, string imageFileName, string? uniqueId, string note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageFileName);

        var cleanMac = macAddress.Trim().ToUpperInvariant();
        var effectiveId = string.IsNullOrWhiteSpace(uniqueId) ? cleanMac : uniqueId.Trim();

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("EslImageInfo",
                new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "xsd", Xsd.NamespaceName),
                new XElement("ImageFile", imageFileName.Trim()),
                new XElement("ID", effectiveId), // NOTE: Unique ID per tag prevents state overwrite in Opticon SQLite
                new XElement("Note", note ?? string.Empty),
                new XElement("Label", cleanMac)
            )
        );

        using var stringWriter = new Utf8StringWriter();
        doc.Save(stringWriter, SaveOptions.None);
        return stringWriter.ToString();
    }

    /// <summary>
    /// Backward-compatible overload defaulting to static {MAC}.png image filename.
    /// </summary>
    public static string GenerateEslImageInfoXml(string macAddress, string note)
    {
        return GenerateEslImageInfoXml(macAddress, $"{macAddress.Trim().ToUpperInvariant()}.png", uniqueId: macAddress, note);
    }

    /// <summary>
    /// Custom StringWriter with UTF-8 encoding support.
    /// </summary>
    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}

