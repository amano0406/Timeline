using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class WindowsCodex
{
    private void OpenDeleteModal()
    {
        if (HasSelection)
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

        _deleting = true;
        _error = null;
        _operationMessage = null;
        try
        {
            var result = await Timeline.DeleteWindowsCodexItemsAsync(new TimelineThreadItemsRequest
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
}
