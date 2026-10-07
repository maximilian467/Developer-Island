namespace DeveloperIsland.Core.HomeCloud;

/// <summary>The target side of an upload, abstracted so the copy logic can be tested without a network share.</summary>
public interface IFileSink
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    void CreateDirectory(string path);

    /// <summary>Creates a new file for writing; throws <see cref="IOException"/> when it already exists.</summary>
    Stream CreateNew(string path);

    /// <summary>Renames without overwriting; throws <see cref="IOException"/> when the target exists.</summary>
    void Move(string from, string to);

    void DeleteFile(string path);
}

/// <summary>Production sink on top of System.IO; UNC paths work.</summary>
public sealed class FileSystemSink : IFileSink
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public Stream CreateNew(string path) =>
        new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);

    public void Move(string from, string to) => File.Move(from, to, overwrite: false);

    public void DeleteFile(string path) => File.Delete(path);
}
