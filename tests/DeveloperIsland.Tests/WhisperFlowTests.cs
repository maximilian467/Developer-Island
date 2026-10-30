using DeveloperIsland.Core.Dictation;
using DeveloperIsland.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace DeveloperIsland.Tests;

/// <summary>WhisperFlow's status file: read, judged by age and process, and followed while the integration is on.</summary>
public class WhisperFlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static string Status(string state, DateTimeOffset at, int pid = 4242) =>
        $$"""{"version":1,"state":"{{state}}","at":"{{at:yyyy-MM-ddTHH:mm:ss.fffzzz}}","pid":{{pid}}}""";

    [Theory]
    [InlineData("recording", DictationPhase.Recording)]
    [InlineData("transcribing", DictationPhase.Transcribing)]
    [InlineData("done", DictationPhase.Done)]
    [InlineData("empty", DictationPhase.NoSpeech)]
    [InlineData("error", DictationPhase.Failed)]
    [InlineData("ready", DictationPhase.Idle)]
    [InlineData("loading", DictationPhase.Idle)]
    [InlineData("stopped", DictationPhase.Idle)]
    [InlineData("something-newer", DictationPhase.Idle)]
    public void Each_state_maps_to_what_the_island_shows(string state, DictationPhase phase)
    {
        var status = WhisperFlowStatusFile.Parse(Status(state, Now))!;

        Assert.Equal(new WhisperFlowStatus(phase, Now, 4242), status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"state":"recording"}""")]
    [InlineData("""{"state":"recording","at":"yesterday"}""")]
    public void A_broken_or_partial_file_is_not_a_status(string json) =>
        Assert.Null(WhisperFlowStatusFile.Parse(json));

    [Fact]
    public void Python_timestamps_with_milliseconds_and_offset_are_read()
    {
        var status = WhisperFlowStatusFile.Parse("""{"version":1,"state":"done","at":"2026-10-30T09:00:00.250+00:00","pid":7}""")!;

        Assert.Equal(Now.AddMilliseconds(250), status.At);
    }

    [Fact]
    public void A_recording_shows_only_while_its_whisperflow_process_runs()
    {
        var recording = new WhisperFlowStatus(DictationPhase.Recording, Now, 4242);

        Assert.Equal(DictationPhase.Recording, WhisperFlowStatusFile.Effective(recording, Now.AddMinutes(2), _ => true));
        Assert.Equal(DictationPhase.Idle, WhisperFlowStatusFile.Effective(recording, Now.AddMinutes(2), _ => false));
        Assert.Equal(DictationPhase.Idle, WhisperFlowStatusFile.Effective(recording, Now + WhisperFlowStatusFile.MaxActiveAge, _ => true));
    }

    [Fact]
    public void A_result_shows_for_a_few_seconds_even_after_whisperflow_quit()
    {
        var failed = new WhisperFlowStatus(DictationPhase.Failed, Now, 4242);

        Assert.Equal(DictationPhase.Failed, WhisperFlowStatusFile.Effective(failed, Now.AddSeconds(1), _ => false));
        Assert.Equal(DictationPhase.Idle, WhisperFlowStatusFile.Effective(failed, Now + WhisperFlowStatusFile.ResultDuration, _ => false));
        Assert.Equal(WhisperFlowStatusFile.ResultDuration - TimeSpan.FromSeconds(1), WhisperFlowStatusFile.NextCheck(failed, Now.AddSeconds(1)));
    }

    [Fact]
    public void Off_by_default()
    {
        Assert.False(new AppSettings().WhisperFlowEnabled);
        Assert.False(new AppSettings().Clone().WhisperFlowEnabled);
    }

    [Fact]
    public void Turned_on_it_shows_the_current_state_and_lets_a_result_go_after_a_few_seconds()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "status.json");
        File.WriteAllText(path, Status("done", Now));
        var time = new FakeTimeProvider(Now.AddSeconds(1));
        using var service = new WhisperFlowService(path, time, _ => true);
        var seen = new List<DictationPhase>();
        service.Changed += seen.Add;

        service.Configure(true);
        Assert.Equal(DictationPhase.Done, service.Phase);

        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(DictationPhase.Idle, service.Phase);
        Assert.Equal([DictationPhase.Done, DictationPhase.Idle], seen);
    }

    [Fact]
    public void A_whisperflow_closed_mid_recording_clears_within_seconds()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "status.json");
        File.WriteAllText(path, Status("recording", Now));
        var running = true;
        var time = new FakeTimeProvider(Now);
        using var service = new WhisperFlowService(path, time, _ => running);

        service.Configure(true);
        Assert.Equal(DictationPhase.Recording, service.Phase);

        running = false;
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(DictationPhase.Idle, service.Phase);
    }

    [Fact]
    public void Turned_off_it_shows_nothing_and_reads_nothing()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "status.json");
        File.WriteAllText(path, Status("recording", Now));
        using var service = new WhisperFlowService(path, new FakeTimeProvider(Now), _ => true);

        service.Configure(false);
        Assert.Equal(DictationPhase.Idle, service.Phase);

        service.Configure(true);
        service.Configure(false);
        Assert.Equal(DictationPhase.Idle, service.Phase);
    }

    [Fact]
    public void Without_a_status_file_there_is_nothing_to_show()
    {
        using var dir = new TempDirectory();
        using var service = new WhisperFlowService(Path.Combine(dir.Path, "status.json"), new FakeTimeProvider(Now), _ => true);

        service.Configure(true);

        Assert.Equal(DictationPhase.Idle, service.Phase);
    }

    [Fact]
    public void Labels_are_short_and_idle_has_none()
    {
        Assert.Equal("WhisperFlow", DictationText.Label(DictationPhase.Recording));
        Assert.Equal("Transcribing…", DictationText.Label(DictationPhase.Transcribing));
        Assert.Empty(DictationText.Label(DictationPhase.Idle));
        Assert.All(Enum.GetValues<DictationPhase>().Where(p => p != DictationPhase.Idle), p => Assert.InRange(DictationText.Label(p).Length, 1, 13));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "di-whisperflow-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
