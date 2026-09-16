namespace Gateway.Api.Containers;

/// <summary>
/// Outcome of an <see cref="IContainerRuntime.PruneImagesAsync"/> run: how many
/// images the daemon actually removed and how many bytes those images occupied.
/// </summary>
/// <param name="ImagesDeleted">Number of images the daemon reported as deleted.</param>
/// <param name="BytesReclaimed">
/// Sum of the <c>Size</c> reported for each successfully deleted image, in bytes.
/// </param>
public sealed record ImagePruneResult(int ImagesDeleted, long BytesReclaimed)
{
    /// <summary>The no-op result: no images deleted, no bytes reclaimed.</summary>
    public static ImagePruneResult None { get; } = new(0, 0L);
}
