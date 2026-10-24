using Windows.Storage;
using Windows.Storage.Pickers;

namespace PickWeight_App.Services;

public sealed class FileDialogService
{
    public async Task<string?> OpenTextAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        foreach (var extension in extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        var file = await picker.PickSingleFileAsync();
        return file is null ? null : await FileIO.ReadTextAsync(file);
    }

    public async Task<string?> OpenPathAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task SaveTextAsync(string suggestedName, string extension, string content)
    {
        var path = await SavePathAsync(suggestedName, extension);
        if (path is not null)
        {
            await File.WriteAllTextAsync(path, content);
        }
    }

    /// <summary>弹出保存对话框，返回用户选择的完整路径；取消返回 null。</summary>
    public async Task<string?> SavePathAsync(string suggestedName, string extension)
    {
        var picker = new FileSavePicker { SuggestedFileName = suggestedName };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        picker.FileTypeChoices.Add(extension.TrimStart('.').ToUpperInvariant(), [extension]);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }
}
