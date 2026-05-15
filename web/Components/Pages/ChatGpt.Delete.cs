using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class ChatGpt
{
    private void OpenDeleteModal()
    {
        if (SupportsGeneratedDelete && HasSelection)
        {
            _deleteModalOpen = true;
        }
    }

    private void CloseDeleteModal()
    {
        if (!_deleting)
        {
            _deleteModalOpen = false;
        }
    }

    private async Task ConfirmDeleteSelectedAsync()
    {
        var itemIds = SelectedItemIds();
        if (itemIds.Count == 0)
        {
            _error = "削除するスレッドを選択してください。";
            return;
        }

        if (!SupportsGeneratedDelete)
        {
            _error = "TimelineForChatGPT does not support generated item removal in the current product CLI contract.";
            return;
        }

        _deleting = true;
        _error = null;
        _operationMessage = null;
        try
        {
            var result = await Timeline.DeleteChatGptItemsAsync(new TimelineThreadItemsRequest
            {
                ItemIds = itemIds,
            });
            _selectedThreadIds.Clear();
            _deleteModalOpen = false;
            _operationMessage = DeleteMessage(result, itemIds.Count);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _deleting = false;
        }
    }

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

    private static string EmptyText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;
}
