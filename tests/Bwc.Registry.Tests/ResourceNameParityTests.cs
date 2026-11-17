using System.Xml.Linq;
using Bwc.Registry.Resources;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

/// <summary>
/// Keeps the resource names of the text export (<see cref="ResourceNames"/>, Bwc.Registry) equal to the .resw entries
/// that the viewers read (<c>ResourceText</c>, Bwc.UI). Bwc.UI has no reference to Bwc.Registry, so the .resw file is
/// read by path and each engine name is compared with the entry its viewer key names.
/// docs/failure-modes/resource-values.md section 8.
/// </summary>
[TestClass]
public sealed class ResourceNameParityTests
{
    private const string ResourcesPath = @"src\Bwc.UI\Strings\en-US\Resources.resw";
    private const string SolutionFileName = "BetterWindowsConfig.slnx";

    [TestMethod]
    public void InterfaceTypeNamesMatchTheUiResources()
    {
        Dictionary<string, string> resw = LoadResw();

        for (uint type = 0; type <= 11; type += 1)
        {
            Assert.AreEqual(
                Ui(resw, "ResourceBusType" + type.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ResourceNames.InterfaceType(type),
                "bus type " + type);
        }

        Assert.AreEqual(Ui(resw, "ResourceInvalid"), ResourceNames.InterfaceType(12), "bus type 12 is out of range");
        Assert.AreEqual(Ui(resw, "ResourceInvalid"), ResourceNames.InterfaceType(uint.MaxValue), "bus type -1 is out of range");
    }

    [TestMethod]
    public void DispositionAndDescriptorNamesMatchTheUiResources()
    {
        Dictionary<string, string> resw = LoadResw();

        Assert.AreEqual(Ui(resw, "ResourceUndetermined"), ResourceNames.ShareDisposition(0), "share 0");
        Assert.AreEqual(Ui(resw, "ResourceDeviceExclusive"), ResourceNames.ShareDisposition(1), "share 1");
        Assert.AreEqual(Ui(resw, "ResourceDriverExclusive"), ResourceNames.ShareDisposition(2), "share 2");
        Assert.AreEqual(Ui(resw, "ResourceShared"), ResourceNames.ShareDisposition(3), "share 3");
        Assert.AreEqual(Ui(resw, "ResourceInvalid"), ResourceNames.ShareDisposition(4), "share 4 is out of range");

        Assert.AreEqual(Ui(resw, "ResourceInvalid"), ResourceNames.PartialType(0), "partial 0 has no name");
        Assert.AreEqual(Ui(resw, "ResourcePortResource"), ResourceNames.PartialType(1), "partial 1");
        Assert.AreEqual(Ui(resw, "ResourceInterrupt"), ResourceNames.PartialType(2), "partial 2");
        Assert.AreEqual(Ui(resw, "ResourceMemoryResource"), ResourceNames.PartialType(3), "partial 3");
        Assert.AreEqual(Ui(resw, "ResourceDma"), ResourceNames.PartialType(4), "partial 4");
        Assert.AreEqual(Ui(resw, "ResourceDeviceSpecific"), ResourceNames.PartialType(5), "partial 5");
        Assert.AreEqual(Ui(resw, "ResourceInvalid"), ResourceNames.PartialType(6), "partial 6 has no name");

        Assert.AreEqual(Ui(resw, "ResourceReadWrite"), ResourceNames.MemoryAccess(0), "memory access 0");
        Assert.AreEqual(Ui(resw, "ResourceReadOnly"), ResourceNames.MemoryAccess(1), "memory access 1");
        Assert.AreEqual(Ui(resw, "ResourceWriteOnly"), ResourceNames.MemoryAccess(2), "memory access 2");
        Assert.AreEqual(Ui(resw, "ResourceInvalid"), ResourceNames.MemoryAccess(3), "memory access 3 is out of range");

        Assert.AreEqual(Ui(resw, "ResourceLevelSensitive"), ResourceNames.InterruptType(0), "interrupt, flag clear");
        Assert.AreEqual(Ui(resw, "ResourceLatched"), ResourceNames.InterruptType(1), "interrupt, latched flag");

        Assert.AreEqual(Ui(resw, "ResourceMemoryType"), ResourceNames.PortType(0), "port, flag clear");
        Assert.AreEqual(Ui(resw, "ResourcePortType"), ResourceNames.PortType(1), "port, I/O flag");
    }

    private static string Ui(Dictionary<string, string> resw, string key)
    {
        Assert.IsTrue(resw.TryGetValue(key, out string? text), "The .resw file has no entry named " + key + ".");
        return text!;
    }

    private static Dictionary<string, string> LoadResw()
    {
        string path = Path.Combine(FindRepositoryRoot(), ResourcesPath);
        Assert.IsTrue(File.Exists(path), "The UI resources are missing at " + path + ".");

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (XElement data in XDocument.Load(path).Root!.Elements("data"))
        {
            string? name = (string?)data.Attribute("name");
            string? value = (string?)data.Element("value");
            if (name is not null && value is not null)
            {
                entries[name] = value;
            }
        }

        return entries;
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(SolutionFileName + " was not found above " + AppContext.BaseDirectory + ".");
    }
}
