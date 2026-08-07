using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AirPlayReceiver.Protocol;

/// <summary>
/// Minimal Apple XML property list writer and reader, the text sibling of
/// <see cref="BinaryPlist"/>.
///
/// AirPlay uses both encodings in opposite directions: iOS sends us
/// <c>bplist00</c> bodies (SETUP, /play, /action), but the bodies the receiver
/// pushes back to the phone over the reverse (PTTH) channel — /event and the
/// FCUP request/response exchange — are XML plists with Content-Type
/// <c>text/x-apple-plist+xml</c>.
///
/// Values map exactly as <see cref="BinaryPlist"/> maps them:
/// <see cref="Dictionary{String,Object}"/>, <see cref="List{Object}"/>,
/// <see cref="long"/>, <see cref="double"/>, <see cref="bool"/>,
/// <see cref="string"/> and <see cref="byte"/>[] (<c>&lt;data&gt;</c>, base64),
/// so a plist parsed from one encoding can be re-emitted in the other.
///
/// Output matches CoreFoundation's <c>CFPropertyListCreateXMLData</c> layout:
/// XML declaration, Apple's DOCTYPE, <c>&lt;plist version="1.0"&gt;</c>, the
/// root container flush left, one tab per nesting level, LF line endings.
/// Dictionary keys are emitted in insertion order (CoreFoundation sorts them;
/// plist readers do not care, and insertion order keeps callers in control of
/// how a request body reads in a log).
///
/// Format reference: Apple PropertyList-1.0.dtd.
/// </summary>
public static class XmlPlist
{
    /// <summary>The Content-Type Apple uses for XML plist bodies on the wire.</summary>
    public const string ContentType = "text/x-apple-plist+xml";

    private const string Preamble =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
        "<plist version=\"1.0\">\n";

    private const string Epilogue = "</plist>\n";

    /// <summary>
    /// Container nesting limit while parsing. Same value and same reason as
    /// <see cref="BinaryPlist.MaxDepth"/> — an unbounded recursion over a body we
    /// did not write is a StackOverflowException, which .NET cannot catch — but
    /// declared here rather than referenced, because this file is deliberately
    /// free of dependencies so the harnesses can link it on its own.
    /// </summary>
    private const int MaxDepth = 32;

    // ── Writing ───────────────────────────────────────────────────────────────

    /// <summary>Serialises <paramref name="root"/> as a UTF-8 XML plist body (no BOM).</summary>
    public static byte[] Write(object? root) => Encoding.UTF8.GetBytes(WriteToString(root));

    /// <summary>Serialises <paramref name="root"/> as an XML plist string.</summary>
    public static string WriteToString(object? root)
    {
        var sb = new StringBuilder(Preamble);
        WriteObject(sb, root, 0);
        sb.Append(Epilogue);
        return sb.ToString();
    }

    private static void WriteObject(StringBuilder sb, object? o, int indent)
    {
        string pad = new('\t', indent);

        switch (o)
        {
            case null:
                // XML plists have no null representation (binary plists do). Fail
                // loudly rather than silently changing a value's type on the wire.
                throw new NotSupportedException("XML plist cannot serialize null — omit the key instead");

            case bool b:
                sb.Append(pad).Append(b ? "<true/>" : "<false/>").Append('\n');
                break;

            case string str:
                sb.Append(pad).Append("<string>").Append(Escape(str)).Append("</string>\n");
                break;

            case byte[] data:
                sb.Append(pad).Append("<data>").Append(Convert.ToBase64String(data)).Append("</data>\n");
                break;

            case int or long or short or uint or ushort or byte or sbyte:
                sb.Append(pad).Append("<integer>")
                  .Append(Convert.ToInt64(o, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture))
                  .Append("</integer>\n");
                break;

            case double or float or decimal:
                sb.Append(pad).Append("<real>")
                  .Append(Convert.ToDouble(o, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture))
                  .Append("</real>\n");
                break;

            case DateTime date:
                sb.Append(pad).Append("<date>")
                  .Append(date.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
                  .Append("</date>\n");
                break;

            // Accept any dictionary/sequence shape so callers can pass the
            // read-only or interface-typed collections they already have.
            case IDictionary<string, object?> dict:
            {
                if (dict.Count == 0) { sb.Append(pad).Append("<dict/>\n"); break; }
                sb.Append(pad).Append("<dict>\n");
                foreach (var (key, value) in dict)
                {
                    sb.Append(pad).Append('\t').Append("<key>").Append(Escape(key)).Append("</key>\n");
                    WriteObject(sb, value, indent + 1);
                }
                sb.Append(pad).Append("</dict>\n");
                break;
            }

            case IEnumerable<object?> list:
            {
                var items = list as IList<object?> ?? list.ToList();
                if (items.Count == 0) { sb.Append(pad).Append("<array/>\n"); break; }
                sb.Append(pad).Append("<array>\n");
                foreach (object? item in items)
                    WriteObject(sb, item, indent + 1);
                sb.Append(pad).Append("</array>\n");
                break;
            }

            default:
                throw new NotSupportedException($"XML plist cannot serialize {o.GetType()}");
        }
    }

    private static string Escape(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ── Parsing ───────────────────────────────────────────────────────────────

    /// <summary>True when <paramref name="buffer"/> looks like an XML plist (vs. a <c>bplist00</c> body).</summary>
    public static bool IsXmlPlist(byte[] buffer)
    {
        if (buffer.Length < 5) return false;
        string head = Encoding.ASCII.GetString(buffer, 0, Math.Min(buffer.Length, 512)).TrimStart();
        return head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<!DOCTYPE plist", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<plist", StringComparison.OrdinalIgnoreCase);
    }

    public static object? Parse(byte[] buffer) => Parse(Encoding.UTF8.GetString(buffer));

    public static object? Parse(string xml)
    {
        // DtdProcessing.Ignore + no resolver: Apple's DOCTYPE references an
        // external DTD over http, which must never be fetched (XXE / hang).
        var settings = new XmlReaderSettings
        {
            DtdProcessing            = DtdProcessing.Ignore,
            XmlResolver              = null,
            IgnoreWhitespace         = true,
            IgnoreComments           = true,
            IgnoreProcessingInstructions = true,
        };

        using var reader = XmlReader.Create(new StringReader(xml), settings);
        XDocument doc;
        try { doc = XDocument.Load(reader); }
        catch (XmlException ex) { throw new FormatException($"Not an XML plist: {ex.Message}", ex); }

        XElement? plist = doc.Root;
        if (plist is null || plist.Name.LocalName != "plist")
            throw new FormatException("Not an XML plist (missing <plist> root)");

        XElement? root = plist.Elements().FirstOrDefault();
        return root is null ? null : ReadElement(root, 0);
    }

    private static object? ReadElement(XElement e, int depth) => depth > MaxDepth
        ? throw new FormatException($"XML plist nests deeper than {MaxDepth}")
        : e.Name.LocalName switch
    {
        "dict"    => ReadDict(e, depth),
        "array"   => ReadArray(e, depth),
        "string"  => e.Value,
        "integer" => long.Parse(e.Value.Trim(), CultureInfo.InvariantCulture),
        "real"    => double.Parse(e.Value.Trim(), CultureInfo.InvariantCulture),
        "true"    => true,
        "false"   => false,
        "data"    => Convert.FromBase64String(StripWhitespace(e.Value)),
        "date"    => DateTime.Parse(e.Value.Trim(), CultureInfo.InvariantCulture,
                                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
        _         => throw new FormatException($"Unsupported XML plist element <{e.Name.LocalName}>"),
    };

    private static Dictionary<string, object?> ReadDict(XElement e, int depth)
    {
        var dict = new Dictionary<string, object?>();
        string? key = null;
        foreach (XElement child in e.Elements())
        {
            if (child.Name.LocalName == "key")
            {
                key = child.Value;
            }
            else
            {
                if (key is null) throw new FormatException("<dict> value without a preceding <key>");
                dict[key] = ReadElement(child, depth + 1);
                key = null;
            }
        }
        return dict;
    }

    private static List<object?> ReadArray(XElement e, int depth)
    {
        var list = new List<object?>();
        foreach (XElement child in e.Elements())
            list.Add(ReadElement(child, depth + 1));
        return list;
    }

    private static string StripWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }
}
