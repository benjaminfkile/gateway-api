using System.Collections.Concurrent;

namespace Gateway.Api.RealTime;

/// <summary>
/// Safe insert/remove for two-level "outer key → bucket → inner key" concurrent maps
/// whose EMPTY buckets are pruned (review finding): a bare
/// <c>if (bucket.IsEmpty) map.TryRemove(key)</c> races a concurrent insert that just
/// repopulated the same bucket instance, unlinking a live entry. Both presence
/// registries' membership maps share this shape, so the coordination lives here once:
/// inserts take the per-bucket lock and re-check the bucket is still linked (retrying
/// when it was pruned underneath them); removals prune only while holding the same lock.
/// </summary>
internal static class PrunableBuckets
{
    /// <summary>Insert via <paramref name="insert"/> into the bucket for <paramref name="outerKey"/>.</summary>
    public static void Insert<TValue>(
        ConcurrentDictionary<string, ConcurrentDictionary<string, TValue>> map,
        string outerKey,
        Action<ConcurrentDictionary<string, TValue>> insert)
    {
        while (true)
        {
            var bucket = map.GetOrAdd(
                outerKey, _ => new ConcurrentDictionary<string, TValue>(StringComparer.Ordinal));
            lock (bucket)
            {
                if (map.TryGetValue(outerKey, out var current) && ReferenceEquals(current, bucket))
                {
                    insert(bucket);
                    return;
                }
            }
            // The bucket was pruned while we acquired the lock; loop and re-create.
        }
    }

    /// <summary>
    /// Remove <paramref name="innerKey"/> from the bucket for <paramref name="outerKey"/>,
    /// pruning the bucket when it empties. Returns whether the inner key was present.
    /// </summary>
    public static bool Remove<TValue>(
        ConcurrentDictionary<string, ConcurrentDictionary<string, TValue>> map,
        string outerKey,
        string innerKey)
    {
        if (!map.TryGetValue(outerKey, out var bucket))
        {
            return false;
        }

        lock (bucket)
        {
            var removed = bucket.TryRemove(innerKey, out _);
            if (bucket.IsEmpty)
            {
                // Inserts hold this same lock and re-check linkage, so unlinking here
                // cannot orphan a concurrent add.
                map.TryRemove(new KeyValuePair<string, ConcurrentDictionary<string, TValue>>(outerKey, bucket));
            }

            return removed;
        }
    }
}
