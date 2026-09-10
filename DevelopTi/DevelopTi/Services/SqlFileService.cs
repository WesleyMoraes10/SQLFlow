using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DevelopTi.Services;

/// <summary>Salva o texto de um editor SQL num arquivo local, via diálogo nativo do Windows.</summary>
public class SqlFileService
{
    public async Task<bool> SaveTextAsync(string suggestedFileName, string content)
    {
        var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView
            as Microsoft.UI.Xaml.Window;
        if (window is null) return false;

        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));

        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = suggestedFileName;
        picker.FileTypeChoices.Add("Arquivo de texto", new List<string> { ".txt" });

        var file = await picker.PickSaveFileAsync();
        if (file is null) return false;

        await FileIO.WriteTextAsync(file, content);
        return true;
    }
}
