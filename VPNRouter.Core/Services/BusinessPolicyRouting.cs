using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

internal static class BusinessPolicyRouting
{
    internal static List<CustomRule> Compose(IEnumerable<CustomRule> embedded, BusinessPolicySnapshot? policy)
    {
        var seed = embedded.ToArray();
        if (policy != null) BusinessPolicyDomains.ValidateRules(policy.Rules);

        if (policy?.Phone != null) BusinessPhonePolicy.ValidateRules(policy.Phone.Rules);

        var result = new List<CustomRule>
        {
            Site(BusinessPolicyDomains.PortalDomain, "proxy", "Corporate administration")
        };
        if (policy?.Phone == null)
        {
            result.AddRange(BusinessPolicyDomains.ProtectedDirectDomains
                .Select(domain => Site(domain, "direct", "Protected telephony service")));
            result.AddRange(seed.Where(r => !IsSite(r) && r.Type != "process_name").Select(Copy));
        }
        else
        {
            result.AddRange(policy.Phone.Rules.Select(r => new CustomRule
            {
                Type = r.Type, Value = r.Value, Action = r.Action, Comment = r.Comment, Enabled = true
            }));
        }

        if (policy?.Phone != null)
            result.AddRange(seed.Where(r => !IsSite(r) && r.Type != "process_name" &&
                !(r.Type == "ip_cidr" && r.Value == BusinessPhonePolicy.LegacySipCidr)).Select(Copy));

        if (policy == null)
            result.AddRange(seed.Where(r => IsSite(r) && !BusinessPolicyDomains.IsProtected(r.Value)).Select(Copy));
        else
            result.AddRange(policy.Rules.Select(r => Site(r.Domain, r.Action, r.Comment)));

        result.AddRange(seed.Where(r => r.Type == "process_name").Select(Copy));
        return result;
    }

    private static bool IsSite(CustomRule rule) => rule.Type is "domain" or "domain_suffix";

    private static CustomRule Site(string domain, string action, string comment) => new()
    {
        Type = "domain_suffix", Value = domain, Action = action, Comment = comment, Enabled = true
    };

    private static CustomRule Copy(CustomRule rule) => new()
    {
        Type = rule.Type, Value = rule.Value, Action = rule.Action,
        Comment = rule.Comment, Enabled = rule.Enabled
    };
}
