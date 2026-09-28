using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>Where a monitoring template can be linked (0.6.0): to clients, sites and endpoints, as the template says.</summary>
public static class TemplateLinkRules
{
    public static bool Allows(bool forClients, bool forSites, bool forEndpoints, LinkLevel level) => level switch
    {
        LinkLevel.Client => forClients,
        LinkLevel.Site => forSites,
        LinkLevel.Endpoint => forEndpoints,
        _ => false
    };

    public static bool Allows(MonitoringTemplate template, LinkLevel level) =>
        Allows(template.ForClients, template.ForSites, template.ForEndpoints, level);

    /// <summary>"clients, sites and endpoints", "sites", "clients and endpoints".</summary>
    public static string Describe(bool forClients, bool forSites, bool forEndpoints)
    {
        var levels = new List<string>();
        if (forClients)
        {
            levels.Add("clients");
        }

        if (forSites)
        {
            levels.Add("sites");
        }

        if (forEndpoints)
        {
            levels.Add("endpoints");
        }

        return levels.Count switch
        {
            0 => "nothing",
            1 => levels[0],
            _ => string.Join(", ", levels[..^1]) + " and " + levels[^1]
        };
    }

    /// <summary>"a client", "a site", "an endpoint".</summary>
    public static string WithArticle(LinkLevel level) => level switch
    {
        LinkLevel.Client => "a client",
        LinkLevel.Site => "a site",
        _ => "an endpoint"
    };
}
