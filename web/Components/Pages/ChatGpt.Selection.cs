using Microsoft.AspNetCore.Components;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class ChatGpt
{
    private bool IsSelected(TimelineThreadRow thread) =>
        !string.IsNullOrWhiteSpace(thread.ItemId) && _selectedThreadIds.Contains(thread.ItemId);

    private List<string> SelectedItemIds() =>
        Threads.Where(IsSelected)
            .Select(thread => thread.ItemId)
            .Where(itemId => !string.IsNullOrWhiteSpace(itemId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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

    private static bool IsChecked(ChangeEventArgs args) =>
        args.Value is bool value && value;
}
