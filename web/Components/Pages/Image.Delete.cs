using Microsoft.JSInterop;
using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class Image
{
    private async Task DeleteSelectedAsync()
    {
        var itemIds = SelectedGeneratedItemIds();
        if (itemIds.Count == 0)
        {
            return;
        }

        var accepted = await Js.InvokeAsync<bool>("confirm", $"{itemIds.Count} 件の生成物を削除します。元画像は削除しません。");
        if (!accepted)
        {
            return;
        }

        _deleting = true;
        _error = null;
        _operationMessage = null;
        try
        {
            var result = await Timeline.DeleteImageItemsAsync(new ImageItemsRequest { ItemIds = itemIds });
            _operationMessage = $"{result.DeletedCount} 件削除しました。";
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
