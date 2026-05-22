using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Azure.Storage.Blobs;
using CareerPanda.Framework.Configuration;

namespace CareerPanda.Framework.Storage;

public class FileStorageService : IFileStorageService
{
    private readonly Config _config;

    public FileStorageService(Config config)
    {
        _config = config;
    }

    public async Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken = default)
    {
        var source = _config.UploadSourceConfig.UploadSource?.ToUpperInvariant() ?? "AZURE";

        return source switch
        {
            "AWS" => await UploadToAwsAsync(content, fileName, contentType, cancellationToken),
            _ => await UploadToAzureAsync(content, fileName, contentType, cancellationToken)
        };
    }

    public async Task<Stream?> DownloadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var source = _config.UploadSourceConfig.UploadSource?.ToUpperInvariant() ?? "AZURE";

        return source switch
        {
            "AWS" => await DownloadFromAwsAsync(filePath, cancellationToken),
            _ => await DownloadFromAzureAsync(filePath, cancellationToken)
        };
    }

    public async Task DeleteAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var source = _config.UploadSourceConfig.UploadSource?.ToUpperInvariant() ?? "AZURE";

        if (source == "AWS")
            await DeleteFromAwsAsync(filePath, cancellationToken);
        else
            await DeleteFromAzureAsync(filePath, cancellationToken);
    }

    private async Task<string> UploadToAzureAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken)
    {
        var client = new BlobServiceClient(_config.AzureSettingsConfig.ConnectionName);
        var container = client.GetBlobContainerClient(_config.AzureSettingsConfig.ContainerName);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var blob = container.GetBlobClient(fileName);
        await blob.UploadAsync(content, overwrite: true, cancellationToken);
        return blob.Uri.ToString();
    }

    private async Task<Stream?> DownloadFromAzureAsync(string filePath, CancellationToken cancellationToken)
    {
        var client = new BlobServiceClient(_config.AzureSettingsConfig.ConnectionName);
        var container = client.GetBlobContainerClient(_config.AzureSettingsConfig.ContainerName);
        var blob = container.GetBlobClient(Path.GetFileName(filePath));
        if (!await blob.ExistsAsync(cancellationToken))
            return null;

        var ms = new MemoryStream();
        await blob.DownloadToAsync(ms, cancellationToken);
        ms.Position = 0;
        return ms;
    }

    private async Task DeleteFromAzureAsync(string filePath, CancellationToken cancellationToken)
    {
        var client = new BlobServiceClient(_config.AzureSettingsConfig.ConnectionName);
        var container = client.GetBlobContainerClient(_config.AzureSettingsConfig.ContainerName);
        await container.DeleteBlobIfExistsAsync(Path.GetFileName(filePath), cancellationToken: cancellationToken);
    }

    private async Task<string> UploadToAwsAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken)
    {
        var aws = _config.AWSSettingsConfig;
        using var client = new AmazonS3Client(aws.StorageAccountKey, aws.StorageSecret, RegionEndpoint.GetBySystemName(aws.RegionEndPoint));
        var request = new PutObjectRequest
        {
            BucketName = aws.ContainerName,
            Key = fileName,
            InputStream = content,
            ContentType = contentType
        };
        await client.PutObjectAsync(request, cancellationToken);
        return $"s3://{aws.ContainerName}/{fileName}";
    }

    private async Task<Stream?> DownloadFromAwsAsync(string filePath, CancellationToken cancellationToken)
    {
        var aws = _config.AWSSettingsConfig;
        using var client = new AmazonS3Client(aws.StorageAccountKey, aws.StorageSecret, RegionEndpoint.GetBySystemName(aws.RegionEndPoint));
        var key = Path.GetFileName(filePath);
        var response = await client.GetObjectAsync(aws.ContainerName, key, cancellationToken);
        var ms = new MemoryStream();
        await response.ResponseStream.CopyToAsync(ms, cancellationToken);
        ms.Position = 0;
        return ms;
    }

    private async Task DeleteFromAwsAsync(string filePath, CancellationToken cancellationToken)
    {
        var aws = _config.AWSSettingsConfig;
        using var client = new AmazonS3Client(aws.StorageAccountKey, aws.StorageSecret, RegionEndpoint.GetBySystemName(aws.RegionEndPoint));
        await client.DeleteObjectAsync(aws.ContainerName, Path.GetFileName(filePath), cancellationToken);
    }
}
