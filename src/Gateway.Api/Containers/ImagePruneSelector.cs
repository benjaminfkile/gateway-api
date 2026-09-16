namespace Gateway.Api.Containers;

/// <summary>
/// Pure selection rules for <see cref="IContainerRuntime.PruneImagesAsync"/>.
/// Lives outside the Docker client so the test-only fake runtime can apply the
/// exact same rules, keeping production and test behaviour in lock-step.
/// </summary>
internal static class ImagePruneSelector
{
    /// <summary>
    /// A minimal projection of a local image needed to decide whether to
    /// prune it. Keeps the selector independent of Docker.DotNet types so the
    /// fake runtime can call it with its own image records.
    /// </summary>
    public sealed record Candidate(
        string Id,
        IReadOnlyList<string> RepoTags,
        IReadOnlyList<string> RepoDigests,
        DateTime Created,
        long Size);

    /// <summary>
    /// Return the images that should be deleted, in oldest-first order.
    /// Selection rules match <see cref="IContainerRuntime.PruneImagesAsync"/>
    /// exactly: keep anything whose ID or repo digest sha is in
    /// <paramref name="keepDigests"/>, anything created at or after
    /// <paramref name="cutoff"/>, and the <paramref name="keepPerRepository"/>
    /// most-recently-created images per repository bucket (dangling / untagged
    /// images share a single bucket).
    /// </summary>
    public static List<Candidate> SelectCandidates(
        IEnumerable<Candidate> images,
        HashSet<string> keepDigests,
        DateTime cutoff,
        int keepPerRepository)
    {
        var buckets = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
        foreach (var image in images)
        {
            var bucket = RepositoryOf(image);
            if (!buckets.TryGetValue(bucket, out var list))
            {
                list = new List<Candidate>();
                buckets[bucket] = list;
            }

            list.Add(image);
        }

        var candidates = new List<Candidate>();
        foreach (var (_, group) in buckets)
        {
            // Newest first so keepPerRepository skips the freshest images.
            group.Sort((a, b) => b.Created.CompareTo(a.Created));

            for (var i = 0; i < group.Count; i++)
            {
                if (i < keepPerRepository)
                {
                    continue;
                }

                var image = group[i];

                if (IsKept(image, keepDigests))
                {
                    continue;
                }

                if (image.Created >= cutoff)
                {
                    continue;
                }

                candidates.Add(image);
            }
        }

        // Delete oldest first so a failure mid-run still removes the stalest
        // layers, which are the ones filling the disk.
        candidates.Sort((a, b) => a.Created.CompareTo(b.Created));
        return candidates;
    }

    /// <summary>
    /// The repository portion of the image's first non-<c>&lt;none&gt;</c> repo
    /// tag (everything before the last <c>':'</c>), or the shared
    /// <c>&lt;none&gt;</c> bucket for dangling / untagged images.
    /// </summary>
    private static string RepositoryOf(Candidate image)
    {
        if (image.RepoTags is { Count: > 0 })
        {
            foreach (var tag in image.RepoTags)
            {
                if (string.IsNullOrEmpty(tag) || tag.StartsWith("<none>", StringComparison.Ordinal))
                {
                    continue;
                }

                var colon = tag.LastIndexOf(':');
                return colon > 0 ? tag[..colon] : tag;
            }
        }

        return "<none>";
    }

    private static bool IsKept(Candidate image, HashSet<string> keepDigests)
    {
        if (keepDigests.Count == 0)
        {
            return false;
        }

        if (keepDigests.Contains(image.Id))
        {
            return true;
        }

        if (image.RepoDigests is { Count: > 0 })
        {
            foreach (var repoDigest in image.RepoDigests)
            {
                if (string.IsNullOrEmpty(repoDigest))
                {
                    continue;
                }

                // A repo digest is "repo@sha256:...". Callers pass the sha part
                // (that is what PullImageAsync returns), but a full "repo@sha"
                // may also be in the set; check both forms.
                if (keepDigests.Contains(repoDigest))
                {
                    return true;
                }

                var at = repoDigest.IndexOf('@');
                if (at >= 0 && keepDigests.Contains(repoDigest[(at + 1)..]))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
