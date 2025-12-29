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
        {
            throw new ArgumentException("R2TileStore configuration is missing ServiceUrl, AccessKey, or SecretKey");
        }

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

        var key = GetKey(hash);
        
        var request = new ListObjectsV2Request
        {
            BucketName = _bucketName,
            Prefix = key,
            MaxKeys = 1
        };

        var response = await _s3Client.ListObjectsV2Async(request);
        return response.S3Objects.Count > 0;
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
            AutoCloseStream = false
        };

        await _s3Client.PutObjectAsync(putRequest);
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
