using Bwc.Gpo.Comments;
using Bwc.Gpo.Store;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Gpo.Tests;

/// <summary>
/// Read-only invariant over this machine's real policy folder. Nothing here writes, and the file holds no write API.
/// The content differs between machines, so the test asserts only that what is on disk round-trips unchanged.
/// </summary>
[TestClass]
public sealed class GpoStoreRealFolderTests
{
    private static byte[]? ReadShared(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    [TestMethod]
    public void RealFolder_GptIniAndBothCmtx_SerializeByteForByte()
    {
        var root = Path.Combine(Environment.SystemDirectory, "GroupPolicy");
        if (!Directory.Exists(root))
        {
            Assert.Inconclusive("This machine has no local group policy folder.");
        }

        var checkedFiles = 0;
        var gptBytes = ReadShared(Path.Combine(root, "gpt.ini"));
        if (gptBytes is not null)
        {
            CollectionAssert.AreEqual(gptBytes, GptIni.Parse(gptBytes).ToBytes(), "gpt.ini");
            checkedFiles++;
        }

        foreach (var folder in new[] { "Machine", "User" })
        {
            var bytes = ReadShared(Path.Combine(root, folder, "comment.cmtx"));
            if (bytes is not null)
            {
                CollectionAssert.AreEqual(bytes, CmtxDocument.Parse(bytes).ToBytes(), folder + " comment file");
                checkedFiles++;
            }
        }

        if (checkedFiles == 0)
        {
            Assert.Inconclusive("The folder holds none of the three files.");
        }
    }

    [TestMethod]
    public void RealFolder_OpenedThroughTheStore_ReadsWithoutWriting()
    {
        var root = Path.Combine(Environment.SystemDirectory, "GroupPolicy");
        if (!Directory.Exists(root))
        {
            Assert.Inconclusive("This machine has no local group policy folder.");
        }

        var before = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => (p, File.GetLastWriteTimeUtc(p))).OrderBy(t => t.p, StringComparer.OrdinalIgnoreCase).ToArray();

        var snapshot = new GpoStore(new SystemGpoStorePolicy()).Open();

        var after = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => (p, File.GetLastWriteTimeUtc(p))).OrderBy(t => t.p, StringComparer.OrdinalIgnoreCase).ToArray();
        CollectionAssert.AreEqual(before, after);
        Assert.IsNull(snapshot.GptIniFailure);
        Assert.IsNull(snapshot.Machine.CommentsFailure);
        Assert.IsNull(snapshot.User.CommentsFailure);
    }
}
