using CareerPanda.BL.Logic;
using CareerPanda.Framework;
using CareerPanda.Framework.MVC;
using CareerPanda.Framework.Util;
using Microsoft.AspNetCore.Mvc;

namespace CareerPanda.Web.Controllers;

public class FileController : CoreController
{
    private readonly FileBL _fileBl;
    private readonly ILogger<FileController> _logger;

    public FileController(FileBL fileBl, ILogger<FileController> logger)
    {
        _fileBl = fileBl;
        _logger = logger;
    }

    [HttpPost]
    [Route("api/file/upload")]
    [RequestSizeLimit(52_428_800)]
    public async Task<FrameworkResponse> Upload(IFormFile file)
    {
        ApplicationContext.UserId = UserId;
        ApplicationContext.CorrelationId = Guid.NewGuid().ToString();

        var response = new FrameworkResponse { Status = Status.Failed };
        if (file == null || file.Length == 0)
        {
            response.Message = "Please provide a file.";
            return response;
        }

        await using var stream = file.OpenReadStream();
        return await _fileBl.UploadAsync(stream, file.FileName, file.ContentType);
    }

    [HttpGet]
    [Route("api/file/download")]
    public async Task<IActionResult> Download([FromQuery] string path)
    {
        ApplicationContext.UserId = UserId;
        ApplicationContext.CorrelationId = path ?? Guid.NewGuid().ToString();

        if (string.IsNullOrWhiteSpace(path))
            return BadRequest("path query parameter is required.");

        var (stream, contentType, error) = await _fileBl.DownloadAsync(path);
        if (stream == null)
            return NotFound(error ?? "File not found.");

        return File(stream, contentType ?? "application/octet-stream", Path.GetFileName(path));
    }
}
