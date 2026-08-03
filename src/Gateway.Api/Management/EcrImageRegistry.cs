using System.Text;
using Amazon.ECR;
using Amazon.ECR.Model;

namespace Gateway.Api.Management;

/// <summary>
/// <see cref="IImageRegistry"/> over Amazon ECR (AWSSDK.ECR). Resolves a
/// <c>repo:tag</c> to its immutable sha256 digest via <c>DescribeImages</c>
/// (tech-spec §4.4). Registered lazily in production so a gateway with no AWS
/// region configured still boots — the client is only constructed when a deploy
/// actually resolves a digest.
/// </summary>
public sealed class EcrImageRegistry : IImageRegistry
{
    private readonly IAmazonECR _ecr;

    public EcrImageRegistry(IAmazonECR ecr)
    {
        _ecr = ecr;
    }

    public async Task<string> ResolveDigestAsync(string image, string tag, CancellationToken ct = default)
    {
        var repository = RepositoryName(image);

        try
        {
            var response = await _ecr.DescribeImagesAsync(new DescribeImagesRequest
            {
                RepositoryName = repository,
                ImageIds = new List<ImageIdentifier> { new() { ImageTag = tag } },
            }, ct);

            var digest = response.ImageDetails?
                .Select(d => d.ImageDigest)
                .FirstOrDefault(d => !string.IsNullOrEmpty(d));

            if (string.IsNullOrEmpty(digest))
            {
                throw new ImageNotFoundException($"No digest for image '{image}:{tag}'.");
            }

            return digest;
        }
        catch (ImageNotFoundException)
        {
            throw;
        }
        catch (Amazon.ECR.Model.ImageNotFoundException)
        {
            throw new ImageNotFoundException($"Image '{image}:{tag}' not found in registry.");
        }
        catch (RepositoryNotFoundException)
        {
            throw new ImageNotFoundException($"Repository '{repository}' not found in registry.");
        }
    }

    public async Task<RegistryCredentials> GetCredentialsAsync(CancellationToken ct = default)
    {
        var response = await _ecr.GetAuthorizationTokenAsync(new GetAuthorizationTokenRequest(), ct);

        var data = response.AuthorizationData?.FirstOrDefault()
            ?? throw new InvalidOperationException("ECR returned no authorization data.");

        // The token is base64("user:password"); ECR uses the fixed user "AWS".
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(data.AuthorizationToken));
        var sep = decoded.IndexOf(':');
        if (sep < 0)
        {
            throw new InvalidOperationException("Malformed ECR authorization token.");
        }

        return new RegistryCredentials(decoded[..sep], decoded[(sep + 1)..], data.ProxyEndpoint);
    }

    /// <summary>
    /// Reduce a possibly fully-qualified image reference to the ECR repository name.
    /// A leading registry host (<c>&lt;acct&gt;.dkr.ecr.&lt;region&gt;.amazonaws.com/</c>)
    /// is stripped; any remaining namespace path is part of the repo name.
    /// </summary>
    private static string RepositoryName(string image)
    {
        var slash = image.IndexOf('/');
        if (slash > 0)
        {
            var first = image[..slash];
            if (first.Contains('.') || first.Contains(':'))
            {
                return image[(slash + 1)..];
            }
        }

        return image;
    }
}
