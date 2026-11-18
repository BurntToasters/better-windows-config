using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace Bwc.Oracle.Resources.Msc;

/// Parses an MMC console file (gpedit.msc) as data. DTD processing is prohibited and no resolver is set, so the file
/// cannot pull in anything. Large text blocks (the base64 icons) are replaced by their length and SHA-256.
internal static class ConsoleFileParser
{
    private const int InlineTextLimit = 256;

    public static JsonObject Parse(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = 64 * 1024 * 1024,
        };

        XDocument document;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using XmlReader reader = XmlReader.Create(stream, settings);
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"{path} is not valid XML: {ex.Message}", ex);
        }

        XElement root = document.Root ?? throw new InvalidDataException($"{path} has no root element");
        if (root.Name.LocalName != "MMC_ConsoleFile")
        {
            throw new InvalidDataException($"{path} root element is '{root.Name.LocalName}', expected MMC_ConsoleFile");
        }

        return new JsonObject
        {
            ["generator"] = "Bwc.Oracle.Resources",
            ["schemaVersion"] = 1,
            ["source"] = new JsonObject
            {
                ["path"] = path,
                ["sizeBytes"] = bytes.LongLength,
                ["sha256"] = Hash(bytes),
            },
            ["console"] = ConsoleSettings(root),
            ["snapins"] = Snapins(root),
            ["strings"] = Strings(root),
            ["tree"] = Generic(root),
        };
    }

    private static JsonObject ConsoleSettings(XElement root)
    {
        var console = Attributes(root);
        XElement? frame = root.Element("FrameState");
        if (frame is not null)
        {
            console["frameState"] = Attributes(frame);
        }

        var views = new JsonArray();
        foreach (XElement view in root.Element("Views")?.Elements("View") ?? [])
        {
            JsonObject item = Attributes(view);
            if (view.Element("ViewOptions") is { } options)
            {
                item["viewOptions"] = Attributes(options);
            }
            views.Add(item);
        }
        console["views"] = views;
        return console;
    }

    private static JsonArray Snapins(XElement root)
    {
        var snapins = new JsonArray();
        foreach (XElement snapin in root.Descendants("SnapinCache").Elements("Snapin"))
        {
            JsonObject item = Attributes(snapin);
            var extensions = new JsonArray();
            foreach (XElement extension in snapin.Element("Extensions")?.Elements() ?? [])
            {
                extensions.Add(Attributes(extension));
            }
            if (snapin.Element("Extensions") is not null)
            {
                item["extensions"] = extensions;
            }
            snapins.Add(item);
        }
        return snapins;
    }

    private static JsonArray Strings(XElement root)
    {
        var strings = new JsonArray();
        foreach (XElement table in root.Descendants("StringTable"))
        {
            string guid = table.Element("GUID")?.Value ?? string.Empty;
            foreach (XElement s in table.Element("Strings")?.Elements("String") ?? [])
            {
                JsonObject item = Attributes(s);
                item["table"] = guid;
                item["text"] = s.Value;
                strings.Add(item);
            }
        }
        return strings;
    }

    private static JsonObject Attributes(XElement element)
    {
        var result = new JsonObject();
        foreach (XAttribute attribute in element.Attributes())
        {
            result[attribute.Name.LocalName] = attribute.Value;
        }
        return result;
    }

    private static JsonObject Generic(XElement element)
    {
        var node = new JsonObject { ["name"] = element.Name.LocalName };
        if (element.HasAttributes)
        {
            node["attributes"] = Attributes(element);
        }

        string text = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
        if (text.Length > InlineTextLimit)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            node["text"] = new JsonObject { ["length"] = utf8.Length, ["sha256"] = Hash(utf8) };
        }
        else if (text.Length > 0)
        {
            node["text"] = text;
        }

        if (element.HasElements)
        {
            var children = new JsonArray();
            foreach (XElement child in element.Elements())
            {
                children.Add(Generic(child));
            }
            node["children"] = children;
        }
        return node;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
