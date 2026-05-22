namespace CareerPanda.Framework.Storage;

public interface IFileStorageService
{
    Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken = default);

    Task<Stream?> DownloadAsync(string filePath, CancellationToken cancellationToken = default);

    Task DeleteAsync(string filePath, CancellationToken cancellationToken = default);
}
