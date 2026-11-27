using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace ShiftBridge.Desktop;

/// <summary>
/// Choix de fichiers par les boîtes du système (Avalonia), pour l'export / import des réglages. Ici plutôt que dans l'hôte macOS :
/// ce projet se compile partout, l'hôte macOS seulement sur un Mac.
/// </summary>
public static class DesktopFiles
{
    private static readonly FilePickerFileType Json = new("JSON") { Patterns = ["*.json"], MimeTypes = ["application/json"] };

    public static async Task<bool> SaveTextAsync(TopLevel owner, string suggestedName, string content)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            FileTypeChoices = [Json],
        });
        if (file is null) return false;
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        await writer.WriteAsync(content);
        return true;
    }

    public static async Task<string?> OpenTextAsync(TopLevel owner)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [Json, FilePickerFileTypes.All],
        });
        if (files.Count == 0) return null;
        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
