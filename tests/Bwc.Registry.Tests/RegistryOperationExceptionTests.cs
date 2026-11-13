using System.ComponentModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

[TestClass]
public sealed class RegistryOperationExceptionTests
{
    [TestMethod]
    public void CarriesOperationPathAndWin32Code()
    {
        var path = RegistryPath.Parse("HKCU\\Software\\BWC-E2E\\Sample");
        var exception = new RegistryOperationException("SetValue", path, 5);

        Assert.AreEqual(5, exception.Win32Error);
        Assert.AreEqual("SetValue", exception.Operation);
        Assert.AreEqual(path, exception.Path);
        Assert.AreEqual(unchecked((int)0x80070005u), exception.HResult);
    }

    [TestMethod]
    public void MessageContainsOperationPathCodeAndSystemText()
    {
        var path = RegistryPath.Parse("HKCU\\Software\\BWC-E2E\\Sample");
        var exception = new RegistryOperationException("DeleteKey", path, 2);
        string systemMessage = new Win32Exception(2).Message;

        StringAssert.Contains(exception.Message, "DeleteKey");
        StringAssert.Contains(exception.Message, path.ToDisplayString());
        StringAssert.Contains(exception.Message, "2");
        StringAssert.Contains(exception.Message, systemMessage);
        Assert.AreEqual(unchecked((int)0x80070002u), exception.HResult);
    }

    [TestMethod]
    public void HResultIsPreservedForCodesAboveSixteenBits()
    {
        var path = RegistryPath.Parse("HKLM\\Software");
        var exception = new RegistryOperationException("SetKeySecurity", path, 0x1_0000);

        Assert.AreEqual(0x1_0000, exception.Win32Error);
        Assert.AreEqual(new Exception().HResult, exception.HResult);
    }
}
