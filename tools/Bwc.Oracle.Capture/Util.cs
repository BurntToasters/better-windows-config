using System.Text;

namespace Bwc.Oracle.Capture;

internal static class Util
{
    internal static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();

    // Writes text with LF line endings (callers build LF text) and no byte-order mark.
    internal static void WriteUtf8NoBom(string path, string text)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));
    }

    internal static void WriteBytes(string path, byte[] bytes)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(path, bytes);
    }
}
