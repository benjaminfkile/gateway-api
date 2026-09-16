using Gateway.Api.Containers;

namespace Gateway.Api.Tests;

/// <summary>
/// Unit tests for the image-prune capability on <see cref="IContainerRuntime"/>.
/// Exercises the selection rules against <see cref="FakeContainerRuntime"/> — the
/// fake shares the production selector, so proving the fake obeys each rule
/// pins the Docker runtime's behaviour without a live daemon (tech-spec §4.3
/// Housekeeping). Also asserts that the fake records call arguments and can be
/// primed to throw, both needed by the reconciler tests in the follow-up task.
/// </summary>
public sealed class ImagePruneTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static FakeContainerRuntime NewFake()
    {
        var fake = new FakeContainerRuntime { UtcNow = () => Now };
        return fake;
    }

    private static FakeContainerRuntime.FakeImage Image(
        string id,
        DateTime created,
        long size = 100,
        string[]? tags = null,
        string[]? digests = null) =>
        new(
            Id: id,
            RepoTags: tags ?? Array.Empty<string>(),
            RepoDigests: digests ?? Array.Empty<string>(),
            Created: created,
            Size: size);

    [Fact]
    public async Task Keeps_ImageWhoseRepoDigest_IsInKeepSet_EvenWhenOldAndUnreferenced()
    {
        var fake = NewFake();
        var digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        fake.SetImages(new[]
        {
            Image("id-1", Now - TimeSpan.FromDays(10), digests: new[] { $"foo/bar@{digest}" }),
        });

        var result = await fake.PruneImagesAsync(
            new[] { digest }, TimeSpan.FromHours(48), keepPerRepository: 0);

        Assert.Equal(0, result.ImagesDeleted);
        Assert.Single(fake.Images);
    }

    [Fact]
    public async Task Keeps_ImageWhoseImageId_IsInKeepSet()
    {
        var fake = NewFake();
        fake.SetImages(new[]
        {
            Image("sha256:image-id-1", Now - TimeSpan.FromDays(10), tags: new[] { "foo/bar:v1" }),
        });

        var result = await fake.PruneImagesAsync(
            new[] { "sha256:image-id-1" }, TimeSpan.FromHours(48), keepPerRepository: 0);

        Assert.Equal(0, result.ImagesDeleted);
        Assert.Single(fake.Images);
    }

    [Fact]
    public async Task Keeps_ImageYoungerThanMinimumAge()
    {
        var fake = NewFake();
        fake.SetImages(new[]
        {
            Image("id-young", Now - TimeSpan.FromHours(1), tags: new[] { "foo/bar:v1" }),
        });

        var result = await fake.PruneImagesAsync(
            Array.Empty<string>(), TimeSpan.FromHours(48), keepPerRepository: 0);

        Assert.Equal(0, result.ImagesDeleted);
        Assert.Single(fake.Images);
    }

    [Fact]
    public async Task KeepPerRepository_KeepsThreeNewest_DeletesThreeOldest()
    {
        var fake = NewFake();
        var images = new List<FakeContainerRuntime.FakeImage>();
        for (var i = 0; i < 6; i++)
        {
            images.Add(Image($"id-{i}", Now - TimeSpan.FromDays(10 + i), size: 100 + i,
                tags: new[] { "foo/bar:v" + i }));
        }

        fake.SetImages(images);

        var result = await fake.PruneImagesAsync(
            Array.Empty<string>(), TimeSpan.FromHours(1), keepPerRepository: 3);

        Assert.Equal(3, result.ImagesDeleted);

        var remaining = fake.Images.Select(i => i.Id).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "id-0", "id-1", "id-2" }, remaining);
    }

    [Fact]
    public async Task KeepPerRepository_BucketsPerRepositoryNotGlobally()
    {
        var fake = NewFake();
        var images = new List<FakeContainerRuntime.FakeImage>();
        for (var i = 0; i < 5; i++)
        {
            images.Add(Image($"a-{i}", Now - TimeSpan.FromDays(10 + i), tags: new[] { "repo/a:v" + i }));
            images.Add(Image($"b-{i}", Now - TimeSpan.FromDays(10 + i), tags: new[] { "repo/b:v" + i }));
        }

        fake.SetImages(images);

        var result = await fake.PruneImagesAsync(
            Array.Empty<string>(), TimeSpan.FromHours(1), keepPerRepository: 3);

        Assert.Equal(4, result.ImagesDeleted);

        var remainingA = fake.Images.Count(i => i.Id.StartsWith("a-", StringComparison.Ordinal));
        var remainingB = fake.Images.Count(i => i.Id.StartsWith("b-", StringComparison.Ordinal));
        Assert.Equal(3, remainingA);
        Assert.Equal(3, remainingB);
    }

    [Fact]
    public async Task DanglingImages_AreBucketedTogether_AndPrunedByAge()
    {
        var fake = NewFake();
        fake.SetImages(new[]
        {
            Image("dangling-1", Now - TimeSpan.FromDays(10)),
            Image("dangling-2", Now - TimeSpan.FromDays(9), tags: new[] { "<none>:<none>" }),
            Image("dangling-3", Now - TimeSpan.FromDays(8)),
        });

        var result = await fake.PruneImagesAsync(
            Array.Empty<string>(), TimeSpan.FromHours(48), keepPerRepository: 1);

        Assert.Equal(2, result.ImagesDeleted);

        var remaining = fake.Images.Single();
        Assert.Equal("dangling-3", remaining.Id);
    }

    [Fact]
    public async Task PerImageFailure_DoesNotStopOthers_AndIsExcludedFromCount()
    {
        var fake = NewFake();
        fake.SetImages(new[]
        {
            Image("id-a", Now - TimeSpan.FromDays(10), size: 100, tags: new[] { "repo/a:v1" }),
            Image("id-b", Now - TimeSpan.FromDays(9), size: 200, tags: new[] { "repo/b:v1" }),
            Image("id-c", Now - TimeSpan.FromDays(8), size: 400, tags: new[] { "repo/c:v1" }),
        });
        fake.InUseImageIds.Add("id-b");

        var result = await fake.PruneImagesAsync(
            Array.Empty<string>(), TimeSpan.FromHours(1), keepPerRepository: 0);

        Assert.Equal(2, result.ImagesDeleted);
        Assert.Equal(500L, result.BytesReclaimed);

        Assert.Contains(fake.Images, i => i.Id == "id-b");
        Assert.DoesNotContain(fake.Images, i => i.Id == "id-a");
        Assert.DoesNotContain(fake.Images, i => i.Id == "id-c");
    }

    [Fact]
    public async Task BytesReclaimed_SumsOnlySuccessfullyDeletedImages()
    {
        var fake = NewFake();
        fake.SetImages(new[]
        {
            Image("id-a", Now - TimeSpan.FromDays(10), size: 111, tags: new[] { "repo/a:v1" }),
            Image("id-b", Now - TimeSpan.FromDays(9), size: 222, tags: new[] { "repo/b:v1" }),
            Image("id-c", Now - TimeSpan.FromDays(8), size: 444, tags: new[] { "repo/c:v1" }),
        });
        fake.InUseImageIds.Add("id-c");

        var result = await fake.PruneImagesAsync(
            Array.Empty<string>(), TimeSpan.FromHours(1), keepPerRepository: 0);

        Assert.Equal(333L, result.BytesReclaimed);
    }

    [Fact]
    public async Task Empty_KeepSet_EmptyImageList_And_ZeroKeep_AreSafe()
    {
        var fake = NewFake();

        var result = await fake.PruneImagesAsync(
            Array.Empty<string>(), TimeSpan.Zero, keepPerRepository: 0);

        Assert.Same(ImagePruneResult.None, result);
        Assert.Empty(fake.Images);
    }

    [Fact]
    public async Task NegativeInputs_AreClampedToZero()
    {
        var fake = NewFake();
        var futureCreated = Now + TimeSpan.FromDays(1);
        fake.SetImages(new[]
        {
            // "Future" image so a still-negative minimumAge (unclamped cutoff =
            // Now + 1 day) would sweep it up. Clamping to zero pins the cutoff
            // at Now, leaving the future image protected.
            Image("id-future", futureCreated, tags: new[] { "repo/a:v1" }),
            Image("id-old-a", Now - TimeSpan.FromDays(10), tags: new[] { "repo/b:v1" }),
            Image("id-old-b", Now - TimeSpan.FromDays(9), tags: new[] { "repo/b:v2" }),
        });

        var result = await fake.PruneImagesAsync(
            Array.Empty<string>(), TimeSpan.FromDays(-1), keepPerRepository: -3);

        // Future image survived (age clamp), and keepPerRepository=-3 behaved like 0
        // so both old images from repo/b were removed.
        Assert.Equal(2, result.ImagesDeleted);
        var survivor = Assert.Single(fake.Images);
        Assert.Equal("id-future", survivor.Id);
    }

    [Fact]
    public async Task RepoDigestSha_AloneInKeepSet_ProtectsImageWithFullRepoDigest()
    {
        var fake = NewFake();
        var sha = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        fake.SetImages(new[]
        {
            Image("local-id", Now - TimeSpan.FromDays(30), digests: new[] { $"registry.example.com/repo@{sha}" }),
        });

        var result = await fake.PruneImagesAsync(
            new[] { sha }, TimeSpan.FromHours(48), keepPerRepository: 0);

        Assert.Equal(0, result.ImagesDeleted);
    }

    [Fact]
    public async Task PruneImages_RecordsCallArguments_ForReconcilerAssertions()
    {
        var fake = NewFake();
        var keep = new[] { "sha256:one", "sha256:two" };

        await fake.PruneImagesAsync(keep, TimeSpan.FromHours(48), keepPerRepository: 3);
        await fake.PruneImagesAsync(Array.Empty<string>(), TimeSpan.FromHours(24), keepPerRepository: 1);

        Assert.Equal(2, fake.PruneCalls.Count);
        var calls = fake.PruneCalls.ToArray();
        Assert.Equal(keep, calls[0].KeepDigests);
        Assert.Equal(TimeSpan.FromHours(48), calls[0].MinimumAge);
        Assert.Equal(3, calls[0].KeepPerRepository);
        Assert.Empty(calls[1].KeepDigests);
        Assert.Equal(TimeSpan.FromHours(24), calls[1].MinimumAge);
        Assert.Equal(1, calls[1].KeepPerRepository);
    }

    [Fact]
    public async Task NextPruneThrow_MakesNextCallThrow_ThenClears()
    {
        var fake = NewFake();
        fake.NextPruneThrow = new InvalidOperationException("boom");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fake.PruneImagesAsync(Array.Empty<string>(), TimeSpan.Zero, keepPerRepository: 0));

        Assert.Equal("boom", thrown.Message);

        // Subsequent call succeeds — the throw is one-shot.
        var result = await fake.PruneImagesAsync(Array.Empty<string>(), TimeSpan.Zero, keepPerRepository: 0);
        Assert.Same(ImagePruneResult.None, result);
    }

    [Fact]
    public async Task RepositoryBucket_UsesPartBeforeLastColon_ForTagsWithColonsInRepo()
    {
        var fake = NewFake();
        fake.SetImages(new[]
        {
            Image("id-0", Now - TimeSpan.FromDays(10), tags: new[] { "registry.example.com:5000/foo:v1" }),
            Image("id-1", Now - TimeSpan.FromDays(9), tags: new[] { "registry.example.com:5000/foo:v2" }),
            Image("id-2", Now - TimeSpan.FromDays(8), tags: new[] { "registry.example.com:5000/foo:v3" }),
        });

        var result = await fake.PruneImagesAsync(
            Array.Empty<string>(), TimeSpan.FromHours(1), keepPerRepository: 2);

        Assert.Equal(1, result.ImagesDeleted);
        Assert.DoesNotContain(fake.Images, i => i.Id == "id-0");
    }
}
