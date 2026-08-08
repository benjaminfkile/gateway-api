namespace Gateway.Api.Containers;

/// <summary>
/// Thrown by <see cref="IContainerRuntime.StartServiceContainerAsync"/> when the
/// engine cannot start the container because its image reference does not exist
/// locally (Docker's "No such image ...@&lt;digest&gt;"). This is a runtime-agnostic
/// seam exception — <see cref="DockerContainerRuntime"/> translates Docker's
/// image-not-found error into it, and tests raise it from their fake — so the
/// reconciler can react to a stale pinned digest without depending on
/// <c>Docker.DotNet</c> exception types.
/// </summary>
public sealed class ContainerImageNotFoundException : Exception
{
    /// <summary>The image reference (e.g. <c>image@sha256:...</c>) that was not found.</summary>
    public string? ImageRef { get; }

    public ContainerImageNotFoundException(string message, string? imageRef = null, Exception? inner = null)
        : base(message, inner)
    {
        ImageRef = imageRef;
    }
}
