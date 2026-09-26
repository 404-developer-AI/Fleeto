using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Services;

namespace Fleeto.Web.Components.Shared;

/// <summary>A policy or patch policy as <see cref="ClassChoiceEditor"/> edits it: one for every endpoint, or one per class.</summary>
public sealed class ClassSelection
{
    public Guid? All { get; set; }
    public Guid? Server { get; set; }
    public Guid? Workstation { get; set; }

    /// <summary>Servers and workstations have a choice of their own.</summary>
    public bool Split { get; set; }

    public static ClassSelection From(ClassChoice choice) =>
        new() { All = choice.All, Server = choice.Server, Workstation = choice.Workstation, Split = choice.Split };

    public ClassChoice ToChoice() => Split ? new ClassChoice(null, Server, Workstation) : new ClassChoice(All, null, null);

    /// <summary>What an endpoint of <paramref name="endpointClass"/> gets from this choice, or null when it inherits.</summary>
    public Guid? For(EndpointClass endpointClass) => Split ? endpointClass == EndpointClass.Server ? Server : Workstation : All;
}

/// <summary>What <see cref="LinkEditor"/> edits: the policy, patch policy and monitoring templates chosen on one level.</summary>
public sealed class LinkSelection
{
    public ClassSelection Policy { get; init; } = new();
    public ClassSelection PatchPolicy { get; init; } = new();
    public HashSet<Guid> TemplateIds { get; } = [];

    public static LinkSelection From(LevelLinks links) => From(links.Policy.Own, links.PatchPolicy.Own, links.MonitoringTemplates.Select(t => t.Id));

    public static LinkSelection From(ClassChoice policy, ClassChoice patchPolicy, IEnumerable<Guid> templateIds)
    {
        var selection = new LinkSelection { Policy = ClassSelection.From(policy), PatchPolicy = ClassSelection.From(patchPolicy) };
        selection.TemplateIds.UnionWith(templateIds);
        return selection;
    }

    public LinksInput ToInput() => new(Policy.ToChoice(), PatchPolicy.ToChoice(), TemplateIds.ToList());

    /// <summary>True when the selection differs from what <paramref name="links"/> holds.</summary>
    public bool DiffersFrom(LevelLinks links) =>
        Policy.ToChoice() != links.Policy.Own || PatchPolicy.ToChoice() != links.PatchPolicy.Own ||
        !TemplateIds.SetEquals(links.MonitoringTemplates.Select(t => t.Id));
}

/// <summary>The texts of the empty choices: what applies without a choice, for every endpoint and per class.</summary>
public sealed record InheritTexts(string All, string Server, string Workstation)
{
    /// <summary>From what a level inherits: "Inherit: Servers weekly (from site)", or <paramref name="none"/> without anything.</summary>
    public static InheritTexts From(SlotLinks slots, string none)
    {
        var server = Text(slots.InheritedServer, none);
        var workstation = Text(slots.InheritedWorkstation, none);
        var all = slots.InheritedServer?.Id == slots.InheritedWorkstation?.Id
            ? server
            : $"Inherit: {slots.InheritedServer?.Name ?? "nothing"} for servers, {slots.InheritedWorkstation?.Name ?? "nothing"} for workstations";
        return new InheritTexts(all, server, workstation);
    }

    public static InheritTexts Same(string text) => new(text, text, text);

    private static string Text(InheritedLink? link, string none) => link is null ? none : $"Inherit: {link.Name} ({LinkEditor.From(link.Level)})";
}
