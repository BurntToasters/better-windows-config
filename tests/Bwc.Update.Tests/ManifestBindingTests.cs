using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// The signed metadata of a manifest (docs/failure-modes/update-verifier.md, section L). Each case is
// a signed manifest served over loopback and checked by the whole check step.
[TestClass]
public sealed class ManifestBindingTests
{
    [TestMethod]
    public async Task Manifest_Expires_Future_Accepted()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.Publish(expires: Scenario.DefaultExpires(TimeSpan.FromDays(1)));

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

        Assert.IsTrue(outcome.Ok, $"{outcome.Failure}: {outcome.Detail}");
    }

    [TestMethod]
    public async Task Manifest_Expired_Rejected()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.Publish(expires: Scenario.DefaultExpires(TimeSpan.FromHours(-1)));

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.ManifestExpired);
    }

    [TestMethod]
    public async Task Manifest_Expires_At_Now_Rejected()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.Publish(expires: Scenario.DefaultExpires(TimeSpan.Zero));

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.ManifestExpired);
    }

    [TestMethod]
    public async Task Comment_Missing_Channel_Rejected()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.Publish(rawComment: $"version:{Scenario.DefaultVersion} expires:{Scenario.DefaultExpires()}");

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.ManifestInvalid);
    }

    [TestMethod]
    [DataRow("timestamp:1794102823 version:{V} channel:{C} expires:{E}")]
    [DataRow("channel:{C} version:{V} expires:{E}")]
    [DataRow("version:{V}  channel:{C} expires:{E}")]
    [DataRow("version:{V} channel:{C} expires:{E} extra:1")]
    [DataRow("version:{V}\tchannel:{C}\texpires:{E}")]
    public async Task Comment_Format_Not_Exact_Rejected(string template)
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        var comment = template
            .Replace("{V}", Scenario.DefaultVersion, StringComparison.Ordinal)
            .Replace("{C}", s.Channel, StringComparison.Ordinal)
            .Replace("{E}", Scenario.DefaultExpires(), StringComparison.Ordinal);
        s.Publish(rawComment: comment);

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.ManifestInvalid);
    }

    [TestMethod]
    public async Task Comment_Channel_Mismatch_Rejected()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.Publish(commentChannel: "stable");

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.ManifestInvalid);
    }

    [TestMethod]
    [DataRow("2099-01-01T00:00:00+00:00")]
    [DataRow("2099-01-01T00:00:00.000Z")]
    [DataRow("2099-01-01")]
    [DataRow("2099-02-30T00:00:00Z")]
    [DataRow("2099-01-01T00:00:00")]
    public async Task Comment_Expires_Format_Rejected(string expires)
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.Publish(rawComment: $"version:{Scenario.DefaultVersion} channel:beta expires:{expires}");

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.ManifestInvalid);
    }

    [TestMethod]
    public async Task Comment_Version_Not_SemVer_Rejected()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.Publish(rawComment: $"version:v{Scenario.DefaultVersion} channel:beta expires:{Scenario.DefaultExpires()}");

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.ManifestInvalid);
    }

    [TestMethod]
    public async Task Comment_Unknown_Channel_Rejected()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.Publish(rawComment: $"version:{Scenario.DefaultVersion} channel:nightly expires:{Scenario.DefaultExpires()}");

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.ManifestInvalid);
    }
}
