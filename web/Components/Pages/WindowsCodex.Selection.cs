using Microsoft.AspNetCore.Components;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class WindowsCodex
{
    private List<string> SelectedItemIds() =>
        Threads.Where(IsSelected)
            .Select(thread => thread.ItemId)
            .Where(itemId => !string.IsNullOrWhiteSpace(itemId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private bool IsSelected(TimelineThreadRow thread) =>
        !string.IsNullOrWhiteSpace(thread.ItemId) && _selectedThreadIds.Contains(thread.ItemId);

    private void ToggleThread(TimelineThreadRow thread, bool selected)
    {
        _operationMessage = null;
        if (string.IsNullOrWhiteSpace(thread.ItemId))
        {
            return;
        }

        if (selected)
        {
            _selectedThreadIds.Add(thread.ItemId);
        }
        else
        {
            _selectedThreadIds.Remove(thread.ItemId);
        }
    }

    private void ToggleAllThreads(ChangeEventArgs args)
    {
        _operationMessage = null;
        if (IsChecked(args))
        {
            foreach (var thread in Threads)
            {
                if (!string.IsNullOrWhiteSpace(thread.ItemId))
                {
                    _selectedThreadIds.Add(thread.ItemId);
                }
            }
        }
        else
        {
            foreach (var thread in Threads)
            {
                _selectedThreadIds.Remove(thread.ItemId);
            }
        }
    }

    private void ClearSelection()
    {
        _operationMessage = null;
        _selectedThreadIds.Clear();
    }

    private void RemoveMissingSelections()
    {
        var visible = Threads.Select(thread => thread.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selectedThreadIds.RemoveWhere(itemId => !visible.Contains(itemId));
    }

    private static string EmptyText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private string ShortDate(string? value) =>
        UiFormat.ShortDate(value ?? "", DisplayTimeZoneId);

    private static bool IsChecked(ChangeEventArgs args) =>
        args.Value is bool value && value;

    private static string DeleteMessage(TimelineThreadItemsDeleteResult result, int selectedCount)
    {
        if (result.DeletedCount > 0)
        {
            var message = $"{result.DeletedCount} 件の生成物を削除しました。";
            if (result.MissingItemIds.Count > 0)
            {
                message += $" 削除済みまたは見つからない項目: {result.MissingItemIds.Count} 件。";
            }
            return message;
        }

        return $"選択した {selectedCount} 件に削除対象の生成物はありませんでした。";
    }

    private static string ThreadUrl(TimelineThreadRow thread) =>
        $"windows-codex/thread/{Uri.EscapeDataString(thread.ItemId)}";

    private static string StateLabel(string? state)
    {
        return (state ?? "").Trim().ToLowerInvariant() switch
        {
            "completed" => "完了",
            "available" => "取得済み",
            "running" => "処理中",
            "queued" => "待機中",
            "failed" => "失敗",
            "canceled" => "中断",
            "" => "未作成",
            var value => value,
        };
    }

    private static string StatePillClass(string? state)
    {
        return (state ?? "").Trim().ToLowerInvariant() switch
        {
            "completed" => "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800",
            "available" => "tfa-status-pill border-teal-200 bg-teal-50 text-teal-800",
            "running" => "tfa-status-pill border-sky-200 bg-sky-50 text-sky-800",
            "queued" => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
            "failed" => "tfa-status-pill border-red-200 bg-red-50 text-red-800",
            _ => "tfa-status-pill border-slate-200 bg-slate-50 text-slate-700",
        };
    }
}
