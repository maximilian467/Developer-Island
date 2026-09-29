using System.Text;

namespace DeveloperIsland.Core.Usage;

/// <summary>
/// Reads complete lines appended to a JSONL file since a byte offset. A trailing line without a
/// newline is left for the next read, because the writer may still be in the middle of it.
/// Opens files with full sharing so the writing tool is never blocked.
/// </summary>
public static class JsonlTailReader
{
    private const int ChunkSize = 64 * 1024;

    /// <summary>Returns the offset just after the last complete line that was read.</summary>
    public static long ReadLines(string path, long offset, Action<string> onLine, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        if (offset > stream.Length)
        {
            // The file was truncated or replaced: start over.
            offset = 0;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[ChunkSize];
        var pending = new MemoryStream();
        var consumed = offset;
        int read;

        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n')
                {
                    continue;
                }

                string line;
                if (pending.Length > 0)
                {
                    pending.Write(buffer, start, i - start);
                    line = Decode(pending.GetBuffer(), 0, (int)pending.Length);
                    consumed += pending.Length + 1;
                    pending.SetLength(0);
                }
                else
                {
                    line = Decode(buffer, start, i - start);
                    consumed += i - start + 1;
                }

                if (line.Length > 0)
                {
                    onLine(line);
                }

                start = i + 1;
            }

            if (start < read)
            {
                pending.Write(buffer, start, read - start);
            }
        }

        return consumed;
    }

    private static string Decode(byte[] bytes, int index, int count)
    {
        if (count > 0 && bytes[index + count - 1] == (byte)'\r')
        {
            count--;
        }

        return Encoding.UTF8.GetString(bytes, index, count);
    }
}
