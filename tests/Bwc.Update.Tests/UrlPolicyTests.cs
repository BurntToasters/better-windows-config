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
        var url = "https://example.invalid/" + new string('a', 2100);
        Expect.Fails(UrlPolicy.Validate(url, requireHttps: true), UpdateFailure.UrlRejected);
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
