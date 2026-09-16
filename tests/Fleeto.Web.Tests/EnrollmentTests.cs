using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Security;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// The install command carries the gateway address and the CA fingerprint; the token is stored only as a hash and never
/// reaches a log or the audit trail.
/// </summary>
[Collection(WebCollection.Name)]
public class EnrollmentTests
{
    private const string Fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly WebFixture _fixture;

    public EnrollmentTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Windows_install_command_contains_the_fingerprint_agent_address_and_token_in_single_quotes()
    {
        var (token, _, _) = OpaqueTokens.Create(OpaqueTokens.EnrollmentPrefix);
        var command = InstallCommand.Build("https://rmm.example.com/", "agents.rmm.example.com", 443, token, Fingerprint.ToUpperInvariant()).Windows;

        Assert.StartsWith("powershell -NoProfile -ExecutionPolicy Bypass -Command '", command);
        Assert.Contains("-Uri (''https://rmm.example.com/agent/download/windows-'' + $a) -OutFile", command);
        Assert.Contains("''ARM64''", command);
        Assert.Contains("--server ''agents.rmm.example.com:443''", command);
        Assert.Contains($"--token ''{token}''", command);
        Assert.Contains($"--ca-fingerprint ''{Fingerprint}''", command);
        Assert.EndsWith("'", command);

        Assert.Throws<ArgumentException>(() => InstallCommand.Build("https://rmm.example.com", "agents'; calc", 443, token, Fingerprint));
        Assert.Throws<ArgumentException>(() => InstallCommand.Build("https://rmm.example.com", "agents.example.com", 443, token, "not-hex"));
        Assert.Throws<ArgumentException>(() => InstallCommand.Build("https://rmm.example.com/'; rm -rf /", "agents.example.com", 443, token, Fingerprint));
    }

    [Fact]
    public void Linux_install_command_picks_the_architecture_and_runs_the_installer_outside_tmp()
    {
        var (token, _, _) = OpaqueTokens.Create(OpaqueTokens.EnrollmentPrefix);
        var command = InstallCommand.Build("https://rmm.example.com", "agents.rmm.example.com", 443, token, Fingerprint).Linux;

        Assert.StartsWith("sudo sh -c '", command);
        Assert.EndsWith("'", command);
        // The command is one single-quoted shell argument, so nothing in it may be quoted with a single quote.
        Assert.Equal(2, command.Count(c => c == '\''));
        Assert.Contains("x86_64) a=amd64;; aarch64|arm64) a=arm64;;", command);
        Assert.Contains("https://rmm.example.com/agent/download/linux-$a", command);
        // /tmp is mounted without exec permission on hardened endpoints, so the installer runs from /opt.
        Assert.Contains("mktemp -d /opt/.fleeto-install.", command);
        Assert.Contains("curl -fsS", command);
        Assert.Contains("wget -q", command);
        Assert.Contains($"install --server agents.rmm.example.com:443 --token {token} --ca-fingerprint {Fingerprint}", command);
    }

    [Fact]
    public void Every_platform_of_the_install_command_is_served_by_this_instance()
    {
        Assert.Equal(["windows-amd64", "windows-arm64", "linux-amd64", "linux-arm64"], InstallCommand.Platforms);
    }

    [Fact]
    public async Task Created_token_is_hashed_audited_without_the_secret_and_never_logged()
    {
        var db = _fixture.Database;
        await using (var setup = db.DbFactory.CreateSystem())
        {
            if (!await setup.CertificateAuthorities.AnyAsync(c => c.RetiredAt == null))
            {
                setup.CertificateAuthorities.Add(new CertificateAuthority
                {
                    Id = Guid.NewGuid(),
                    CertificateDer = [1, 2, 3],
                    Fingerprint = Fingerprint,
                    EncryptedPrivateKey = [4, 5, 6],
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddYears(10)
                });
                await setup.SaveChangesAsync();
            }
        }

        string activeFingerprint;
        await using (var read = db.DbFactory.CreateSystem())
        {
            activeFingerprint = await read.CertificateAuthorities.Where(c => c.RetiredAt == null).OrderByDescending(c => c.CreatedAt)
                .Select(c => c.Fingerprint).FirstAsync();
        }

        var client = await db.CreateClientAsync();
        var site = await db.CreateSiteAsync(client.Id);
        var service = _fixture.Services.GetRequiredService<EnrollmentService>();

        var result = await service.CreateAsync(WebFixtureBase.Technician(), site.Id, "Rollout", TimeSpan.FromDays(7), null);
        Assert.True(result.Success, result.Problem);
        var created = result.Value!;
        Assert.NotNull(created.InstallCommands);
        foreach (var command in new[] { created.InstallCommands.Windows, created.InstallCommands.Linux })
        {
            Assert.Contains(activeFingerprint, command);
            Assert.Contains("agents." + Fleeto.Testing.TestDatabase.Fqdn + ":443", command);
            Assert.Contains(created.Token, command);
        }

        Assert.True(OpaqueTokens.TryParse(created.Token, OpaqueTokens.EnrollmentPrefix, out var id, out var hash));
        await using var check = db.DbFactory.CreateSystem();
        var row = await check.EnrollmentTokens.SingleAsync(t => t.Id == id);
        Assert.Equal(hash, row.TokenHash);
        Assert.Null(row.MaxUses);

        var secret = created.Token.Split('_', 3)[2];
        Assert.DoesNotContain(_fixture.Logs.Messages, m => m.Contains(secret, StringComparison.Ordinal));
        var audit = await check.AuditEntries.Where(a => a.TargetId == id.ToString()).Select(a => a.DetailsJson).ToListAsync();
        Assert.NotEmpty(audit);
        Assert.DoesNotContain(audit, details => details.Contains(secret, StringComparison.Ordinal));

        // Lifetimes outside the offered choices are refused.
        var odd = await service.CreateAsync(WebFixtureBase.Technician(), site.Id, "Odd", TimeSpan.FromDays(365), 1);
        Assert.False(odd.Success);
    }
    [Fact]
    public async Task An_enroll_again_token_is_single_use_for_one_day_and_bound_to_its_endpoint()
    {
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-AGAIN");
        var enrollment = _fixture.Services.GetRequiredService<EnrollmentService>();

        Assert.False((await enrollment.CreateForEndpointAsync(WebFixture.CallerWith(Infrastructure.Data.SystemClientScope.Instance, FleetoRoles.ReadOnly),
            endpoint.Id)).Success);
        Assert.False((await enrollment.CreateForEndpointAsync(WebFixture.CallerWith(new RestrictedClientScope([Guid.NewGuid()]), FleetoRoles.Technician),
            endpoint.Id)).Success);

        var created = await enrollment.CreateForEndpointAsync(WebFixture.Technician(), endpoint.Id);
        Assert.True(created.Success, created.Problem);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var row = await db.EnrollmentTokens.AsNoTracking().SingleAsync(t => t.Id == created.Value!.Id);
        Assert.Equal(endpoint.Id, row.EndpointId);
        Assert.Equal(site.Id, row.SiteId);
        Assert.Equal(1, row.MaxUses);
        Assert.Equal(TimeSpan.FromDays(1), row.ExpiresAt - row.CreatedAt);
        var listed = Assert.Single(await enrollment.ListAsync(WebFixture.Technician(), site.Id), t => t.Id == row.Id);
        Assert.Equal("SRV-AGAIN", listed.EndpointHostname);
        var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.TargetId == row.Id.ToString());
        Assert.DoesNotContain(created.Value!.Token, audit.DetailsJson);
        Assert.Contains(endpoint.Id.ToString(), audit.DetailsJson);

        // Deleting the endpoint removes the token with it.
        await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteDeleteAsync();
        Assert.False(await db.EnrollmentTokens.AnyAsync(t => t.Id == row.Id));
    }
}
