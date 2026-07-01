namespace Timeline.Web.Services;

public static class ComputeModeResolver
{
    public static string NormalizeCommon(string? value)
    {
        var mode = value?.Trim().ToLowerInvariant() ?? "";
        return mode is "auto" or "gpu" or "cpu" ? mode : "auto";
    }

    public static string NormalizeProduct(string? value)
    {
        var mode = value?.Trim().ToLowerInvariant() ?? "";
        return mode == "gpu" ? "gpu" : "cpu";
    }

    public static string ResolveProduct(
        string? productValue,
        TimelineCommonAiSettings? commonAi,
        IEnumerable<string>? fallbackGpuDevices = null)
    {
        var productMode = productValue?.Trim().ToLowerInvariant() ?? "";
        if (productMode is "gpu" or "cpu")
        {
            return productMode;
        }

        return ResolveCommonForProduct(commonAi, fallbackGpuDevices);
    }

    public static bool TryResolveProduct(
        string? productValue,
        TimelineCommonAiSettings? commonAi,
        IEnumerable<string>? fallbackGpuDevices,
        out string mode)
    {
        var normalized = productValue?.Trim().ToLowerInvariant() ?? "";
        mode = normalized switch
        {
            "gpu" => "gpu",
            "cpu" => "cpu",
            "auto" => ResolveCommonForProduct(commonAi, fallbackGpuDevices),
            _ => ResolveCommonForProduct(commonAi, fallbackGpuDevices),
        };

        return normalized is "gpu" or "cpu" or "auto";
    }

    public static string ResolveCommonForProduct(
        TimelineCommonAiSettings? commonAi,
        IEnumerable<string>? fallbackGpuDevices = null)
    {
        var commonMode = NormalizeCommon(commonAi?.ComputeMode);
        if (commonMode is "gpu" or "cpu")
        {
            return commonMode;
        }

        return ResolveAuto(commonAi, fallbackGpuDevices);
    }

    public static string ResolveAuto(
        TimelineCommonAiSettings? commonAi,
        IEnumerable<string>? fallbackGpuDevices = null)
    {
        var resolved = commonAi?.ResolvedComputeMode?.Trim().ToLowerInvariant() ?? "";
        if (resolved is "gpu" or "cpu")
        {
            return resolved;
        }

        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return "cpu";
        }

        return HasAiGpuDevice(fallbackGpuDevices) ? "gpu" : "cpu";
    }

    public static bool ProductNeedsAttention(bool productFound, bool known, string mode, string productMode) =>
        productFound && (!known || NormalizeProduct(mode) != productMode);

    public static string ProductBaseline(bool known, string mode) =>
        known ? NormalizeProduct(mode) : "";

    public static bool HasAiGpuDevice(IEnumerable<string>? devices) =>
        devices?.Any(IsAiGpuDevice) == true;

    public static bool IsAiGpuDevice(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Contains("nvidia", StringComparison.Ordinal)
            || normalized.Contains("cuda", StringComparison.Ordinal);
    }
}
