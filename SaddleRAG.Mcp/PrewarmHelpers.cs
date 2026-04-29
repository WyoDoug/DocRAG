// PrewarmHelpers.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

namespace SaddleRAG.Mcp;

internal static class PrewarmHelpers
{
    public const int BytesPerMegabyte = 1024 * 1024;

    private const int ReadBufferSize = 81920;

    /// <summary>
    ///     Walks the directory recursively and reads every file end-to-end with sequential-scan
    ///     hint, forcing the OS file cache to populate before service startup. Returns the file
    ///     count and total bytes read. I/O exceptions on individual files are swallowed so
    ///     transient locks or perms don't fail the whole prewarm.
    /// </summary>
    public static (int FileCount, long TotalBytes) ReadAllFiles(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);

        long totalBytes = 0;
        int fileCount = 0;
        var buffer = new byte[ReadBufferSize];

        foreach(var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            long readBytes = ReadFileSafe(file, buffer);
            if (readBytes >= 0)
            {
                totalBytes += readBytes;
                fileCount++;
            }
        }

        return (fileCount, totalBytes);
    }

    private static long ReadFileSafe(string path, byte[] buffer)
    {
        long total = -1;
        try
        {
            using var fs = new FileStream(path,
                                          FileMode.Open,
                                          FileAccess.Read,
                                          FileShare.ReadWrite,
                                          buffer.Length,
                                          FileOptions.SequentialScan
                                         );
            total = 0;
            int read;
            do
            {
                read = fs.Read(buffer, 0, buffer.Length);
                total += read;
            }
            while(read > 0);
        }
        catch(IOException)
        {
        }
        catch(UnauthorizedAccessException)
        {
        }
        return total;
    }
}
