namespace Timeline.Web.Services;

public static class RuntimeDisplayText
{
    public static string ProductRuntimeMessage(ProductRuntimeRow product)
        => ProductRuntimeMessage(product.Message, product.State);

    public static string ProductRuntimeMessage(string? message, string? state = null)
    {
        var text = (message ?? "").Trim();
        var normalizedState = (state ?? "").Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(text))
        {
            return normalizedState switch
            {
                "failed" => "前回の操作で問題が発生しました。状態更新後に、起動または再起動を試してください。",
                _ => "",
            };
        }

        if (IsLocalApiConnectionFailure(text))
        {
            return "Timeline の操作機能に接続できません。Timeline を起動し直してください。";
        }

        if (text.Contains("Product health API is running.", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        if (text.Contains("Product health API is stopped.", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Product health API returned false.", StringComparison.OrdinalIgnoreCase))
        {
            return "製品の応答を確認できません。停止中の可能性があります。必要な場合は起動してください。";
        }

        if (text.Contains("Product directory was not found.", StringComparison.OrdinalIgnoreCase))
        {
            return "製品の配置が見つかりません。インストール状態を確認してください。";
        }

        if (text.Contains("Product health API base URL was not resolved.", StringComparison.OrdinalIgnoreCase))
        {
            return "製品の接続先設定を確認できません。設定またはインストール状態を確認してください。";
        }

        if (text.Contains("health check timed out", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "製品の応答に時間がかかっています。処理中の可能性があります。しばらく待ってから状態更新してください。";
        }

        if (text.Contains("is not running", StringComparison.OrdinalIgnoreCase))
        {
            return "製品が起動していません。必要な場合は起動してください。";
        }

        if (LooksLikeInternalEnglish(text))
        {
            return normalizedState switch
            {
                "failed" => "前回の操作で問題が発生しました。状態更新後に、起動または再起動を試してください。",
                "stopped" => "製品は停止しています。必要な場合は起動してください。",
                _ => "状態を確認できません。状態更新後にもう一度確認してください。",
            };
        }

        return text;
    }

    public static string ProductActionFailure(string displayName, string actionLabel, string? message)
    {
        var detail = ProductRuntimeMessage(message);
        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = "状態更新後にもう一度確認してください。";
        }

        return $"{displayName} の{actionLabel}に失敗しました。{detail}";
    }

    public static string ProductStatusLoadFailure(string? message)
    {
        var detail = ProductRuntimeMessage(message);
        return string.IsNullOrWhiteSpace(detail)
            ? "製品の状態を確認できませんでした。状態更新後にもう一度確認してください。"
            : $"製品の状態を確認できませんでした。{detail}";
    }

    public static string WorkerStatusDetail(TimelineDockerWorkerStatus? status)
    {
        if (status is null || status.Available && status.State.Equals("running", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        var state = (status.State ?? "").Trim().ToLowerInvariant();
        return state switch
        {
            "local_api_unreachable" => "Timeline の操作機能に接続できません。Timeline を起動し直してください。",
            "docker_unavailable" => "Docker が起動していません。Docker を起動してから、もう一度状態を確認してください。",
            "missing" => "自動処理の起動記録が見つかりません。必要な場合は復旧を実行してください。",
            "stale" => "自動処理から一定時間応答がありません。止まっている可能性があります。",
            "unreadable" => "Docker または worker の状態確認に失敗しました。復旧を実行すると起動を試します。",
            "stopped" => "自動処理は停止しています。必要な場合は復旧を実行してください。",
            "unknown" or "" => "自動処理の状態はまだ確認できていません。",
            _ => ProductRuntimeMessage(status.Message, state),
        };
    }

    private static bool IsLocalApiConnectionFailure(string text)
        => text.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
        || text.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
        || text.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeInternalEnglish(string text)
    {
        if (text.Any(ch => ch >= 0x3040))
        {
            return false;
        }

        return text.Contains("Timeline ", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Docker ", StringComparison.OrdinalIgnoreCase)
            || text.Contains("worker ", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Product ", StringComparison.OrdinalIgnoreCase)
            || text.Contains("HTTP ", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Ollama ", StringComparison.OrdinalIgnoreCase);
    }
}
