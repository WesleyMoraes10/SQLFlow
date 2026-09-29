using System.Text.Json;

namespace SQLFlow.Services;

/// <summary>Preferências gerais do app, persistidas via Preferences.Default (chave-valor nativo do
/// MAUI) — diferente do ThemeService porque não precisa de JS interop (não mexe em atributo/CSS do
/// DOM, só num valor lido pelos componentes Razor).</summary>
public class AppSettingsService
{
    private const string ParallelResultsHorizontalKey = "sqlflow.parallel-results-horizontal";
    private const string FileConnectionsKey = "sqlflow.file-connections";
    private const string DefaultScriptsDirectoryKey = "sqlflow.default-scripts-directory";

    public bool ParallelResultsHorizontal { get; private set; } = Preferences.Default.Get(ParallelResultsHorizontalKey, true);

    /// <summary>Pasta onde "Salvar" grava um script novo direto, sem abrir diálogo — configurável em
    /// Configurações. "Salvar como" ignora isso e sempre abre o diálogo de escolha de pasta.</summary>
    public string DefaultScriptsDirectory { get; private set; } = Preferences.Default.Get(
        DefaultScriptsDirectoryKey,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ScriptSQLFlow"));

    // Caminho do arquivo (absoluto) -> Id do perfil de conexão usado da última vez com ele. Existe pra
    // reabrir um script salvo já direto na conexão certa, em vez de herdar a conexão da aba ativa no
    // momento — foi exatamente essa herança "silenciosa" que causava ORA-00942 (tabela existe só no
    // schema de outra conexão) quando a pessoa abria um script escrito pra outro banco.
    private readonly Dictionary<string, string> _fileConnections = LoadFileConnections();

    public event Action? Changed;

    public void SetParallelResultsHorizontal(bool horizontal)
    {
        if (ParallelResultsHorizontal == horizontal) return;

        ParallelResultsHorizontal = horizontal;
        Preferences.Default.Set(ParallelResultsHorizontalKey, horizontal);
        Changed?.Invoke();
    }

    public void SetDefaultScriptsDirectory(string path)
    {
        if (DefaultScriptsDirectory == path) return;

        DefaultScriptsDirectory = path;
        Preferences.Default.Set(DefaultScriptsDirectoryKey, path);
        Changed?.Invoke();
    }

    public string? GetConnectionForFile(string filePath) =>
        _fileConnections.TryGetValue(filePath, out var id) ? id : null;

    public void RememberConnectionForFile(string filePath, string connectionProfileId)
    {
        if (_fileConnections.TryGetValue(filePath, out var current) && current == connectionProfileId) return;

        _fileConnections[filePath] = connectionProfileId;
        Preferences.Default.Set(FileConnectionsKey, JsonSerializer.Serialize(_fileConnections));
    }

    private static Dictionary<string, string> LoadFileConnections()
    {
        var json = Preferences.Default.Get(FileConnectionsKey, string.Empty);
        if (string.IsNullOrEmpty(json)) return new Dictionary<string, string>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
