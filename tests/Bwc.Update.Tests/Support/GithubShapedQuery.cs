namespace Bwc.Update.Tests;

/// <summary>A release-asset query shaped like GitHub's (about 1000 characters, with %2F, %3D, %3B and +).</summary>
internal static class GithubShapedQuery
{
    public static string Build()
    {
        var jwt = "eyJ" + new string('A', 380) + ".eyJ" + new string('B', 60) + ".c2ln" + new string('C', 40);
        return "sp=r&sv=2018-11-09&sr=b&spr=https"
            + "&se=2026-11-13T12%3A00%3A00Z"
            + "&rscd=attachment%3B+filename%3Dbwc-stable.json&rsct=application%2Foctet-stream"
            + "&skoid=" + Guid.NewGuid().ToString("D")
            + "&sktid=" + Guid.NewGuid().ToString("D")
            + "&skt=2026-11-13T11%3A00%3A00Z&ske=2026-11-13T12%3A00%3A00Z&sks=b&skv=2018-11-09"
            + "&sig=abc%2Fdef%3D"
            + "&jwt=" + jwt
            + "&response-content-disposition=attachment%3B%20filename%3Dbwc-stable.json"
            + "&response-content-type=application%2Foctet-stream";
    }
}
