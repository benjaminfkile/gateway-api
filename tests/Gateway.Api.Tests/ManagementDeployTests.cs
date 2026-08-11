using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gateway.Api.Data;
using Gateway.Api.Management;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Tests;

/// <summary>
/// Integration coverage for deploy / rollback / manifest-edit / logs / history
/// (tech-spec §4.5). Asserts the deploy resolves the digest once and records
/// <c>deploy_history</c>, the PUT validation guardrails, rollback to a previous
/// successful digest, and the authz matrix (OpsDeploy for deploy, OpsAdmin for PUT).
/// </summary>
public class ManagementDeployTests
{
    private static async Task SeedAsync(ManagementApiFactory factory, params ServiceManifest[] manifests)
    {
        await factory.WithDbAsync(async db =>
        {
            db.ServiceManifests.AddRange(manifests);
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Deploy_ResolvesDigestOnce_UpdatesManifest_RecordsHistory()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a", digest: "sha256:old", tag: "v1"));
        factory.ImageRegistry.DigestsByTag["v2"] = "sha256:new";

        var client = factory.CreateClient(ManagementApiFactory.DeployToken("bob"));
        var response = await client.PostAsJsonAsync("/mgmt/services/svc-a/deploy", new { tag = "v2" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var deployId = body.GetProperty("deployId").GetInt32();
        Assert.Equal("sha256:new", body.GetProperty("digest").GetString());

        // Digest resolved exactly once (fleet converges to one image).
        Assert.Single(factory.ImageRegistry.Resolved);
        Assert.Equal(("registry/svc-a", "v2"), factory.ImageRegistry.Resolved[0]);

        await factory.WithDbAsync(async db =>
        {
            var m = await db.ServiceManifests.SingleAsync(x => x.Name == "svc-a");
            Assert.Equal("sha256:new", m.Digest);
            Assert.Equal("v2", m.Tag);
            Assert.Equal("bob", m.UpdatedBy);

            var history = await db.DeployHistory.SingleAsync(h => h.Id == deployId);
            Assert.Equal(DeployAction.Deploy, history.Action);
            Assert.Equal("sha256:old", history.FromDigest);
            Assert.Equal("sha256:new", history.ToDigest);
            Assert.Equal(DeployStatus.InProgress, history.Status);
            Assert.Equal("bob", history.Actor);
            Assert.Null(history.FinishedAt);
        });
    }

    [Fact]
    public async Task Deploy_UnknownTag_Returns404()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a"));
        factory.ImageRegistry.MissingTags.Add("nope");

        var client = factory.CreateClient(ManagementApiFactory.DeployToken());
        var response = await client.PostAsJsonAsync("/mgmt/services/svc-a/deploy", new { tag = "nope" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deploy_MissingTag_Returns400()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a"));

        var client = factory.CreateClient(ManagementApiFactory.DeployToken());
        var response = await client.PostAsJsonAsync("/mgmt/services/svc-a/deploy", new { tag = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Deploy_CiToken_Allowed()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a"));

        var client = factory.CreateClient(ManagementApiFactory.CiToken());
        var response = await client.PostAsJsonAsync("/mgmt/services/svc-a/deploy", new { tag = "v9" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Deploy_NoRoleToken_Forbidden()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a"));

        var client = factory.CreateClient(ManagementApiFactory.NoRoleToken());
        var response = await client.PostAsJsonAsync("/mgmt/services/svc-a/deploy", new { tag = "v2" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Deploy_GatewayName_Rejected400()
    {
        await using var factory = new ManagementApiFactory();

        var client = factory.CreateClient(ManagementApiFactory.DeployToken());
        var response = await client.PostAsJsonAsync("/mgmt/services/gateway/deploy", new { tag = "v2" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rollback_UsesPreviousSuccessfulDigest()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a", digest: "sha256:v2"));
        await factory.WithDbAsync(async db =>
        {
            // A completed deploy that put v1 live, then v2 (current) in progress.
            db.DeployHistory.Add(new DeployHistory
            {
                Service = "svc-a", FromDigest = null, ToDigest = "sha256:v1",
                Actor = "bob", Action = DeployAction.Deploy, Status = DeployStatus.Done,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10), FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-9),
            });
            await db.SaveChangesAsync();
        });

        var client = factory.CreateClient(ManagementApiFactory.DeployToken());
        var response = await client.PostAsync("/mgmt/services/svc-a/rollback", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sha256:v1", body.GetProperty("digest").GetString());

        await factory.WithDbAsync(async db =>
        {
            var m = await db.ServiceManifests.SingleAsync(x => x.Name == "svc-a");
            Assert.Equal("sha256:v1", m.Digest);
            var rollback = await db.DeployHistory.SingleAsync(h => h.Action == DeployAction.Rollback);
            Assert.Equal("sha256:v2", rollback.FromDigest);
            Assert.Equal("sha256:v1", rollback.ToDigest);
            Assert.Equal(DeployStatus.InProgress, rollback.Status);
        });
    }

    [Fact]
    public async Task Rollback_NoPreviousDigest_Returns409()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a", digest: "sha256:v1"));

        var client = factory.CreateClient(ManagementApiFactory.DeployToken());
        var response = await client.PostAsync("/mgmt/services/svc-a/rollback", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Put_CreatesNewManifest_201_AndAudits()
    {
        await using var factory = new ManagementApiFactory();

        var client = factory.CreateClient(ManagementApiFactory.AdminToken("alice"));
        var response = await client.PutAsJsonAsync("/mgmt/services/svc-new", new
        {
            image = "registry/svc-new",
            tag = "latest",
            port = 8090,
            desiredStatus = "running",
            includeInHealth = true,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await factory.WithDbAsync(async db =>
        {
            var m = await db.ServiceManifests.SingleAsync(x => x.Name == "svc-new");
            Assert.Equal(8090, m.Port);
            Assert.Equal("alice", m.UpdatedBy);
            var audit = await db.DeployHistory.SingleAsync();
            Assert.Equal(DeployAction.Upsert, audit.Action);
        });
    }

    [Fact]
    public async Task Put_UpdatesExisting_200_PreservesDigest()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a", port: 8080, digest: "sha256:keep"));

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PutAsJsonAsync("/mgmt/services/svc-a", new
        {
            image = "registry/svc-a",
            tag = "latest",
            port = 9090,
            includeInHealth = false,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.WithDbAsync(async db =>
        {
            var m = await db.ServiceManifests.SingleAsync(x => x.Name == "svc-a");
            Assert.Equal(9090, m.Port);
            Assert.False(m.IncludeInHealth);
            Assert.Equal("sha256:keep", m.Digest); // preserved across the edit
        });
    }

    [Fact]
    public async Task Put_CreatesNewManifest_GeneratesPublishToken_NotInResponse()
    {
        // task #593: a create mints a realtime publish token on the row, but the token
        // is write-only — never echoed in the API response (exposed only via container env).
        await using var factory = new ManagementApiFactory();

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PutAsJsonAsync("/mgmt/services/svc-new", new
        {
            image = "registry/svc-new",
            tag = "latest",
            port = 8090,
            includeInHealth = false,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        string token = null!;
        await factory.WithDbAsync(async db =>
        {
            var m = await db.ServiceManifests.SingleAsync(x => x.Name == "svc-new");
            Assert.False(string.IsNullOrEmpty(m.RealtimePublishToken));
            token = m.RealtimePublishToken!;
        });

        // The token must not leak in the create response nor in GET /mgmt/services.
        var created = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(token, created);
        Assert.DoesNotContain("realtimePublishToken", created, StringComparison.OrdinalIgnoreCase);

        var list = await client.GetStringAsync("/mgmt/services");
        Assert.DoesNotContain(token, list);
        Assert.DoesNotContain("realtimePublishToken", list, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Put_UpdatesExisting_PreservesPublishToken()
    {
        // task #593: an edit that does not change the token must preserve it — a
        // token generated once never rotates on an unrelated manifest edit.
        await using var factory = new ManagementApiFactory();

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        await client.PutAsJsonAsync("/mgmt/services/svc-a", new
        {
            image = "registry/svc-a",
            tag = "latest",
            port = 8080,
            includeInHealth = false,
        });

        string original = null!;
        await factory.WithDbAsync(async db =>
            original = (await db.ServiceManifests.SingleAsync(x => x.Name == "svc-a")).RealtimePublishToken!);
        Assert.False(string.IsNullOrEmpty(original));

        // Edit the port; the token must survive unchanged.
        await client.PutAsJsonAsync("/mgmt/services/svc-a", new
        {
            image = "registry/svc-a",
            tag = "latest",
            port = 9090,
            includeInHealth = false,
        });

        await factory.WithDbAsync(async db =>
        {
            var m = await db.ServiceManifests.SingleAsync(x => x.Name == "svc-a");
            Assert.Equal(9090, m.Port);
            Assert.Equal(original, m.RealtimePublishToken);
        });
    }

    [Fact]
    public async Task Put_SetsRealtimeAuthPath_PersistsAndReturned()
    {
        // task #594: realtime_auth_path is a first-class manifest field — settable on
        // upsert and, unlike the publish token, returned by GET /mgmt/services (it is
        // not a secret; null = public channels, non-null = delegated auth).
        await using var factory = new ManagementApiFactory();

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PutAsJsonAsync("/mgmt/services/svc-new", new
        {
            image = "registry/svc-new",
            tag = "latest",
            port = 8090,
            includeInHealth = false,
            realtimeAuthPath = "/realtime/auth",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("/realtime/auth", created.GetProperty("realtimeAuthPath").GetString());

        await factory.WithDbAsync(async db =>
        {
            var m = await db.ServiceManifests.SingleAsync(x => x.Name == "svc-new");
            Assert.Equal("/realtime/auth", m.RealtimeAuthPath);
        });

        var list = await client.GetFromJsonAsync<JsonElement>("/mgmt/services");
        var svc = list.EnumerateArray().Single(s => s.GetProperty("name").GetString() == "svc-new");
        Assert.Equal("/realtime/auth", svc.GetProperty("realtimeAuthPath").GetString());
    }

    [Fact]
    public async Task Put_NoRealtimeAuthPath_DefaultsNull_PublicChannels()
    {
        await using var factory = new ManagementApiFactory();

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PutAsJsonAsync("/mgmt/services/svc-new", new
        {
            image = "registry/svc-new",
            tag = "latest",
            port = 8090,
            includeInHealth = false,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, created.GetProperty("realtimeAuthPath").ValueKind);

        await factory.WithDbAsync(async db =>
        {
            var m = await db.ServiceManifests.SingleAsync(x => x.Name == "svc-new");
            Assert.Null(m.RealtimeAuthPath);
        });
    }

    [Fact]
    public async Task Put_RealtimeAuthPath_NotRooted_Returns400()
    {
        await using var factory = new ManagementApiFactory();

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PutAsJsonAsync("/mgmt/services/svc-new", new
        {
            image = "registry/svc-new",
            tag = "latest",
            port = 8090,
            includeInHealth = false,
            realtimeAuthPath = "https://evil.example/auth",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_SetsRealtimeAllowedOrigins_NormalizesPersistsAndReturned()
    {
        // task #595: realtime_allowed_origins is settable on upsert and, like the auth
        // path, returned by GET /mgmt/services (not a secret). Entries are canonicalized
        // to their exact origin form so the dynamic /hub CORS policy can exact-match them.
        await using var factory = new ManagementApiFactory();

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PutAsJsonAsync("/mgmt/services/svc-new", new
        {
            image = "registry/svc-new",
            tag = "latest",
            port = 8090,
            includeInHealth = false,
            // Trailing slash + default port are stripped on normalization.
            realtimeAllowedOrigins = "https://chat.example.com/, https://app.example.com:443",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "https://chat.example.com,https://app.example.com",
            created.GetProperty("realtimeAllowedOrigins").GetString());

        await factory.WithDbAsync(async db =>
        {
            var m = await db.ServiceManifests.SingleAsync(x => x.Name == "svc-new");
            Assert.Equal("https://chat.example.com,https://app.example.com", m.RealtimeAllowedOrigins);
        });

        var list = await client.GetFromJsonAsync<JsonElement>("/mgmt/services");
        var svc = list.EnumerateArray().Single(s => s.GetProperty("name").GetString() == "svc-new");
        Assert.Equal(
            "https://chat.example.com,https://app.example.com",
            svc.GetProperty("realtimeAllowedOrigins").GetString());
    }

    [Fact]
    public async Task Put_NoRealtimeAllowedOrigins_DefaultsNull()
    {
        await using var factory = new ManagementApiFactory();

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PutAsJsonAsync("/mgmt/services/svc-new", new
        {
            image = "registry/svc-new",
            tag = "latest",
            port = 8090,
            includeInHealth = false,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, created.GetProperty("realtimeAllowedOrigins").ValueKind);
    }

    [Theory]
    [InlineData("https://chat.example.com/path")]
    [InlineData("https://*.example.com")]
    [InlineData("ftp://chat.example.com")]
    [InlineData("chat.example.com")]
    [InlineData("https://chat.example.com,not-a-url")]
    public async Task Put_MalformedRealtimeAllowedOrigins_Returns400(string origins)
    {
        await using var factory = new ManagementApiFactory();

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PutAsJsonAsync("/mgmt/services/svc-new", new
        {
            image = "registry/svc-new",
            tag = "latest",
            port = 8090,
            includeInHealth = false,
            realtimeAllowedOrigins = origins,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_ChangedTag_ClearsDigest_AndAudits()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a", digest: "sha256:stale", tag: "v1"));

        var client = factory.CreateClient(ManagementApiFactory.AdminToken("alice"));
        // Same image, different tag: the pinned digest was resolved from v1 and is now
        // stale, so the edit must clear it (production bug 2026-08-08).
        var response = await client.PutAsJsonAsync("/mgmt/services/svc-a", new
        {
            image = "registry/svc-a",
            tag = "v2",
            port = 8080,
            includeInHealth = true,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.WithDbAsync(async db =>
        {
            var m = await db.ServiceManifests.SingleAsync(x => x.Name == "svc-a");
            Assert.Equal("v2", m.Tag);
            Assert.Null(m.Digest); // cleared so the next reconcile re-resolves from the tag
            Assert.Equal("alice", m.UpdatedBy);

            var audit = await db.DeployHistory.SingleAsync();
            Assert.Equal(DeployAction.Upsert, audit.Action);
            Assert.Equal("alice", audit.Actor);
        });
    }

    [Fact]
    public async Task Put_ChangedImage_ClearsDigest()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a", digest: "sha256:stale", tag: "latest"));

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        // Different image (same tag): the pinned digest belongs to the old repo.
        var response = await client.PutAsJsonAsync("/mgmt/services/svc-a", new
        {
            image = "registry/svc-a-fork",
            tag = "latest",
            port = 8080,
            includeInHealth = true,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.WithDbAsync(async db =>
        {
            var m = await db.ServiceManifests.SingleAsync(x => x.Name == "svc-a");
            Assert.Equal("registry/svc-a-fork", m.Image);
            Assert.Null(m.Digest);
        });
    }

    [Theory]
    [InlineData("Svc-A")]      // uppercase not allowed
    [InlineData("svc_a")]      // underscore not allowed
    [InlineData("svc a")]      // space not allowed
    public async Task Put_InvalidName_Returns400(string name)
    {
        await using var factory = new ManagementApiFactory();

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PutAsJsonAsync($"/mgmt/services/{Uri.EscapeDataString(name)}", new
        {
            image = "registry/x",
            tag = "latest",
            port = 8080,
            includeInHealth = false,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_OmitsRealtimeFields_PreservesExisting_PrivateChannelStaysPrivate()
    {
        // FINDING 1: a pre-phase-2 CI workflow re-upserting only {image, tag, port} must
        // NOT silently null realtime_auth_path / realtime_allowed_origins — that would flip
        // every private channel of the service to public and wipe its hub CORS origins.
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest(
            "svc-a",
            port: 8080,
            realtimeAuthPath: "/realtime/auth",
            realtimeAllowedOrigins: "https://chat.example.com"));

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        // The CI-shaped minimal upsert: image + tag + port + health only, realtime fields absent.
        var response = await client.PutAsJsonAsync("/mgmt/services/svc-a", new
        {
            image = "registry/svc-a",
            tag = "latest",
            port = 9090,
            includeInHealth = false,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.WithDbAsync(async db =>
        {
            var m = await db.ServiceManifests.SingleAsync(x => x.Name == "svc-a");
            Assert.Equal(9090, m.Port);
            // Both security-relevant fields survive the minimal re-upsert: the channel
            // stays private and the CORS allow-list is intact.
            Assert.Equal("/realtime/auth", m.RealtimeAuthPath);
            Assert.Equal("https://chat.example.com", m.RealtimeAllowedOrigins);
        });
    }

    [Fact]
    public async Task Put_EmptyRealtimeFields_ClearsThem()
    {
        // FINDING 1: an EXPLICIT empty string is the clear signal — distinct from an
        // absent/null field, which preserves.
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest(
            "svc-a",
            realtimeAuthPath: "/realtime/auth",
            realtimeAllowedOrigins: "https://chat.example.com"));

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PutAsJsonAsync("/mgmt/services/svc-a", new
        {
            image = "registry/svc-a",
            tag = "latest",
            port = 8080,
            includeInHealth = false,
            realtimeAuthPath = "",
            realtimeAllowedOrigins = "",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.WithDbAsync(async db =>
        {
            var m = await db.ServiceManifests.SingleAsync(x => x.Name == "svc-a");
            Assert.Null(m.RealtimeAuthPath);
            Assert.Null(m.RealtimeAllowedOrigins);
        });
    }

    [Fact]
    public async Task Put_SetRealtimeFields_ReplacesExisting()
    {
        // FINDING 1: a non-empty value replaces the stored one (validated/normalized).
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest(
            "svc-a",
            realtimeAuthPath: "/old/auth",
            realtimeAllowedOrigins: "https://old.example.com"));

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PutAsJsonAsync("/mgmt/services/svc-a", new
        {
            image = "registry/svc-a",
            tag = "latest",
            port = 8080,
            includeInHealth = false,
            realtimeAuthPath = "/new/auth",
            realtimeAllowedOrigins = "https://new.example.com:443/",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.WithDbAsync(async db =>
        {
            var m = await db.ServiceManifests.SingleAsync(x => x.Name == "svc-a");
            Assert.Equal("/new/auth", m.RealtimeAuthPath);
            // Normalized: default port + trailing slash stripped.
            Assert.Equal("https://new.example.com", m.RealtimeAllowedOrigins);
        });
    }

    [Theory]
    [InlineData("gateway")]   // the gateway is never a managed service
    [InlineData("hub")]        // would shadow the SignalR /hub endpoint
    [InlineData("internal")]   // would shadow the internal publish listener
    [InlineData("ops")]        // ops:* realtime namespace is gateway-owned
    public async Task Put_ReservedName_Returns400(string name)
    {
        await using var factory = new ManagementApiFactory();

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PutAsJsonAsync($"/mgmt/services/{name}", new
        {
            image = "registry/x",
            tag = "latest",
            port = 8080,
            includeInHealth = false,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("reserved", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public async Task Put_InvalidPort_Returns400(int port)
    {
        await using var factory = new ManagementApiFactory();

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PutAsJsonAsync("/mgmt/services/svc-a", new
        {
            image = "registry/svc-a",
            tag = "latest",
            port,
            includeInHealth = false,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_DeployToken_Forbidden()
    {
        await using var factory = new ManagementApiFactory();

        var client = factory.CreateClient(ManagementApiFactory.DeployToken());
        var response = await client.PutAsJsonAsync("/mgmt/services/svc-a", new
        {
            image = "registry/svc-a",
            tag = "latest",
            port = 8080,
            includeInHealth = false,
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Deploys_ListAndGet_WithInstanceChildren()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a"));
        int deployId = 0;
        await factory.WithDbAsync(async db =>
        {
            var d = new DeployHistory
            {
                Service = "svc-a", ToDigest = "sha256:v1", Actor = "bob",
                Action = DeployAction.Deploy, Status = DeployStatus.InProgress,
                StartedAt = DateTimeOffset.UtcNow,
            };
            db.DeployHistory.Add(d);
            await db.SaveChangesAsync();
            deployId = d.Id;

            db.DeployInstanceStatus.Add(new DeployInstanceStatus
            {
                DeployId = deployId, InstanceId = "i-1",
                Status = DeployInstanceState.Converged, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());

        var list = await client.GetFromJsonAsync<JsonElement>("/mgmt/deploys");
        Assert.Contains(list.EnumerateArray(), e => e.GetProperty("id").GetInt32() == deployId);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/mgmt/deploys/{deployId}");
        Assert.Equal("svc-a", detail.GetProperty("deploy").GetProperty("service").GetString());
        var child = detail.GetProperty("instances").EnumerateArray().Single();
        Assert.Equal("i-1", child.GetProperty("instanceId").GetString());
        Assert.Equal(DeployInstanceState.Converged, child.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Deploy_Get_UnknownId_Returns404()
    {
        await using var factory = new ManagementApiFactory();

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.GetAsync("/mgmt/deploys/9999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Logs_ReturnsLines_ForInstance_AdminOnly()
    {
        await using var factory = new ManagementApiFactory();
        var ts = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
        factory.LogStore.Seed("svc-a", "i-1",
            new Gateway.Api.Management.LogLine(ts, "started"),
            new Gateway.Api.Management.LogLine(ts.AddSeconds(1), "ready"));

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var body = await client.GetFromJsonAsync<JsonElement>("/mgmt/services/svc-a/logs?instance=i-1&tail=50");

        var lines = body.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("started", lines[0].GetProperty("message").GetString());
        Assert.Equal(("svc-a", "i-1", 50), factory.LogStore.Queries.Single());
    }

    [Fact]
    public async Task Logs_DeployToken_Forbidden()
    {
        await using var factory = new ManagementApiFactory();

        var client = factory.CreateClient(ManagementApiFactory.DeployToken());
        var response = await client.GetAsync("/mgmt/services/svc-a/logs");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
