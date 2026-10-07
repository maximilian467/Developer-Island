using DeveloperIsland.Core.HomeCloud;

namespace DeveloperIsland.Tests.HomeCloud;

public class HomeCloudUploaderTests
{
    private readonly string _source = TestData.TempDirectory();
    private readonly string _target = Path.Combine(TestData.TempDirectory(), "inbox");

    private string Write(string relative, int size = 10)
    {
        var path = Path.Combine(_source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private sealed class Recorder : IProgress<HomeCloudProgress>
    {
        public List<HomeCloudProgress> Reports { get; } = [];

        public Action<HomeCloudProgress>? OnReport { get; set; }

        public void Report(HomeCloudProgress value)
        {
            Reports.Add(value);
            OnReport?.Invoke(value);
        }
    }

    /// <summary>File system sink with hooks for failures, delays and counting parts in flight.</summary>
    private sealed class TestSink : IFileSink
    {
        private readonly FileSystemSink _inner = new();
        private int _active;

        public bool ThrowOnWrite { get; set; }

        public TimeSpan WriteDelay { get; set; }

        public int MaxActive { get; private set; }

        public bool FileExists(string path) => _inner.FileExists(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public Stream CreateNew(string path)
        {
            var stream = _inner.CreateNew(path);
            MaxActive = Math.Max(MaxActive, Interlocked.Increment(ref _active));
            return new HookStream(stream, this);
        }

        public void Move(string from, string to)
        {
            _inner.Move(from, to);
            Interlocked.Decrement(ref _active);
        }

        public void DeleteFile(string path)
        {
            _inner.DeleteFile(path);
            Interlocked.Decrement(ref _active);
        }

        private sealed class HookStream(Stream inner, TestSink owner) : Stream
        {
            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => inner.Length;

            public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

            public override void Flush() => inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (owner.ThrowOnWrite)
                {
                    throw new IOException("boom secret-name.txt");
                }

                if (owner.WriteDelay > TimeSpan.Zero)
                {
                    await Task.Delay(owner.WriteDelay, cancellationToken);
                }

                await inner.WriteAsync(buffer, cancellationToken);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                }

                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                await base.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Copies_a_file_and_leaves_no_part_file()
    {
        var file = Write("a.txt", 100);

        var result = await new HomeCloudUploader(new FileSystemSink()).UploadAsync([file], _target);

        Assert.Equal(HomeCloudUploadStatus.Succeeded, result.Status);
        Assert.Equal(1, result.FileCount);
        Assert.Equal(100, result.Bytes);
        Assert.Equal(["a.txt"], Directory.GetFiles(_target).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Existing_file_names_get_a_numbered_suffix()
    {
        var file = Write("a.txt");
        var uploader = new HomeCloudUploader(new FileSystemSink());

        await uploader.UploadAsync([file], _target);
        await uploader.UploadAsync([file], _target);
        await uploader.UploadAsync([file], _target);

        Assert.Equal(["a (1).txt", "a (2).txt", "a.txt"], Directory.GetFiles(_target).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task A_leftover_part_file_does_not_block_the_name()
    {
        var file = Write("a.txt");
        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_target, "a.txt.part"), "other writer");

        var result = await new HomeCloudUploader(new FileSystemSink()).UploadAsync([file], _target);

        Assert.Equal(HomeCloudUploadStatus.Succeeded, result.Status);
        Assert.True(File.Exists(Path.Combine(_target, "a (1).txt")));
        Assert.True(File.Exists(Path.Combine(_target, "a.txt.part")));
    }

    [Fact]
    public async Task Existing_top_level_folders_get_a_numbered_suffix()
    {
        Write(Path.Combine("Photos", "x.jpg"));
        var folder = Path.Combine(_source, "Photos");
        Directory.CreateDirectory(Path.Combine(_target, "Photos"));
        var uploader = new HomeCloudUploader(new FileSystemSink());

        await uploader.UploadAsync([folder], _target);
        await uploader.UploadAsync([folder], _target);

        Assert.True(File.Exists(Path.Combine(_target, "Photos (1)", "x.jpg")));
        Assert.True(File.Exists(Path.Combine(_target, "Photos (2)", "x.jpg")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_target, "Photos")));
    }

    [Fact]
    public async Task Folders_are_copied_recursively_including_empty_subfolders()
    {
        Write(Path.Combine("Proj", "root.txt"));
        Write(Path.Combine("Proj", "src", "deep", "code.cs"));
        Directory.CreateDirectory(Path.Combine(_source, "Proj", "empty", "nested"));

        var result = await new HomeCloudUploader(new FileSystemSink()).UploadAsync([Path.Combine(_source, "Proj")], _target);

        Assert.Equal(HomeCloudUploadStatus.Succeeded, result.Status);
        Assert.Equal(2, result.FileCount);
        Assert.True(File.Exists(Path.Combine(_target, "Proj", "root.txt")));
        Assert.True(File.Exists(Path.Combine(_target, "Proj", "src", "deep", "code.cs")));
        Assert.True(Directory.Exists(Path.Combine(_target, "Proj", "empty", "nested")));
    }

    [Fact]
    public async Task A_write_failure_removes_the_part_file_and_reports_a_name_free_reason()
    {
        var file = Write("secret-name.txt");
        var sink = new TestSink { ThrowOnWrite = true };

        var result = await new HomeCloudUploader(sink).UploadAsync([file], _target);

        Assert.Equal(HomeCloudUploadStatus.Failed, result.Status);
        Assert.False(string.IsNullOrEmpty(result.Reason));
        Assert.DoesNotContain("secret-name", result.Reason);
        Assert.Empty(Directory.GetFiles(_target));
    }

    [Fact]
    public async Task A_missing_source_fails()
    {
        var result = await new HomeCloudUploader(new FileSystemSink()).UploadAsync([Path.Combine(_source, "nope.txt")], _target);

        Assert.Equal(HomeCloudUploadStatus.Failed, result.Status);
        Assert.Equal("Source not found", result.Reason);
    }

    [Fact]
    public async Task Cancel_removes_the_part_file_and_keeps_finished_files()
    {
        var first = Write("1-small.bin", 10);
        var second = Write("2-big.bin", 9 * 1024 * 1024);
        using var cts = new CancellationTokenSource();
        var progress = new Recorder { OnReport = p => { if (p.FileIndex == 2) { cts.Cancel(); } } };

        var result = await new HomeCloudUploader(new FileSystemSink()).UploadAsync([first, second], _target, progress, cts.Token);

        Assert.Equal(HomeCloudUploadStatus.Canceled, result.Status);
        Assert.Equal(["1-small.bin"], Directory.GetFiles(_target).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Progress_is_monotonic_ends_at_the_total_and_numbers_the_files()
    {
        var a = Write("a.bin", 5 * 1024 * 1024);
        var b = Write("b.bin", 100);
        var c = Write("c.bin", 0);
        var progress = new Recorder();

        await new HomeCloudUploader(new FileSystemSink()).UploadAsync([a, b, c], _target, progress);

        var total = 5L * 1024 * 1024 + 100;
        Assert.True(progress.Reports.Count >= 4);
        Assert.All(progress.Reports, p => Assert.Equal((total, 3), (p.TotalBytes, p.FileCount)));
        Assert.Equal(progress.Reports.OrderBy(p => p.BytesCopied).Select(p => p.BytesCopied), progress.Reports.Select(p => p.BytesCopied));
        Assert.Equal(progress.Reports.OrderBy(p => p.FileIndex).Select(p => p.FileIndex), progress.Reports.Select(p => p.FileIndex));
        Assert.Equal(new HomeCloudProgress(total, total, 3, 3), progress.Reports[^1]);
        Assert.Equal([1, 2, 3], progress.Reports.Select(p => p.FileIndex).Distinct());
    }

    [Fact]
    public async Task Two_parallel_uploads_run_one_after_the_other()
    {
        var a = Write("a.txt");
        var b = Write("b.txt");
        var sink = new TestSink { WriteDelay = TimeSpan.FromMilliseconds(100) };
        var uploader = new HomeCloudUploader(sink);

        var results = await Task.WhenAll(uploader.UploadAsync([a], _target), uploader.UploadAsync([b], _target));

        Assert.All(results, r => Assert.Equal(HomeCloudUploadStatus.Succeeded, r.Status));
        Assert.Equal(1, sink.MaxActive);
        Assert.Equal(2, Directory.GetFiles(_target).Length);
    }
}
