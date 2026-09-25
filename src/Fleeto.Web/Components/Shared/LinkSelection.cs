using Fleeto.Web.Services;

namespace Fleeto.Web.Components.Shared;

/// <summary>What <see cref="LinkEditor"/> edits: the policy, patch policy and monitoring templates chosen on one level.</summary>
public sealed class LinkSelection
{
    public Guid? PolicyId { get; set; }
    public Guid? PatchPolicyId { get; set; }
    public HashSet<Guid> TemplateIds { get; } = [];

    public static LinkSelection From(LevelLinks links)
    {
        var selection = new LinkSelection { PolicyId = links.PolicyId, PatchPolicyId = links.PatchPolicyId };
        selection.TemplateIds.UnionWith(links.MonitoringTemplates.Select(t => t.Id));
        return selection;
    }

    public static LinkSelection From(Guid? policyId, Guid? patchPolicyId, IEnumerable<Guid> templateIds)
    {
        var selection = new LinkSelection { PolicyId = policyId, PatchPolicyId = patchPolicyId };
        selection.TemplateIds.UnionWith(templateIds);
        return selection;
    }

    public LinksInput ToInput() => new(PolicyId, PatchPolicyId, TemplateIds.ToList());

    /// <summary>True when the selection differs from what <paramref name="links"/> holds.</summary>
    public bool DiffersFrom(LevelLinks links) =>
        PolicyId != links.PolicyId || PatchPolicyId != links.PatchPolicyId || !TemplateIds.SetEquals(links.MonitoringTemplates.Select(t => t.Id));
}
