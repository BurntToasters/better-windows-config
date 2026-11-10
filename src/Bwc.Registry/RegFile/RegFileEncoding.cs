using System.Text;

namespace Bwc.Registry;

/// <summary>
/// Shared constants and the ANSI code page used by the REGEDIT4 format. The code page is the
/// operating system's ANSI code page (GetACP), not <see cref="Encoding.Default"/>, which is
/// UTF-8 on modern .NET.
/// </summary>
internal static class RegFileEncoding
{
    internal const string V5Header = "Windows Registry Editor Version 5.00";
    internal const string Regedit4Header = "REGEDIT4";
    internal const string Crlf = "\r\n";

    private static readonly Encoding Ansi;

    static RegFileEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Ansi = Encoding.GetEncoding(0);
    }

    internal static Encoding AnsiEncoding => Ansi;

    internal static byte[] ConvertAnsiToUnicodeBytes(byte[] data) =>
        Encoding.Unicode.GetBytes(Ansi.GetString(data));

    internal static byte[] ConvertUnicodeToAnsiBytes(byte[] data) =>
        Ansi.GetBytes(RegistryValueCodec.DecodeStringRaw(data));
}
