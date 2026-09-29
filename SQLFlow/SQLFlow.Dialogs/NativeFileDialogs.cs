using System.Windows.Forms;

namespace SQLFlow.Dialogs;

/// <summary>Diálogos clássicos de arquivo/pasta do Windows (Shell IFileDialog via WinForms), usados no
/// lugar de Windows.Storage.Pickers (WinRT): os pickers WinRT exigem identidade de pacote (MSIX) pra
/// funcionar de forma confiável e falham com COMException 0x80004005 em apps sem pacote (como este,
/// WindowsPackageType=None) em várias máquinas — política de grupo, restrição de AppContainer etc.
/// Não depende de MAUI/WinUI — só recebe o handle da janela dona (nint) de quem chamar.</summary>
public static class NativeFileDialogs
{
    private sealed class Win32Window(nint handle) : IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    private static DialogResult Show(CommonDialog dialog, nint ownerHandle)
        => ownerHandle == 0 ? dialog.ShowDialog() : dialog.ShowDialog(new Win32Window(ownerHandle));

    /// <summary>Diálogo "Salvar como". Retorna o caminho escolhido, ou null se cancelado.</summary>
    public static string? PickSaveFile(nint ownerHandle, string suggestedFileName)
    {
        using var dialog = new SaveFileDialog
        {
            FileName = suggestedFileName,
            Filter = "Arquivo de texto (*.txt)|*.txt|Todos os arquivos (*.*)|*.*",
            AddExtension = true,
            DefaultExt = "txt"
        };

        return Show(dialog, ownerHandle) == DialogResult.OK ? dialog.FileName : null;
    }

    /// <summary>Diálogo de abrir um script (.sql/.txt). Retorna o caminho escolhido, ou null se cancelado.</summary>
    public static string? PickScriptFile(nint ownerHandle)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Scripts SQL (*.sql;*.txt)|*.sql;*.txt|Todos os arquivos (*.*)|*.*"
        };

        return Show(dialog, ownerHandle) == DialogResult.OK ? dialog.FileName : null;
    }

    /// <summary>Diálogo de abrir um arquivo de banco SQLite. Retorna o caminho escolhido, ou null se cancelado.</summary>
    public static string? PickSqliteFile(nint ownerHandle)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Banco SQLite (*.db;*.sqlite;*.sqlite3;*.db3)|*.db;*.sqlite;*.sqlite3;*.db3|Todos os arquivos (*.*)|*.*"
        };

        return Show(dialog, ownerHandle) == DialogResult.OK ? dialog.FileName : null;
    }

    /// <summary>Diálogo de escolha de pasta. Retorna o caminho escolhido, ou null se cancelado.</summary>
    public static string? PickFolder(nint ownerHandle)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Escolha a pasta",
            UseDescriptionForTitle = true
        };

        return Show(dialog, ownerHandle) == DialogResult.OK ? dialog.SelectedPath : null;
    }
}
