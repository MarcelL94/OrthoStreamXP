using System.Diagnostics;

namespace OrthoStreamXP;

/// <summary>Whole-file writes that readers never see half-done.</summary>
internal static class AtomicFile
{
    private const string PartialSuffix = ".partial";

    /// <summary>Writes beside the destination and renames over it. Returns the seconds it took.</summary>
    public static double Write(string destination, ReadOnlySpan<byte> data)
    {
        long started = Stopwatch.GetTimestamp();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = $"{destination}.{Guid.NewGuid():N}{PartialSuffix}";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                file.Write(data);
            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
        return Stopwatch.GetElapsedTime(started).TotalSeconds;
    }

    /// <summary>Removes temporary files left behind when a previous run was killed mid-write.</summary>
    public static int DeleteStalePartials(string root)
    {
        if (!Directory.Exists(root)) return 0;
        int deleted = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (string file in Directory.EnumerateFiles(root, "*" + PartialSuffix, options))
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (IOException)
            {
                // In use by another running instance; leave it.
            }
        }
        return deleted;
    }
}
