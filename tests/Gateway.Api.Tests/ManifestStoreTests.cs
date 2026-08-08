using Gateway.Api.Manifest;

namespace Gateway.Api.Tests;

/// <summary>
/// Direct unit coverage for <see cref="InMemoryManifestStore"/>'s mutating
/// operations (the EF implementation is exercised end-to-end by the Management API
/// integration tests). Focused here on <c>DeleteAsync</c> (tech-spec §4.4).
/// </summary>
public class ManifestStoreTests
{
    [Fact]
    public async Task DeleteAsync_RemovesRow()
    {
        var store = new InMemoryManifestStore();
        await store.UpsertAsync(ManagementTestData.Manifest("svc-a"));

        await store.DeleteAsync("svc-a");

        Assert.Null(await store.GetAsync("svc-a"));
        Assert.Empty(await store.GetAllAsync());
    }

    [Fact]
    public async Task DeleteAsync_UnknownService_Throws()
    {
        var store = new InMemoryManifestStore();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.DeleteAsync("ghost"));
    }

    [Fact]
    public async Task DeleteAsync_LeavesOtherRows_Intact()
    {
        var store = new InMemoryManifestStore();
        await store.UpsertAsync(ManagementTestData.Manifest("svc-a"));
        await store.UpsertAsync(ManagementTestData.Manifest("svc-b"));

        await store.DeleteAsync("svc-a");

        Assert.Null(await store.GetAsync("svc-a"));
        Assert.NotNull(await store.GetAsync("svc-b"));
    }
}
