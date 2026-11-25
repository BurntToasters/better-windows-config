using System.Text;

namespace Bwc.Gpo.Tests;

/// <summary>The gpt.ini bytes classic wrote in the 2026-11-18 probe (ANSI, CRLF), and helpers to build variants.</summary>
internal static class GptIniProbe
{
    public const string RegistryCse = "{35378EAC-683F-11D2-A89A-00C04FBBCFA2}";
    public const string MachineTool = "{D02B1F72-3407-48AE-BA88-E8213C6761F1}";
    public const string UserTool = "{D02B1F73-3407-48AE-BA88-E8213C6761F1}";

    public static string MachineLine => "gPCMachineExtensionNames=[" + RegistryCse + MachineTool + "]";

    public static string UserLine => "gPCUserExtensionNames=[" + RegistryCse + UserTool + "]";

    public static string Text(uint version) =>
        "[General]\r\n" + MachineLine + "\r\nVersion=" + version + "\r\n" + UserLine + "\r\n";

    public static byte[] Bytes(uint version = 720908) => Encoding.Latin1.GetBytes(Text(version));

    public static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    public static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);
}
