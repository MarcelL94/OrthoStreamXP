using System.Buffers.Binary;
using System.Text;

namespace OrthoStreamXP.Tests;

/// <summary>A scratch folder that is deleted after the test, with helpers to lay out package files.</summary>
internal sealed class TempTree : IDisposable
{
    public TempTree() => Directory.CreateDirectory(Root);

    public string Root { get; } = Path.Join(Path.GetTempPath(), "orthostreamxp-tests", Guid.NewGuid().ToString("N"));

    public string PathOf(string relative) => Path.Join(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public string WriteText(string relative, string content)
    {
        string path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public string WriteBytes(string relative, byte[] content)
    {
        string path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>Just the PNG signature and IHDR size fields, which is all the mask-size check reads.</summary>
    public string WritePngHeader(string relative, int width)
    {
        byte[] png = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(8), 13);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), width);
        return WriteBytes(relative, png);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A handle may still be closing; the temp folder is cleaned by the OS eventually.
        }
    }
}
