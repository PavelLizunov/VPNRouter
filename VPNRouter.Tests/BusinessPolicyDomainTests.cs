using System.Text.Json;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class BusinessPolicyDomainTests
{
    [Fact]
    public void NormalizeInput_SharedFixture_CoversUrlsIdnaAndPublicSuffixExceptions()
    {
        using var stream = typeof(BusinessPolicyDomainTests).Assembly.GetManifestResourceStream(
            "VPNRouter.Tests.Fixtures.BusinessPolicy.domains.json");
        Assert.NotNull(stream);
        using var fixture = JsonDocument.Parse(stream);
        foreach (var test in fixture.RootElement.GetProperty("normalization").EnumerateArray())
        {
            var input = test.GetProperty("input").GetString()!;
            var expected = test.GetProperty("expected");
            if (expected.ValueKind == JsonValueKind.Null)
                Assert.Throws<InvalidDataException>(() => BusinessPolicyDomains.NormalizeInput(input));
            else Assert.Equal(expected.GetString(), BusinessPolicyDomains.NormalizeInput(input));
        }
    }

    [Theory]
    [InlineData("EXAMPLE.com")]
    [InlineData("example.com.")]
    [InlineData("bücher.example")]
    [InlineData("https://example.com/path")]
    [InlineData(" example.com")]
    [InlineData("co.uk")]
    [InlineData("github.io")]
    [InlineData("a.ck")]
    [InlineData("x.kawasaki.jp")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("127.1")]
    [InlineData("999.1.1.1")]
    [InlineData("*.example.com")]
    [InlineData("example..com")]
    [InlineData("site_.example")]
    [InlineData("site-.example")]
    [InlineData("xn--.example")]
    public void ValidateRules_RejectsNoncanonicalOrOverbroadWireDomains(string domain) =>
        Assert.Throws<InvalidDataException>(() => BusinessPolicyDomains.ValidateRules([new(domain, "direct", "")]));

    [Theory]
    [InlineData("uiscom.ru")]
    [InlineData("sip.uiscom.ru")]
    [InlineData("uiscom.co")]
    [InlineData("comagic.ru")]
    [InlineData("callbackhunter.com")]
    [InlineData("admin.corp.ninitux.com")]
    [InlineData("child.admin.corp.ninitux.com")]
    [InlineData("corp.ninitux.com")]
    [InlineData("ninitux.com")]
    public void ValidateRules_RejectsProtectedDomainsAndIntersections_ForEitherAction(string domain)
    {
        Assert.True(BusinessPolicyDomains.IsProtected(domain));
        foreach (var action in new[] { "direct", "proxy" })
            Assert.Throws<InvalidDataException>(() => BusinessPolicyDomains.ValidateRules([new(domain, action, "")]));
    }

    [Fact]
    public void ValidateRules_AllowsBothDirectionsAndSameDirectionChildren_UsingLabelBoundaries()
    {
        BusinessPolicyDomains.ValidateRules([
            new("example.com", "direct", ""), new("sub.example.com", "direct", ""),
            new("notexample.com", "proxy", ""), new("example.org", "proxy", ""),
            new("notuiscom.ru", "proxy", ""), new("xn--bcher-kva.example", "direct", "")]);
        Assert.False(BusinessPolicyDomains.IsProtected("notuiscom.ru"));
        Assert.False(BusinessPolicyDomains.IsProtected("notadmin.corp.ninitux.com"));
    }

    [Fact]
    public void ValidateRules_RejectsDuplicatesAndConflictingAncestors_InEitherOrder()
    {
        var parent = new BusinessPolicyRule("example.com", "direct", "");
        var child = new BusinessPolicyRule("child.example.com", "proxy", "");
        Assert.Throws<InvalidDataException>(() => BusinessPolicyDomains.ValidateRules([parent, parent]));
        Assert.Throws<InvalidDataException>(() => BusinessPolicyDomains.ValidateRules([parent, child]));
        Assert.Throws<InvalidDataException>(() => BusinessPolicyDomains.ValidateRules([child, parent]));
    }

    [Fact]
    public void ValidateRules_BoundsDnsLabelsCountAndUtf8Comments()
    {
        BusinessPolicyDomains.ValidateRules([]);
        var comment = new string('é', 128);
        BusinessPolicyDomains.ValidateRules([new("example.com", "direct", comment)]);
        BusinessPolicyDomains.ValidateRules(Enumerable.Range(0, 512)
            .Select(i => new BusinessPolicyRule($"s{i}.example.com", "proxy", "")).ToArray());
        Assert.Throws<InvalidDataException>(() => BusinessPolicyDomains.ValidateRules(Enumerable.Range(0, 513)
            .Select(i => new BusinessPolicyRule($"s{i}.example.com", "proxy", "")).ToArray()));
        Assert.Throws<InvalidDataException>(() => BusinessPolicyDomains.ValidateRules([new("example.com", "direct", comment + "é")]));
        Assert.Throws<InvalidDataException>(() => BusinessPolicyDomains.ValidateRules([new("example.com", "direct", "line\nnext")]));
        Assert.Throws<InvalidDataException>(() => BusinessPolicyDomains.ValidateRules([new("example.com", "direct", "\ud800")]));
        Assert.Throws<InvalidDataException>(() => BusinessPolicyDomains.ValidateRules([new(new string('a', 64) + ".com", "direct", "")]));
        Assert.Throws<InvalidDataException>(() => BusinessPolicyDomains.ValidateRules([new("example.com", "block", "")]));
    }
}
