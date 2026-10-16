using System.IO.Compression;
using System.Runtime.InteropServices;
namespace Anno1800IslandTool.Core.Terrain;

/// <summary>Lossless, immutable storage. Reading does not retain an expanded copy.</summary>
internal sealed class CompactGeneratedText
{
    private readonly string? _plain;
    private readonly byte[]? _compressed;
    private readonly int _length;
    public bool HasContent { get; }
    public CompactGeneratedText(string? text)
    {
        text ??= string.Empty;
        HasContent = !string.IsNullOrWhiteSpace(text);
        _length = text.Length;
        if (text.Length < 8192) { _plain = text; return; }
        using var buffer = new MemoryStream();
        using (var writer = new BrotliStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            writer.Write(MemoryMarshal.AsBytes(text.AsSpan()));
        if (buffer.Length >= (long)text.Length * sizeof(char)) _plain = text;
        else _compressed = buffer.ToArray();
    }
    public string Read()
    {
        if (_plain is not null) return _plain;
        return string.Create(_length, _compressed!, static (chars, bytes) =>
        {
            using var buffer = new MemoryStream(bytes, writable: false);
            using var reader = new BrotliStream(buffer, CompressionMode.Decompress);
            reader.ReadExactly(MemoryMarshal.AsBytes(chars));
        });
    }
}
