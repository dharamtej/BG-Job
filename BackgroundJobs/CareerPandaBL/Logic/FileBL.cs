using CareerPanda.Framework;
using CareerPanda.Framework.Storage;
using CareerPanda.Framework.Util;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Logic;

public class FileBL
{
    private readonly IFileStorageService _storage;
    private readonly ILogger<FileBL> _logger;

    public FileBL(IFileStorageService storage, ILogger<FileBL> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    public async Task<FrameworkResponse> UploadAsync(Stream content, string fileName, string contentType)
    {
        var response = new FrameworkResponse { Status = Status.Failed };
        try
        {
            var safeName = $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}";
            var url = await _storage.UploadAsync(content, safeName, contentType);
            response.Status = Status.Success;
            response.Message = url;
            response.Entity = new { FileName = safeName, Url = url };
        }
        catch (Exception ex)
        {
            response.Message = ex.Message;
            _logger.LogError(ex, "File upload failed");
        }
        return response;
    }

    public async Task<(Stream? Stream, string? ContentType, string? Error)> DownloadAsync(string filePath)
    {
        try
        {
            var stream = await _storage.DownloadAsync(filePath);
            if (stream == null)
                return (null, null, "File not found.");

            var contentType = UtilityManager.GetMimeType(filePath);
            return (stream, contentType, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File download failed for {Path}", filePath);
            return (null, null, ex.Message);
        }
    }
}
