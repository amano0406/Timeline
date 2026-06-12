namespace Timeline.Web.Components.Shared;

public sealed record TextVolumeSegment(double StartSec, double EndSec, string? Text);

public sealed record TextVolumeChartPoint(
    string Label,
    int CharacterCount,
    double StartSec,
    double EndSec);

