using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// The owner of one COM pointer (update-elevation.md HN8). The fake object counts what it hands out and what is
// released.
[TestClass]
public sealed class ComObjectTests
{
    private static readonly Guid IidKnown = new("11111111-2222-3333-4444-555555555555");
    private static readonly Guid IidUnknownToFake = new("66666666-7777-8888-9999-000000000000");

    // HN8
    [TestMethod]
    public void ComObject_Dispose_Releases_Once()
    {
        using var fake = new FakeCom("one", IidKnown);
        var owner = ComObject.Own(fake.HandOut());

        Assert.IsNotNull(owner);
        Assert.AreEqual(fake.Pointer, owner.Pointer);
        Assert.AreEqual(0, fake.Releases, "nothing is released while the owner lives");

        owner.Dispose();
        owner.Dispose();

        Assert.AreEqual(1, fake.Releases, fake.Balance());
        Assert.IsTrue(fake.IsBalanced, fake.Balance());
        Assert.AreEqual((nint)0, owner.Pointer, "a released owner holds no pointer");
    }

    // HN8
    [TestMethod]
    public void ComObject_Own_Null_Is_Null()
    {
        Assert.IsNull(ComObject.Own(0));
    }

    // HN8
    [TestMethod]
    public void ComObject_QueryInterface_Failure_Returns_Nothing()
    {
        using var fake = new FakeCom("one", IidKnown);
        using (var owner = ComObject.Own(fake.HandOut())!)
        {
            int hr = owner.QueryInterface(IidUnknownToFake, out ComObject? other);

            Assert.IsTrue(hr < 0, "an interface the object lacks fails");
            Assert.IsNull(other);
        }

        Assert.IsTrue(fake.IsBalanced, fake.Balance());
        Assert.AreEqual(1, fake.Releases);
    }

    // HN8
    [TestMethod]
    public void ComObject_QueryInterface_Adds_One_Reference()
    {
        using var fake = new FakeCom("one", IidKnown);
        using (var owner = ComObject.Own(fake.HandOut())!)
        {
            int hr = owner.QueryInterface(IidKnown, out ComObject? other);

            Assert.AreEqual(0, hr);
            Assert.IsNotNull(other);
            Assert.AreEqual(2, fake.Handouts, "the hand-out and the query");
            other.Dispose();
            Assert.AreEqual(1, fake.Releases, "the query result releases its own reference");
        }

        Assert.AreEqual(2, fake.Releases);
        Assert.IsTrue(fake.IsBalanced, fake.Balance());
    }

    // HN8
    [TestMethod]
    public void ComObject_QueryInterface_On_Released_Owner_Fails_Without_A_Call()
    {
        using var fake = new FakeCom("one", IidKnown);
        var owner = ComObject.Own(fake.HandOut())!;
        owner.Dispose();

        int hr = owner.QueryInterface(IidKnown, out ComObject? other);

        Assert.IsTrue(hr < 0);
        Assert.IsNull(other);
        Assert.AreEqual(1, fake.Handouts, "no query reached the object");
        Assert.IsTrue(fake.IsBalanced, fake.Balance());
    }
}
