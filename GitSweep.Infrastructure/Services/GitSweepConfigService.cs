using System.Text.Json;
using GitSweep.Core.Models;
using GitSweep.Core.Services;

namespace GitSweep.Infrastructure.Services;

/// <summary>
/// Lee <c>.gitsweep.json</c> solo con <see cref="System.Text.Json"/> (sin YAML).
/// El fichero vive en un repo posiblemente clonado, así que es entrada no
/// confiable: tamaño acotado, sin comentarios ni comas finales, profundidad
/// acotada, patrones validados y ningún contenido del fichero en los errores
/// (podría contener secretos pegados por error).
/// </summary>
public sealed class GitSweepConfigService : IGitSweepConfigService
{
    internal const long MaxConfigBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };

    private readonly IGitRepositoryService? _gitService;

    public GitSweepConfigService() { }
    public GitSweepConfigService(IGitRepositoryService gitService) =>
        _gitService = gitService ?? throw new ArgumentNullException(nameof(gitService));

    public async Task<string> GetConfigFilePathAsync(string repoPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
        var root = repoPath;
        if (_gitService is not null)
        {
            try
            {
                var resolved = await _gitService.GetRepositoryRootAsync(repoPath, ct);
                if (!string.IsNullOrWhiteSpace(resolved)) root = resolved;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
            {
                // Sin raíz resoluble se usa la ruta dada; LoadAsync fallará
                // cerrado solo si allí hay un fichero inválido.
            }
        }
        return Path.GetFullPath(Path.Combine(root, GitSweepConfig.FileName));
    }

    public async Task<GitSweepConfig> LoadAsync(string repoPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
        var configPath = await GetConfigFilePathAsync(repoPath, ct);
        // Fallback: si la ruta dada no es la raíz (p. ej. tests con mocks sin
        // GetRepositoryRootAsync), también se mira junto a la ruta dada.
        if (!File.Exists(configPath))
        {
            var localPath = Path.GetFullPath(Path.Combine(repoPath, GitSweepConfig.FileName));
            if (!string.Equals(localPath, configPath, StringComparison.OrdinalIgnoreCase) && File.Exists(localPath))
                configPath = localPath;
            else
                return GitSweepConfig.Default;
        }

        var info = new FileInfo(configPath);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($".gitsweep.json at '{configPath}' is a symbolic link; refusing to read it.");
        if (info.Length > MaxConfigBytes)
            throw new InvalidOperationException($".gitsweep.json exceeds the {MaxConfigBytes} byte limit; refusing to read it.");

        GitSweepConfigDto? dto;
        try
        {
            await using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            dto = await JsonSerializer.DeserializeAsync<GitSweepConfigDto>(stream, JsonOptions, ct);
        }
        catch (JsonException ex)
        {
            // Sin filtrar contenido ni línea del fichero: solo el tipo de error.
            throw new InvalidOperationException($".gitsweep.json is not valid JSON ({ex.GetType().Name}); refusing to clean without verified protection.", ex);
        }

        if (dto is null)
            throw new InvalidOperationException(".gitsweep.json is empty; refusing to clean without verified protection.");

        var patterns = dto.ProtectedBranches ?? [];
        if (patterns.Count > GitSweepConfig.MaxProtectedPatterns)
            throw new InvalidOperationException($".gitsweep.json declares more than {GitSweepConfig.MaxProtectedPatterns} protected patterns; refusing to clean.");
        foreach (var pattern in patterns)
        {
            if (!BranchGlobMatcher.IsValidPattern(pattern))
                throw new InvalidOperationException($".gitsweep.json contains an invalid protected branch pattern; refusing to clean.");
        }

        return new GitSweepConfig(
            patterns.Select(p => p.Trim()).ToList(),
            dto.ExcludeCurrentBranch ?? true,
            dto.ExcludeWorktreeBranches ?? true);
    }

    private sealed class GitSweepConfigDto
    {
        public List<string>? ProtectedBranches { get; set; }
        public bool? ExcludeCurrentBranch { get; set; }
        public bool? ExcludeWorktreeBranches { get; set; }
    }
}
