using System.IO;
using SQLFlow.Dialogs;
using WinRT.Interop;

namespace SQLFlow.Services;

/// <summary>Salva o texto de um editor SQL num arquivo local, via diálogo nativo do Windows. Os
/// diálogos em si (NativeFileDialogs, projeto SQLFlow.Dialogs) usam WinForms — não Windows.Storage.Pickers
/// (WinRT), que exige identidade de pacote (MSIX) pra funcionar de forma confiável e falhava com
/// COMException 0x80004005 em várias máquinas mesmo com o HWND inicializado corretamente.</summary>
public class SqlFileService
{
    /// <summary>Handle nativo da janela principal, usado só pra deixar os diálogos modais (travam
    /// clique na janela do app até fechar) — os diálogos funcionam mesmo sem isso.</summary>
    private static nint GetOwnerHandle()
    {
        var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView
            as Microsoft.UI.Xaml.Window;
        return window is null ? 0 : WindowNative.GetWindowHandle(window);
    }

    /// <summary>Abre o diálogo "Salvar como" e grava o conteúdo. Retorna o caminho escolhido (pra
    /// salvamentos seguintes reaproveitarem via SaveToPathAsync sem abrir o diálogo de novo) ou null se
    /// o usuário cancelou o diálogo.</summary>
    public Task<string?> SaveAsAsync(string suggestedFileName, string content)
    {
        var path = NativeFileDialogs.PickSaveFile(GetOwnerHandle(), suggestedFileName);
        if (path is not null) File.WriteAllText(path, content);
        return Task.FromResult(path);
    }

    /// <summary>Sobrescreve direto um arquivo já salvo antes, sem diálogo — igual o "Salvar" (Ctrl+S) do
    /// DBeaver pra um script que já tem arquivo associado.</summary>
    public Task SaveToPathAsync(string path, string content) => File.WriteAllTextAsync(path, content);

    /// <summary>Abre o diálogo nativo de seleção de arquivo e lê o conteúdo de um script salvo antes.
    /// Retorna null se o usuário cancelou o diálogo.</summary>
    public Task<(string Path, string Content)?> OpenAsync()
    {
        var path = NativeFileDialogs.PickScriptFile(GetOwnerHandle());
        if (path is null) return Task.FromResult<(string, string)?>(null);

        var content = File.ReadAllText(path);
        return Task.FromResult<(string, string)?>((path, content));
    }

    /// <summary>Abre o diálogo nativo pra escolher um arquivo de banco SQLite existente. Retorna null se
    /// o usuário cancelou — pra criar um banco novo o usuário digita o caminho direto no campo.</summary>
    public Task<string?> PickSqliteFileAsync()
        => Task.FromResult(NativeFileDialogs.PickSqliteFile(GetOwnerHandle()));

    /// <summary>Abre o diálogo nativo de escolha de pasta — usado em Configurações pra trocar a pasta
    /// padrão de scripts, e em "Salvar como" pra escolher onde salvar uma cópia do script.</summary>
    public Task<string?> PickFolderAsync()
        => Task.FromResult(NativeFileDialogs.PickFolder(GetOwnerHandle()));
}
