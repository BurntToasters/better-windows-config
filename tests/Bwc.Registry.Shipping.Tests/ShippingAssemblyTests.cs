using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Shipping.Tests;

[TestClass]
public sealed class ShippingAssemblyTests
{
    [TestMethod]
    public void ShippingAssemblyExcludesTheSandboxPolicyAndExposesThePublicContracts()
    {
        var assembly = typeof(RegistryWriter).Assembly;

        Assert.IsNull(
            assembly.GetType("Bwc.Registry.SandboxWritePolicy", throwOnError: false),
            "The E2E sandbox policy must not be compiled into the shipping assembly.");

        Type? shippingPolicy = assembly.GetType("Bwc.Registry.ShippingWritePolicy", throwOnError: false);
        Assert.IsNotNull(shippingPolicy);
        Assert.IsTrue(shippingPolicy!.IsPublic);

        Type? policyContract = assembly.GetType("Bwc.Registry.IRegistryWritePolicy", throwOnError: false);
        Assert.IsNotNull(policyContract);
        Assert.IsTrue(policyContract!.IsPublic);
    }

    [TestMethod]
    public void ShippingAssemblyHasNoCountedNameWriteSeam()
    {
        // The E2E-only counted-name seam (NtSetValueKey and NtDeleteValueKey) must not reach a
        // shipping build. Shipping names reach native code only through RegSetValueExW and
        // RegDeleteValueW, which cannot pass an embedded NUL.
        Type writer = typeof(RegistryWriter);
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        Assert.IsNull(writer.GetMethod("SetValueWithCountedName", All));
        Assert.IsNull(writer.GetMethod("DeleteValueWithCountedName", All));
    }

    [TestMethod]
    public void ShippingShipsTheDenyAllPolicyAndKeepsLinkFollowingBehavior()
    {
        var assembly = typeof(RegistryWriter).Assembly;

        Type? denyAll = assembly.GetType("Bwc.Registry.DenyAllWritePolicy", throwOnError: false);
        Assert.IsNotNull(denyAll, "DenyAllWritePolicy refuses writes and must ship in every flavor.");
        Assert.IsTrue(denyAll!.IsPublic);

        IRegistryWritePolicy shipping = new ShippingWritePolicy();
        Assert.IsFalse(shipping.RejectsSymbolicLinks, "Shipping writes follow symbolic links as regedit does.");
        Assert.IsTrue(shipping.AllowsLinkValues);
    }
}
