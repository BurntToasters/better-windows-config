using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

[TestClass]
public sealed class UrlPolicyTests
{
    [TestMethod]
    public void Http_Url_Rejected_When_Https_Required()
    {
        Expect.Fails(UrlPolicy.Validate("http://example.invalid/x.exe", requireHttps: true), UpdateFailure.UrlRejected);
    }

    [TestMethod]
    public void Http_Url_Accepted_When_Https_Not_Required()
    {
        Assert.IsTrue(UrlPolicy.Validate("http://127.0.0.1:8080/x.exe", requireHttps: false).Ok);
    }

    [TestMethod]
    [DataRow("file:///C:/x.exe")]
    [DataRow("ftp://example.invalid/x.exe")]
    [DataRow("javascript:alert(1)")]
    [DataRow("data:text/plain,x")]
    [DataRow("ws://example.invalid/x")]
    public void Non_Http_Scheme_Rejected(string url)
    {
        Assert.IsFalse(UrlPolicy.Validate(url, requireHttps: false).Ok, url);
    }

    [TestMethod]
    [DataRow("https://user@example.invalid/x.exe")]
    [DataRow("https://user:secret@example.invalid/x.exe")]
    public void Url_UserInfo_Rejected(string url)
    {
        Expect.Fails(UrlPolicy.Validate(url, requireHttps: true), UpdateFailure.UrlRejected);
    }

    [TestMethod]
    public void Url_Fragment_Rejected()
    {
        Expect.Fails(UrlPolicy.Validate("https://example.invalid/x.exe#frag", requireHttps: true), UpdateFailure.UrlRejected);
    }

    [TestMethod]
    [DataRow("https://example.invalid/a/../b")]
    [DataRow("https://example.invalid/./b")]
    [DataRow("https://example.invalid/a/..")]
    [DataRow("https://example.invalid/..")]
    public void Url_Dot_Segment_Rejected(string url)
    {
        Expect.Fails(UrlPolicy.Validate(url, requireHttps: true), UpdateFailure.UrlRejected);
    }

    [TestMethod]
    [DataRow("https://example.invalid/a/%2e%2e/b")]
    [DataRow("https://example.invalid/a%2F..%2Fb")]
    [DataRow("https://example.invalid/a%5C..")]
    [DataRow("https://example.invalid/a%00b")]
    [DataRow("https://example.invalid/%2E%2E/b")]
    public void Url_Encoded_Traversal_Rejected(string url)
    {
        Expect.Fails(UrlPolicy.Validate(url, requireHttps: true), UpdateFailure.UrlRejected);
    }

    [TestMethod]
    [DataRow("https://example.invalid/a\\b")]
    [DataRow("https://example.invalid/a b")]
    [DataRow("https://example.invalid/a\tb")]
    [DataRow("https://example.invalid/a\u0001b")]
    [DataRow("https://example.invalid/a\"b")]
    public void Url_Bad_Characters_Rejected(string url)
    {
        Expect.Fails(UrlPolicy.Validate(url, requireHttps: true), UpdateFailure.UrlRejected);
    }

    [TestMethod]
    public void Url_TooLong_Rejected()
    {
        var url = "https://example.invalid/" + new string('a', 4100);
        Expect.Fails(UrlPolicy.Validate(url, requireHttps: true), UpdateFailure.UrlRejected);
    }

    [TestMethod]
    public void Url_At_Limit_Accepted()
    {
        const string prefix = "https://example.invalid/?q=";
        var url = prefix + new string('a', 4096 - prefix.Length);
        Assert.AreEqual(4096, url.Length);
        Assert.IsTrue(UrlPolicy.Validate(url, requireHttps: true).Ok);
    }

    [TestMethod]
    public void Github_Shaped_Query_Accepted()
    {
        var url = "https://release-assets.example.invalid/asset/1/guid?" + GithubShapedQuery.Build();
        Assert.IsTrue(url.Length is >= 900 and <= 1100, url.Length.ToString());
        Assert.IsTrue(UrlPolicy.Validate(url, requireHttps: true).Ok);
    }

    [TestMethod]
    [DataRow("https://example.invalid/a%2Fb?x=1")]
    [DataRow("https://example.invalid/a%2fb?x=1")]
    [DataRow("https://example.invalid/a%5Cb?x=1&y=2")]
    [DataRow("https://example.invalid/%2E%2E/b?x=1")]
    public void Encoded_Separator_In_Path_Refused_With_Query(string url)
    {
        Expect.Fails(UrlPolicy.Validate(url, requireHttps: true), UpdateFailure.UrlRejected);
    }

    [TestMethod]
    [DataRow("https://example.invalid/a?x=a b")]
    [DataRow("https://example.invalid/a?x=a\\b")]
    [DataRow("https://example.invalid/a?x=a\"b")]
    [DataRow("https://example.invalid/a?x=aéb")]
    [DataRow("https://example.invalid/a?x=a\u0001b")]
    [DataRow("https://example.invalid/a?x=a#b")]
    public void Query_Bad_Characters_Rejected(string url)
    {
        Expect.Fails(UrlPolicy.Validate(url, requireHttps: true), UpdateFailure.UrlRejected);
    }

    [TestMethod]
    public void Redirect_Raw_Space_In_Query_Rejected()
    {
        var from = new Uri("https://example.invalid/x.exe");
        Expect.Fails(UrlPolicy.ValidateRedirect(from, "https://cdn.example.invalid/y?x=a b", requireHttps: true), UpdateFailure.UrlRejected);
    }

    [TestMethod]
    [DataRow("https://")]
    [DataRow("")]
    [DataRow("not a url")]
    public void Url_Without_Host_Rejected(string url)
    {
        Assert.IsFalse(UrlPolicy.Validate(url, requireHttps: true).Ok, url);
    }

    [TestMethod]
    public void Redirect_To_Http_Rejected()
    {
        var from = new Uri("https://example.invalid/x.exe");
        Expect.Fails(UrlPolicy.ValidateRedirect(from, "http://cdn.example.invalid/y.exe", requireHttps: true), UpdateFailure.UrlRejected);
    }

    [TestMethod]
    public void Redirect_To_Other_Host_Accepted()
    {
        var from = new Uri("https://example.invalid/x.exe");
        Assert.IsTrue(UrlPolicy.ValidateRedirect(from, "https://cdn.example.invalid/y.exe", requireHttps: true).Ok);
    }

    [TestMethod]
    public void Redirect_Relative_Accepted_And_Traversal_Rejected()
    {
        var from = new Uri("https://example.invalid/bwc/x.exe");
        Assert.IsTrue(UrlPolicy.ValidateRedirect(from, "/bwc/y.exe", requireHttps: true).Ok);
        Expect.Fails(UrlPolicy.ValidateRedirect(from, "../../etc/y.exe", requireHttps: true), UpdateFailure.UrlRejected);
    }
}
