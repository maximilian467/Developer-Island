using DeveloperIsland.Core.HomeCloud;
using DeveloperIsland.Core.Island;

namespace DeveloperIsland.ViewModels;

/// <summary>What the Home Cloud drop zone shows: hover, upload progress, then the result.</summary>
public sealed class DropViewModel : ObservableObject
{
    public DropPhase Phase { get; private set; }

    /// <summary>The reachability check failed while the zone waits for the drop.</summary>
    public bool IsUnreachable { get; private set; }

    public string Title { get; private set; } = HomeCloudFeedback.HoverTitle;

    public string Detail { get; private set; } = HomeCloudFeedback.HoverDetail;

    /// <summary>0 to 1 while uploading, otherwise null.</summary>
    public double? Progress { get; private set; }

    public string AccessibleText => $"{Title}. {Detail}";

    public void BeginHover()
    {
        Phase = DropPhase.Hover;
        IsUnreachable = false;
        (Title, Detail) = (HomeCloudFeedback.HoverTitle, HomeCloudFeedback.HoverDetail);
        Progress = null;
        OnAllPropertiesChanged();
    }

    public void SetReachability(HomeCloudReachabilityResult result)
    {
        if (Phase != DropPhase.Hover)
        {
            return;
        }

        IsUnreachable = !result.Reachable;
        (Title, Detail) = result.Reachable
            ? (HomeCloudFeedback.HoverTitle, HomeCloudFeedback.HoverDetail)
            : HomeCloudFeedback.Unreachable(result.Reason);
        OnAllPropertiesChanged();
    }

    public void BeginUpload()
    {
        Phase = DropPhase.Uploading;
        IsUnreachable = false;
        (Title, Detail) = (HomeCloudFeedback.UploadTitle, HomeCloudFeedback.PreparingDetail);
        Progress = 0;
        OnAllPropertiesChanged();
    }

    public void Report(HomeCloudProgress progress)
    {
        if (Phase != DropPhase.Uploading)
        {
            return;
        }

        Detail = HomeCloudFeedback.ProgressText(progress);
        Progress = HomeCloudFeedback.Percent(progress) / 100.0;
        OnAllPropertiesChanged();
    }

    public void Finish(HomeCloudUploadResult result)
    {
        Phase = result.Status == HomeCloudUploadStatus.Succeeded ? DropPhase.Succeeded : DropPhase.Failed;
        (Title, Detail) = HomeCloudFeedback.Result(result);
        Progress = null;
        OnAllPropertiesChanged();
    }
}
