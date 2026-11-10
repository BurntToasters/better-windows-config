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
}
