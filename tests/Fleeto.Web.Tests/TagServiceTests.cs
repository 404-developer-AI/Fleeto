using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Tags on clients (0.6.0): typed on a client by admins and technicians, one tag per name whatever the case, filtered on in the
/// clients panel, and managed in Settings only by admins who see every client. A caller limited to clients never learns the tags
/// of other clients.
/// </summary>
[Collection(WebCollection.Name)]
public class TagServiceTests
{
    private readonly WebFixture _fixture;

    public TagServiceTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private TagService Tags => _fixture.Services.GetRequiredService<TagService>();
    private ClientService Clients => _fixture.Services.GetRequiredService<ClientService>();

    private static string NewTag(string prefix = "tag") => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task A_typed_tag_is_created_once_with_the_color_of_its_name_and_reused_whatever_the_case()
    {
        var clientA = await _fixture.Database.CreateClientAsync();
        var clientB = await _fixture.Database.CreateClientAsync();
        var name = NewTag("Gold");

        Assert.True((await Tags.SetClientTagsAsync(WebFixtureBase.Technician(), clientA.Id, [$" {name} ", name.ToLowerInvariant()])).Success);
        Assert.True((await Tags.SetClientTagsAsync(WebFixtureBase.Technician(), clientB.Id, [name.ToUpperInvariant()])).Success);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var tag = await db.Tags.SingleAsync(t => t.NormalizedName == name.ToUpperInvariant());
        Assert.Equal(name, tag.Name);
        Assert.Equal(TagRules.DefaultColor(name), tag.Color);
        Assert.Equal(2, await db.ClientTags.CountAsync(l => l.TagId == tag.Id));
        Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.ClientTagsChanged && a.ClientId == clientA.Id));

        var detail = await Clients.GetAsync(WebFixtureBase.Technician(), clientB.Id);
        Assert.Equal([name], detail!.Tags.Select(t => t.Name).ToList());
    }

    [Fact]
    public async Task Invalid_names_too_many_tags_and_read_only_callers_are_refused()
    {
        var client = await _fixture.Database.CreateClientAsync();
        var technician = WebFixtureBase.Technician();

        Assert.False((await Tags.SetClientTagsAsync(technician, client.Id, ["two words"])).Success);
        Assert.False((await Tags.SetClientTagsAsync(technician, client.Id, [new string('a', TagRules.MaxLength + 1)])).Success);
        Assert.False((await Tags.SetClientTagsAsync(technician, client.Id, ["a\nb"])).Success);
        var eleven = Enumerable.Range(0, TagRules.MaxPerClient + 1).Select(i => NewTag("many")).ToList();
        var tooMany = await Tags.SetClientTagsAsync(technician, client.Id, eleven);
        Assert.False(tooMany.Success);
        Assert.Contains("at most", tooMany.Problem);

        var readOnly = WebFixtureBase.CallerWith(SystemClientScope.Instance, FleetoRoles.ReadOnly);
        Assert.Equal(ServiceResult.ForbiddenProblem, (await Tags.SetClientTagsAsync(readOnly, client.Id, [NewTag()])).Problem);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.False(await db.ClientTags.AnyAsync(l => l.ClientId == client.Id));
    }

    [Fact]
    public async Task Removing_a_tag_from_a_client_keeps_the_tag_and_an_unchanged_list_writes_no_audit_entry()
    {
        var client = await _fixture.Database.CreateClientAsync();
        var keep = NewTag("keep");
        var drop = NewTag("drop");
        var technician = WebFixtureBase.Technician();
        Assert.True((await Tags.SetClientTagsAsync(technician, client.Id, [keep, drop])).Success);
        Assert.True((await Tags.SetClientTagsAsync(technician, client.Id, [keep])).Success);
        Assert.True((await Tags.SetClientTagsAsync(technician, client.Id, [keep.ToUpperInvariant()])).Success);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Equal([keep], await db.ClientTags.Where(l => l.ClientId == client.Id).Select(l => l.Tag!.Name).ToListAsync());
        Assert.True(await db.Tags.AnyAsync(t => t.Name == drop));
        Assert.Equal(2, await db.AuditEntries.CountAsync(a => a.Action == AuditActions.ClientTagsChanged && a.ClientId == client.Id));
    }

    [Fact]
    public async Task The_clients_panel_filters_on_every_chosen_tag_and_searches_tag_names()
    {
        var both = await _fixture.Database.CreateClientAsync();
        var one = await _fixture.Database.CreateClientAsync();
        var red = NewTag("red");
        var blue = NewTag("blue");
        var technician = WebFixtureBase.Technician();
        Assert.True((await Tags.SetClientTagsAsync(technician, both.Id, [red, blue])).Success);
        Assert.True((await Tags.SetClientTagsAsync(technician, one.Id, [red])).Success);

        var visible = await Tags.ListVisibleAsync(technician);
        var redId = visible.Single(t => t.Name == red).Id;
        var blueId = visible.Single(t => t.Name == blue).Id;

        var onlyRed = await Clients.ListTreeAsync(technician, null, [redId]);
        Assert.Equal(new[] { both.Id, one.Id }.Order(), onlyRed.Select(c => c.Id).Order());
        var redAndBlue = await Clients.ListTreeAsync(technician, null, [redId, blueId]);
        Assert.Equal([both.Id], redAndBlue.Select(c => c.Id));
        Assert.Equal([blue, red], Assert.Single(redAndBlue).Tags.Select(t => t.Name));

        var search = await Clients.ListTreeAsync(technician, blue);
        Assert.Equal([both.Id], search.Select(c => c.Id));
    }

    [Fact]
    public async Task A_caller_limited_to_clients_sees_and_changes_only_the_tags_of_its_clients()
    {
        var own = await _fixture.Database.CreateClientAsync();
        var other = await _fixture.Database.CreateClientAsync();
        var secret = NewTag("secret");
        var shared = NewTag("shared");
        Assert.True((await Tags.SetClientTagsAsync(WebFixtureBase.Technician(), other.Id, [secret, shared])).Success);

        var limited = WebFixtureBase.CallerWith(new RestrictedClientScope([own.Id]), FleetoRoles.Technician);
        Assert.DoesNotContain(await Tags.ListVisibleAsync(limited), t => t.Name == secret);

        // Typing a name that exists elsewhere links that tag, and reveals nothing of the other client.
        Assert.True((await Tags.SetClientTagsAsync(limited, own.Id, [shared])).Success);
        var visible = await Tags.ListVisibleAsync(limited);
        Assert.Contains(visible, t => t.Name == shared);
        Assert.DoesNotContain(visible, t => t.Name == secret);

        // Another client is not found, as if it does not exist.
        Assert.False((await Tags.SetClientTagsAsync(limited, other.Id, [NewTag()])).Success);
        var otherTree = await Clients.ListTreeAsync(limited, null, [(await Tags.ListVisibleAsync(WebFixtureBase.Technician())).Single(t => t.Name == secret).Id]);
        Assert.Empty(otherTree);

        // Settings: only admins who see every client.
        var limitedAdmin = WebFixtureBase.CallerWith(new RestrictedClientScope([own.Id]), FleetoRoles.Admin);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Tags.ListAsync(limitedAdmin));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Tags.ListAsync(WebFixtureBase.Technician()));
        var sharedId = visible.Single(t => t.Name == shared).Id;
        Assert.Equal(ServiceResult.ForbiddenProblem, (await Tags.DeleteAsync(limitedAdmin, sharedId)).Problem);
        Assert.Equal(ServiceResult.ForbiddenProblem, (await Tags.UpdateAsync(WebFixtureBase.Technician(), sharedId, "renamed", TagColor.Red)).Problem);
    }

    [Fact]
    public async Task An_admin_renames_recolors_and_deletes_a_tag_for_every_client()
    {
        var client = await _fixture.Database.CreateClientAsync();
        var name = NewTag("old");
        var taken = NewTag("taken");
        var admin = WebFixtureBase.Admin();
        Assert.True((await Tags.SetClientTagsAsync(admin, client.Id, [name, taken])).Success);
        var tag = (await Tags.ListAsync(admin)).Single(t => t.Name == name);
        Assert.Equal(1, tag.ClientCount);

        Assert.False((await Tags.UpdateAsync(admin, tag.Id, taken.ToUpperInvariant(), TagColor.Red)).Success);
        Assert.False((await Tags.UpdateAsync(admin, tag.Id, name, (TagColor)99)).Success);

        var renamed = NewTag("new");
        Assert.True((await Tags.UpdateAsync(admin, tag.Id, renamed, TagColor.Brown)).Success);
        var shown = (await Clients.GetAsync(admin, client.Id))!.Tags.Single(t => t.Id == tag.Id);
        Assert.Equal((renamed, TagColor.Brown), (shown.Name, shown.Color));

        Assert.True((await Tags.DeleteAsync(admin, tag.Id)).Success);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.False(await db.ClientTags.AnyAsync(l => l.TagId == tag.Id));
        Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.TagUpdated && a.TargetId == tag.Id.ToString()));
        Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.TagDeleted && a.TargetId == tag.Id.ToString()));
    }

    [Fact]
    public async Task Deleting_a_client_removes_its_tag_links()
    {
        var client = await _fixture.Database.CreateClientAsync();
        Assert.True((await Tags.SetClientTagsAsync(WebFixtureBase.Technician(), client.Id, [NewTag()])).Success);
        Assert.True((await Clients.DeleteAsync(WebFixtureBase.Technician(), client.Id, client.Code)).Success);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.False(await db.ClientTags.AnyAsync(l => l.ClientId == client.Id));
    }
}

/// <summary>The pure rules of tag names and colors.</summary>
public class TagRulesTests
{
    [Theory]
    [InlineData("gold")]
    [InlineData("Contract-Gold_2026.v1+")]
    [InlineData("Brüssel")]
    public void Valid_names(string name) => Assert.Null(TagRules.Validate(name));

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("semi;colon")]
    [InlineData("line\nbreak")]
    [InlineData("<b>")]
    public void Invalid_names(string name) => Assert.NotNull(TagRules.Validate(name));

    [Fact]
    public void The_default_color_is_stable_ignores_case_and_is_never_gray()
    {
        Assert.Equal(TagRules.DefaultColor("Gold"), TagRules.DefaultColor(" GOLD "));
        var colors = Enumerable.Range(0, 500).Select(i => TagRules.DefaultColor("tag" + i)).ToHashSet();
        Assert.DoesNotContain(TagColor.Gray, colors);
        Assert.Equal(TagRules.Palette.Count - 1, colors.Count);
    }
}
