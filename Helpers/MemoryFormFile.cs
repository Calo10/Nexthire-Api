using Microsoft.AspNetCore.Http;

namespace nexthire_api.Helpers;

/// <summary>In-memory <see cref="IFormFile"/> for server-generated uploads (e.g. Vera creatives).</summary>
public sealed class MemoryFormFile : IFormFile
{
    private readonly Stream _stream;

    public MemoryFormFile(Stream stream, string fileName, string contentType)
    {
        _stream = stream;
        FileName = fileName;
        ContentType = contentType;
        Length = stream.CanSeek ? stream.Length : 0;
        Name = "file";
    }

    public string ContentType { get; }
    public string ContentDisposition => $"form-data; name=\"{Name}\"; filename=\"{FileName}\"";
    public IHeaderDictionary Headers { get; } = new HeaderDictionary();
    public long Length { get; }
    public string Name { get; }
    public string FileName { get; }

    public void CopyTo(Stream target) => _stream.CopyTo(target);

    public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) =>
        _stream.CopyToAsync(target, cancellationToken);

    public Stream OpenReadStream()
    {
        if (_stream.CanSeek)
            _stream.Position = 0;
        return _stream;
    }
}
