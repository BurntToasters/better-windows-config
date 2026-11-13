using System.ComponentModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

/// <summary>
/// Covers remote connection argument validation, own-computer rejection, the local-name check,
/// and the exception payload. No test opens a network connection or touches the registry.
/// </summary>
[TestClass]
public sealed class RemoteRegistryTests
{
    private const int ErrorBadNetPath = 53;

    [TestMethod]
    public void ConnectRejectsNullComputerName()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => RemoteRegistryConnection.Connect(null!));
    }

    [TestMethod]
    public void ConnectRejectsEmptyWhitespaceNulAndSeparatorNames()
    {
        string[] names = { string.Empty, "   ", "bad\0name", @"a\b", "a/b" };

        foreach (string name in names)
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => RemoteRegistryConnection.Connect(name),
                "The name should be rejected.");
        }
    }

    [TestMethod]
    public void ConnectRejectsNamesLongerThanTheRemoteApiLimit()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => RemoteRegistryConnection.Connect(new string('a', 256)));
    }

    [TestMethod]
    public void ConnectRejectsTheOwnComputerBeforeAnyNativeCall()
    {
        string[] names = { Environment.MachineName, ".", "localhost" };

        foreach (string name in names)
        {
            var error = Assert.ThrowsExactly<RemoteRegistryException>(
                () => RemoteRegistryConnection.Connect(name),
                name);

            Assert.AreEqual(RemoteRegistryFailureReason.OwnComputer, error.Reason, name);
            Assert.AreEqual(name, error.ComputerName, name);
            Assert.AreEqual(0, error.Win32Error, name);
        }
    }

    [TestMethod]
    public void IsLocalComputerRecognizesLocalNamesCaseInsensitively()
    {
        Assert.IsTrue(RemoteRegistryConnection.IsLocalComputer(Environment.MachineName));
        Assert.IsTrue(
            RemoteRegistryConnection.IsLocalComputer(Environment.MachineName.ToUpperInvariant()));
        Assert.IsTrue(RemoteRegistryConnection.IsLocalComputer("."));
        Assert.IsTrue(RemoteRegistryConnection.IsLocalComputer("localhost"));
        Assert.IsTrue(RemoteRegistryConnection.IsLocalComputer("LOCALHOST"));
        Assert.IsFalse(RemoteRegistryConnection.IsLocalComputer("bwc-remote-test"));
    }

    [TestMethod]
    public void RemoteRegistryExceptionCarriesConnectionDetails()
    {
        const string computerName = "bwc-remote-test";
        const string operation = "RegConnectRegistry(HKEY_LOCAL_MACHINE)";
        var exception = new RemoteRegistryException(
            computerName,
            operation,
            ErrorBadNetPath,
            RemoteRegistryFailureReason.ConnectFailed);

        Assert.AreEqual(computerName, exception.ComputerName);
        Assert.AreEqual(operation, exception.Operation);
        Assert.AreEqual(ErrorBadNetPath, exception.Win32Error);
        Assert.AreEqual(RemoteRegistryFailureReason.ConnectFailed, exception.Reason);
        Assert.AreEqual(unchecked((int)0x80070035u), exception.HResult);

        string systemMessage = new Win32Exception(ErrorBadNetPath).Message;
        StringAssert.Contains(exception.Message, computerName);
        StringAssert.Contains(exception.Message, operation);
        StringAssert.Contains(exception.Message, "53");
        StringAssert.Contains(exception.Message, systemMessage);
    }

    [TestMethod]
    public void OwnComputerFailureUsesTheWin32HResultBase()
    {
        var exception = new RemoteRegistryException(
            "localhost",
            "Connect",
            0,
            RemoteRegistryFailureReason.OwnComputer);

        Assert.AreEqual(unchecked((int)0x80070000u), exception.HResult);
        StringAssert.Contains(exception.Message, "localhost");
        StringAssert.Contains(exception.Message, "own computer");
    }
}
