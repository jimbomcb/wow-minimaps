using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Minimaps.Shared.Types;

namespace Minimaps.Shared.TileStores;

public class R2TileStore : ITileStore, IDisposable
{
    private readonly AmazonS3Client _s3Client;
    private readonly string _bucketName;

    public R2TileStore(IConfiguration configuration)
    {
        var serviceUrl = configuration["R2TileStore:ServiceUrl"];
        var accessKey = configuration["R2TileStore:AccessKey"];
        var secretKey = configuration["R2TileStore:SecretKey"];
        _bucketName = configuration["R2TileStore:BucketName"] ?? throw new ArgumentNullException("R2TileStore:BucketName");

        if (string.IsNullOrEmpty(serviceUrl) || string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey))
            throw new ArgumentException("R2TileStore configuration is missing ServiceUrl, AccessKey, or SecretKey");

        if (serviceUrl.Contains(_bucketName))
            throw new ArgumentException("R2TileStore:ServiceUrl should not contain the bucket name; it should be in the BucketName setting.");

        var config = new AmazonS3Config
        {
            ServiceURL = serviceUrl,
        };

        _s3Client = new AmazonS3Client(accessKey, secretKey, config);
    }

    public async Task<bool> HasAsync(ContentHash hash)
    {
        if (hash == default)
            throw new ArgumentException("Invalid MD5 hash", nameof(hash));

        // todo: caching

        var key = GetKey(hash);
        var request = new GetObjectMetadataRequest
        {
            BucketName = _bucketName,
            Key = key
        };

        try
        {
            var response = await _s3Client.GetObjectMetadataAsync(request);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<Stream> GetAsync(ContentHash hash)
    {
        if (hash == default)
            throw new ArgumentException("Invalid MD5 hash", nameof(hash));

        var key = GetKey(hash);

        try
        {
            var getRequest = new GetObjectRequest
            {
                BucketName = _bucketName,
                Key = key
            };
            var getResponse = await _s3Client.GetObjectAsync(getRequest);
            return getResponse.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Tile with hash '{hash}' not found", ex);
        }
    }

    public async Task SaveAsync(ContentHash hash, Stream stream, string contentType)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (hash == default)
            throw new ArgumentException("Invalid MD5 hash", nameof(hash));

        var key = GetKey(hash);

        var putRequest = new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            AutoCloseStream = false,
            // Not supported on R2, see https://developers.cloudflare.com/r2/examples/aws/aws-sdk-net/#upload-and-retrieve-objects
            DisablePayloadSigning = true,
            DisableDefaultChecksumValidation = true
        };

        await _s3Client.PutObjectAsync(putRequest);
    }

    // Only used for accelerating tile-sync, not used in production
    public async Task<HashSet<ContentHash>> GetAllHashesAsync(CancellationToken cancellationToken = default)
    {
        var hashes = new HashSet<ContentHash>();
        var request = new ListObjectsV2Request
        {
            BucketName = _bucketName
        };

        ListObjectsV2Response response;
        do
        {
            response = await _s3Client.ListObjectsV2Async(request, cancellationToken);
            foreach (var obj in response.S3Objects)
            {
                if (ContentHash.TryParse(obj.Key, out var hash))
                {
                    hashes.Add(hash);
                }
            }
            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated.Value);

        return hashes;
    }

    private string GetKey(ContentHash hash)
    {
        var hex = hash.ToHex();
        return $"{hex[..2]}/{hex}";
    }

    public void Dispose()
    {
        _s3Client?.Dispose();
        GC.SuppressFinalize(this);
    }
}
