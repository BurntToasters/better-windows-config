using Bwc.Gpo.Store;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Gpo.Tests;

[TestClass]
public sealed class GptIniTests
{
    private const string Cse = GptIniProbe.RegistryCse;
    private const string MachineTool = GptIniProbe.MachineTool;
    private const string UserTool = GptIniProbe.UserTool;

    [TestMethod]
    public void Probe_RoundTripsByteForByte_WithNoChange()
    {
        var bytes = GptIniProbe.Bytes();
        var ini = GptIni.Parse(bytes);
        CollectionAssert.AreEqual(bytes, ini.ToBytes());
        Assert.IsFalse(ini.HasChanges);
    }

    [TestMethod]
    public void Probe_VersionHalves_AreSplitLowMachineHighUser()
    {
        var ini = GptIni.Parse(GptIniProbe.Bytes(720908));
        Assert.AreEqual(720908u, ini.Version);
        Assert.AreEqual((ushort)12, ini.MachineVersion);
        Assert.AreEqual((ushort)11, ini.UserVersion);
    }

    [TestMethod]
    public void BumpMachine_AddsOne_ToLowHalfOnly()
    {
        var ini = GptIni.Parse(GptIniProbe.Bytes(720908));
        ini.BumpVersion(GpoSide.Machine);
        CollectionAssert.AreEqual(GptIniProbe.Bytes(720909), ini.ToBytes());
        Assert.IsTrue(ini.HasChanges);
    }

    [TestMethod]
    public void BumpUser_AddsOne_ToHighHalfOnly()
    {
        var ini = GptIni.Parse(GptIniProbe.Bytes(720908));
        ini.BumpVersion(GpoSide.User);
        CollectionAssert.AreEqual(GptIniProbe.Bytes(720908 + 65536), ini.ToBytes());
        Assert.AreEqual((ushort)12, ini.MachineVersion);
        Assert.AreEqual((ushort)12, ini.UserVersion);
    }

    [TestMethod]
    public void BumpMachine_AtHalfOverflow_WrapsWithoutCarryIntoUserHalf()
    {
        var ini = GptIni.Parse(GptIniProbe.Bytes((3u << 16) | 0xFFFF));
        ini.BumpVersion(GpoSide.Machine);

        // C/Version_FromHighMachine step 2: a half at 65535 goes to 1, not 0, and does not carry into the other half.
        Assert.AreEqual((3u << 16) | 1u, ini.Version);
        Assert.AreEqual((ushort)3, ini.UserVersion);
        Assert.AreEqual((ushort)1, ini.MachineVersion);
    }

    [TestMethod]
    public void BumpUser_AtHalfOverflow_WrapsWithoutTouchingMachineHalf()
    {
        var ini = GptIni.Parse(GptIniProbe.Bytes(0xFFFF0005u));
        ini.BumpVersion(GpoSide.User);

        // C/Version_FromHighUser step 2: the high half at 65535 goes to 1 (the file reads 65536 there), the machine half stays.
        Assert.AreEqual((1u << 16) | 5u, ini.Version);
    }

    [TestMethod]
    public void EnsurePair_WhenPresent_ChangesNothingAndAddsNoDuplicate()
    {
        var bytes = GptIniProbe.Bytes();
        var ini = GptIni.Parse(bytes);
        ini.EnsureExtensionPair(GpoSide.Machine);
        ini.EnsureExtensionPair(GpoSide.User);
        ini.EnsureExtensionPair(GpoSide.Machine);
        CollectionAssert.AreEqual(bytes, ini.ToBytes());
        Assert.IsFalse(ini.HasChanges);
    }

    [TestMethod]
    public void NewFile_MachineSave_UsesProbeLineOrder()
    {
        var ini = GptIni.CreateNew();
        ini.EnsureExtensionPair(GpoSide.Machine);
        ini.BumpVersion(GpoSide.Machine);
        Assert.AreEqual(
            "[General]\r\ngPCMachineExtensionNames=[" + Cse + MachineTool + "]\r\nVersion=1\r\n",
            GptIniProbe.Latin1(ini.ToBytes()));
    }

    [TestMethod]
    public void NewFile_UserSave_WritesLinesInTheOrderTheyAreFirstWritten()
    {
        // Capture A/User_Enabled (D2): the store writes the extension line first, then Version, so the line comes first.
        var first = GptIni.CreateNew();
        first.EnsureExtensionPair(GpoSide.User);
        first.BumpVersion(GpoSide.User);

        var second = GptIni.CreateNew();
        second.BumpVersion(GpoSide.User);
        second.EnsureExtensionPair(GpoSide.User);

        Assert.AreEqual(
            "[General]\r\ngPCUserExtensionNames=[" + Cse + UserTool + "]\r\nVersion=65536\r\n",
            GptIniProbe.Latin1(first.ToBytes()));
        Assert.AreEqual(
            "[General]\r\nVersion=65536\r\ngPCUserExtensionNames=[" + Cse + UserTool + "]\r\n",
            GptIniProbe.Latin1(second.ToBytes()));
    }

    [TestMethod]
    public void NewFile_UserThenMachine_ReproducesTheCapturedFileExactly()
    {
        // Capture A/User_Then_Machine step 3 (D2): user line, Version=65537, machine line.
        var ini = GptIni.CreateNew();
        ini.EnsureExtensionPair(GpoSide.User);
        ini.BumpVersion(GpoSide.User);
        ini.EnsureExtensionPair(GpoSide.Machine);
        ini.BumpVersion(GpoSide.Machine);
        Assert.AreEqual(
            "[General]\r\n" + GptIniProbe.UserLine + "\r\nVersion=65537\r\n" + GptIniProbe.MachineLine + "\r\n",
            GptIniProbe.Latin1(ini.ToBytes()));
    }

    [TestMethod]
    public void NewFile_MachineThenUser_ReproducesTheCapturedFileExactly()
    {
        // Capture A/Machine_Then_User step 3 (D2): machine line, Version=65537, user line.
        var ini = GptIni.CreateNew();
        ini.EnsureExtensionPair(GpoSide.Machine);
        ini.BumpVersion(GpoSide.Machine);
        ini.EnsureExtensionPair(GpoSide.User);
        ini.BumpVersion(GpoSide.User);
        Assert.AreEqual(GptIniProbe.Text(65537), GptIniProbe.Latin1(ini.ToBytes()));
    }

    [TestMethod]
    public void UnknownLinesAndSections_AreKeptByteForByte_AndLineEndingsStay()
    {
        var text = "[General]\nVersion=1\n; keep me \xE9\r\n[Other]\nx = 1\r\ny=2";
        var ini = GptIni.Parse(GptIniProbe.Latin1(text));
        ini.BumpVersion(GpoSide.Machine);
        Assert.AreEqual(text.Replace("Version=1", "Version=2"), GptIniProbe.Latin1(ini.ToBytes()));
    }

    [TestMethod]
    public void MissingLines_AreAppendedAtTheEndOfTheSection()
    {
        // Captures A/User_Then_Machine and A/Machine_Then_User (D2): a missing line goes after the last non-blank line.
        var ini = GptIni.Parse(GptIniProbe.Latin1("[General]\nVersion=1\n; note\n"));
        ini.EnsureExtensionPair(GpoSide.Machine);
        ini.EnsureExtensionPair(GpoSide.User);
        Assert.AreEqual(
            "[General]\nVersion=1\n; note\ngPCMachineExtensionNames=[" + Cse + MachineTool + "]\ngPCUserExtensionNames=[" + Cse + UserTool + "]\n",
            GptIniProbe.Latin1(ini.ToBytes()));
    }

    [TestMethod]
    public void AppendingAfterAnUnterminatedLastLine_AddsTheTerminatorFirst()
    {
        var ini = GptIni.Parse(GptIniProbe.Latin1("[General]\r\nVersion=1"));
        ini.EnsureExtensionPair(GpoSide.User);
        Assert.AreEqual(
            "[General]\r\nVersion=1\r\ngPCUserExtensionNames=[" + Cse + UserTool + "]\r\n",
            GptIniProbe.Latin1(ini.ToBytes()));
    }

    [TestMethod]
    public void SpacesAroundEquals_AreKept_WhenTheValueChanges()
    {
        var ini = GptIni.Parse(GptIniProbe.Latin1("[General]\r\nVersion = 5\r\n"));
        ini.BumpVersion(GpoSide.Machine);
        Assert.AreEqual("[General]\r\nVersion = 6\r\n", GptIniProbe.Latin1(ini.ToBytes()));
    }

    [TestMethod]
    public void MissingVersion_ReadsAsZero_AndBumpInsertsTheLine()
    {
        var ini = GptIni.Parse(GptIniProbe.Latin1("[General]\r\n"));
        Assert.AreEqual(0u, ini.Version);
        ini.BumpVersion(GpoSide.Machine);
        Assert.AreEqual("[General]\r\nVersion=1\r\n", GptIniProbe.Latin1(ini.ToBytes()));
    }

    [TestMethod]
    public void NoGeneralSection_IsAddedAtTheTop_AndOtherSectionsStay()
    {
        var ini = GptIni.Parse(GptIniProbe.Latin1("[Other]\r\na=b\r\n"));
        ini.BumpVersion(GpoSide.User);
        Assert.AreEqual("[General]\r\nVersion=65536\r\n[Other]\r\na=b\r\n", GptIniProbe.Latin1(ini.ToBytes()));
    }

    [TestMethod]
    public void Utf8Bom_IsKept()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(GptIniProbe.Bytes(1)).ToArray();
        var ini = GptIni.Parse(bytes);
        Assert.AreEqual(1u, ini.Version);
        CollectionAssert.AreEqual(bytes, ini.ToBytes());
        ini.BumpVersion(GpoSide.Machine);
        CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(GptIniProbe.Bytes(2)).ToArray(), ini.ToBytes());
    }

    [TestMethod]
    public void Utf16Bom_IsRefused()
    {
        Assert.ThrowsExactly<GptIniFormatException>(() => GptIni.Parse(new byte[] { 0xFF, 0xFE, 0x5B, 0x00 }));
        Assert.ThrowsExactly<GptIniFormatException>(() => GptIni.Parse(new byte[] { 0xFE, 0xFF, 0x00, 0x5B }));
    }

    [TestMethod]
    [DataRow("Version=abc")]
    [DataRow("Version=")]
    [DataRow("Version=-1")]
    [DataRow("Version=4294967296")]
    [DataRow("Version=1.5")]
    public void NonNumericOrOutOfRangeVersion_IsRefused(string line)
    {
        Assert.ThrowsExactly<GptIniFormatException>(() => GptIni.Parse(GptIniProbe.Latin1("[General]\r\n" + line + "\r\n")));
    }

    [TestMethod]
    public void DuplicateVersionAndGeneralSection_FirstWins_RestUntouched()
    {
        var text = "[General]\r\nVersion=7\r\nVersion=9\r\n[General]\r\nVersion=11\r\n";
        var ini = GptIni.Parse(GptIniProbe.Latin1(text));
        Assert.AreEqual(7u, ini.Version);
        ini.BumpVersion(GpoSide.Machine);
        Assert.AreEqual(text.Replace("Version=7", "Version=8"), GptIniProbe.Latin1(ini.ToBytes()));
    }

    [TestMethod]
    public void OtherExtensionGroups_AreKept_AndToolJoinsTheRegistryGroupInOrder()
    {
        const string other = "{00000000-0000-0000-0000-000000000001}{00000000-0000-0000-0000-000000000002}";
        var text = "[General]\r\ngPCMachineExtensionNames=[" + other + "][" + Cse + "{AAAAAAAA-0000-0000-0000-000000000000}]\r\n";
        var ini = GptIni.Parse(GptIniProbe.Latin1(text));
        ini.EnsureExtensionPair(GpoSide.Machine);
        Assert.AreEqual(
            "[General]\r\ngPCMachineExtensionNames=[" + other + "][" + Cse + "{AAAAAAAA-0000-0000-0000-000000000000}" + MachineTool + "]\r\n",
            GptIniProbe.Latin1(ini.ToBytes()));
    }

    [TestMethod]
    public void NewRegistryGroup_IsInsertedBeforeTheFirstGreaterGuid_NeverReorderingExistingGroups()
    {
        const string low = "{00000000-0000-0000-0000-000000000001}{00000000-0000-0000-0000-000000000002}";
        const string high = "{FFFFFFFF-0000-0000-0000-000000000001}{00000000-0000-0000-0000-000000000002}";
        var ini = GptIni.Parse(GptIniProbe.Latin1("[General]\r\ngPCUserExtensionNames=[" + low + "][" + high + "]\r\n"));
        ini.EnsureExtensionPair(GpoSide.User);
        Assert.AreEqual(
            "[General]\r\ngPCUserExtensionNames=[" + low + "][" + Cse + UserTool + "][" + high + "]\r\n",
            GptIniProbe.Latin1(ini.ToBytes()));
    }

    [TestMethod]
    public void ExtensionGuidMatching_IsCaseInsensitive_SoNoDuplicateIsAdded()
    {
        var lower = (Cse + MachineTool).ToLowerInvariant();
        var text = "[General]\r\ngPCMachineExtensionNames=[" + lower + "]\r\n";
        var ini = GptIni.Parse(GptIniProbe.Latin1(text));
        ini.EnsureExtensionPair(GpoSide.Machine);
        Assert.AreEqual(text, GptIniProbe.Latin1(ini.ToBytes()));
    }

    [TestMethod]
    public void MalformedExtensionLine_RefusesTheEdit_AndLeavesTheBytes()
    {
        var text = "[General]\r\ngPCMachineExtensionNames=garbage\r\nVersion=1\r\n";
        var ini = GptIni.Parse(GptIniProbe.Latin1(text));
        Assert.ThrowsExactly<GptIniFormatException>(() => ini.EnsureExtensionPair(GpoSide.Machine));
        Assert.AreEqual(text, GptIniProbe.Latin1(ini.ToBytes()));
        ini.EnsureExtensionPair(GpoSide.User);
        Assert.IsTrue(ini.HasChanges, "the other side's line is independent");
    }

    [TestMethod]
    public void OverTheSizeCap_IsRefused()
    {
        var big = new byte[GptIni.MaxBytes + 1];
        Array.Fill(big, (byte)'a');
        Assert.ThrowsExactly<GptIniFormatException>(() => GptIni.Parse(big));
    }

    [TestMethod]
    public void EmptyFile_IsAFileWithNoLines()
    {
        var ini = GptIni.Parse([]);
        Assert.AreEqual(0u, ini.Version);
        ini.BumpVersion(GpoSide.Machine);
        Assert.AreEqual("[General]\r\nVersion=1\r\n", GptIniProbe.Latin1(ini.ToBytes()));
    }
}
