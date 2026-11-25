using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Core.Tests;

// The pure rule behind the single-instance client check (docs/failure-modes/core.md, SI-I1 to SI-I3).
[TestClass]
public sealed class IntegrityPolicyTests
{
    private static readonly IntegrityLevel[] Known =
    [
        IntegrityLevel.Low,
        IntegrityLevel.Medium,
        IntegrityLevel.High,
        IntegrityLevel.System,
    ];

    [TestMethod]
    public void Accepts_ClientBelowServer_IsRefused()
    {
        foreach (IntegrityLevel server in Known)
        {
            foreach (IntegrityLevel client in Known)
            {
                if (client < server)
                {
                    Assert.IsFalse(
                        IntegrityPolicy.Accepts(server, client),
                        "a " + client + " client must be refused by a " + server + " server.");
                }
            }
        }
    }

    [TestMethod]
    public void Accepts_ClientAtOrAboveServer_IsAccepted()
    {
        foreach (IntegrityLevel server in Known)
        {
            foreach (IntegrityLevel client in Known)
            {
                if (client >= server)
                {
                    Assert.IsTrue(
                        IntegrityPolicy.Accepts(server, client),
                        "a " + client + " client must be accepted by a " + server + " server.");
                }
            }
        }
    }

    [TestMethod]
    public void Accepts_UnknownClient_IsRefused()
    {
        foreach (IntegrityLevel server in Known)
        {
            Assert.IsFalse(
                IntegrityPolicy.Accepts(server, IntegrityLevel.Unknown),
                "a client whose level cannot be read must be refused by a " + server + " server.");
        }
    }

    [TestMethod]
    public void Accepts_UnknownServer_IsRefused()
    {
        foreach (IntegrityLevel client in Known)
        {
            Assert.IsFalse(
                IntegrityPolicy.Accepts(IntegrityLevel.Unknown, client),
                "a server that does not know its own level fails closed.");
        }

        Assert.IsFalse(IntegrityPolicy.Accepts(IntegrityLevel.Unknown, IntegrityLevel.Unknown));
    }

    [TestMethod]
    public void FromRid_MapsTheMandatoryLabelRanges()
    {
        Assert.AreEqual(IntegrityLevel.Unknown, IntegrityPolicy.FromRid(0));
        Assert.AreEqual(IntegrityLevel.Unknown, IntegrityPolicy.FromRid(0xFFF));
        Assert.AreEqual(IntegrityLevel.Low, IntegrityPolicy.FromRid(0x1000));
        Assert.AreEqual(IntegrityLevel.Medium, IntegrityPolicy.FromRid(0x2000));
        Assert.AreEqual(IntegrityLevel.Medium, IntegrityPolicy.FromRid(0x2100));
        Assert.AreEqual(IntegrityLevel.High, IntegrityPolicy.FromRid(0x3000));
        Assert.AreEqual(IntegrityLevel.System, IntegrityPolicy.FromRid(0x4000));
        Assert.AreEqual(IntegrityLevel.System, IntegrityPolicy.FromRid(0x5000));
    }
}
