using System.Text.Json;
using System.Text.Json.Serialization;
using GitSweep.Core.Models;
using GitSweep.Core.Services;

namespace GitSweep.Infrastructure.Services;

/// <summary>
/// Lee <c>.gitsweep.json</c> solo con <see cref="System.Text.Json"/> (sin YAML).
/// El fichero vive en un repo posiblemente clonado, así que es entrada no
/// confiable: lectura acotada (tamaño máximo, nunca abre un fichero vacío o
/// una FIFO, ni sigue enlaces; ver <see cref="BoundedFileReader"/>), sin
/// comentarios ni comas finales, profundidad acotada, patrones validados y
/// ningún contenido del fichero en los errores
/// (podría contener secretos pegados por error). La lectura es estricta: una
/// propiedad desconocida, mal escrita (<c>protected_branches</c>) o duplicada
/// falla cerrado en vez de ignorarse, porque ignorarla dejaría al usuario sin la
/// protección que cree haber configurado. Una <c>protectedBranches</c> ausente,
/// nula o vacía es válida y significa «sin patrones extra»; la protección
/// integrada de <c>main</c>/<c>master</c> no depende del fichero.
/// </summary>
public sealed class GitSweepConfigService : IGitSweepConfigService
{
    internal const int MaxConfigBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
        // Estricto: propiedades desconocidas o duplicadas lanzan JsonException
        // (que LoadAsync convierte en InvalidOperationException sin contenido).
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
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

        // Lectura acotada: sin seguir enlaces, sin abrir longitud 0 (una FIFO
        // bloquearía open(2) para siempre) y sin leer más de límite + 1 bytes.
        // Los mensajes no repiten «refusing to clean»: clean ya lo antepone.
        var read = await BoundedFileReader.ReadAsync(configPath, MaxConfigBytes, ct);
        switch (read.Status)
        {
            case BoundedReadStatus.Link:
                throw new InvalidOperationException($".gitsweep.json at '{configPath}' is a symbolic link; refusing to read it.");
            case BoundedReadStatus.Empty:
                throw new InvalidOperationException(EmptyMessage);
            case BoundedReadStatus.TooLarge:
                throw new InvalidOperationException($".gitsweep.json exceeds the {MaxConfigBytes} byte limit; refusing to read it.");
            case BoundedReadStatus.Ok:
                break;
            default:
                throw new InvalidOperationException(".gitsweep.json could not be read.");
        }

        byte[] json;
        using (var content = read.Content!)
        {
            json = content.ToArray();
        }

        // Un BOM UTF-8 (editores de Windows) se acepta, como antes.
        ReadOnlySpan<byte> utf8 = json;
        if (utf8.StartsWith(Utf8Bom))
            utf8 = utf8[Utf8Bom.Length..];

        // I4: antes se informaba todo como «no es JSON válido», también un JSON
        // válido con una propiedad desconocida (`$schema`) o mal escrita. Se
        // distingue la categoría y se da la línea, pero nunca contenido del
        // fichero (ni siquiera el nombre de la propiedad, que podría ser un
        // secreto pegado por error), ni la excepción interna, que lo citaría.
        var structureError = FindStructureError(utf8);
        if (structureError is not null)
            throw new InvalidOperationException(structureError);

        GitSweepConfigDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<GitSweepConfigDto>(utf8, JsonOptions);
        }
        catch (JsonException ex)
        {
            // La sintaxis y los nombres ya se comprobaron: queda un valor del
            // tipo equivocado (p. ej. "protectedBranches": "release/*").
            throw new InvalidOperationException(
                $".gitsweep.json has a value of the wrong type{FormatLocation(ex.LineNumber, ex.BytePositionInLine)}: " +
                "protectedBranches must be an array of strings (or null), excludeCurrentBranch and excludeWorktreeBranches must be true or false.");
        }

        if (dto is null)
            throw new InvalidOperationException(EmptyMessage);

        // Ausente, nula o vacía = sin patrones extra (los integrados siempre aplican).
        var patterns = dto.ProtectedBranches ?? [];
        if (patterns.Count > GitSweepConfig.MaxProtectedPatterns)
            throw new InvalidOperationException($".gitsweep.json declares more than {GitSweepConfig.MaxProtectedPatterns} protected patterns.");
        for (var index = 0; index < patterns.Count; index++)
        {
            if (!BranchGlobMatcher.IsValidPattern(patterns[index]))
                throw new InvalidOperationException(
                    $".gitsweep.json contains an invalid protected branch pattern (entry {index + 1} of protectedBranches; patterns cannot be empty or contain whitespace, control characters, ~ ^ : [ ] \\, .. or @{{).");
        }

        return new GitSweepConfig(
            patterns.Select(p => p.Trim()).ToList(),
            dto.ExcludeCurrentBranch ?? true,
            dto.ExcludeWorktreeBranches ?? true);
    }

    private const string EmptyMessage = ".gitsweep.json is empty; it must contain a JSON object such as {}.";

    private const string AllowedPropertiesText = "protectedBranches, excludeCurrentBranch and excludeWorktreeBranches";

    // Nombres admitidos, sin distinguir mayúsculas (como el deserializador).
    private static readonly HashSet<string> AllowedProperties =
        new(["protectedBranches", "excludeCurrentBranch", "excludeWorktreeBranches"], StringComparer.OrdinalIgnoreCase);

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Comprueba la sintaxis y las propiedades de primer nivel y devuelve el
    /// mensaje (en inglés, con categoría y línea, sin contenido del fichero)
    /// del primer problema, o <c>null</c> si no hay ninguno. Las mismas reglas
    /// que el deserializador estricto: sin comentarios ni comas finales,
    /// profundidad acotada, solo las tres propiedades conocidas y cada una
    /// una sola vez (las variantes de mayúsculas cuentan como repetición).
    /// </summary>
    private static string? FindStructureError(ReadOnlySpan<byte> utf8)
    {
        var options = new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        };

        // Una sola envoltura: cualquier JsonException (sintaxis, profundidad,
        // o UTF-8 inválido al leer un nombre de propiedad, RR-8) se traduce a
        // la categoría «no es JSON válido», con la línea si la hay y sin texto
        // del framework.
        try
        {
            // 1. Sintaxis de todo el documento.
            var syntax = new Utf8JsonReader(utf8, options);
            while (syntax.Read())
            {
            }

            // 2. Raíz y nombres de propiedad de primer nivel.
            var reader = new Utf8JsonReader(utf8, options);
            reader.Read();
            if (reader.TokenType == JsonTokenType.Null)
                return EmptyMessage;
            if (reader.TokenType != JsonTokenType.StartObject)
                return ".gitsweep.json must contain a JSON object, for example {\"protectedBranches\": []}.";

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                // Posición antes de transcodificar: si el nombre lleva UTF-8
                // inválido, GetString lanza InvalidOperationException (no
                // JsonException; M-1/RR-8) y se mapea a «no es JSON válido» con
                // la línea, sin el texto del framework.
                var location = FormatLocation(utf8, reader.TokenStartIndex);
                string name;
                try
                {
                    name = reader.GetString() ?? string.Empty;
                }
                catch (InvalidOperationException)
                {
                    return $".gitsweep.json is not valid JSON{location}; the file must be strict JSON (no comments or trailing commas).";
                }

                if (!AllowedProperties.Contains(name))
                    return $".gitsweep.json has an unknown property{location}; the allowed properties are {AllowedPropertiesText}.";
                if (!seen.Add(name))
                    return $".gitsweep.json repeats a property{location}; each property may appear only once (names that differ only in case count as the same property).";
                reader.Skip();
            }

            return null;
        }
        catch (JsonException ex)
        {
            return $".gitsweep.json is not valid JSON{FormatLocation(ex.LineNumber, ex.BytePositionInLine)}; the file must be strict JSON (no comments or trailing commas).";
        }
    }

    // Posición de un error del lector (base 0) como « at line N, position M».
    private static string FormatLocation(long? lineNumber, long? bytePositionInLine) =>
        lineNumber is null
            ? string.Empty
            : $" at line {lineNumber.Value + 1}" + (bytePositionInLine is null ? string.Empty : $", position {bytePositionInLine.Value + 1}");

    // Línea (base 1) del byte en tokenStart, contando saltos de línea.
    private static string FormatLocation(ReadOnlySpan<byte> utf8, long tokenStart)
    {
        var before = utf8[..(int)Math.Min(tokenStart, utf8.Length)];
        return $" at line {before.Count((byte)'\n') + 1}";
    }

    private sealed class GitSweepConfigDto
    {
        public List<string>? ProtectedBranches { get; set; }
        public bool? ExcludeCurrentBranch { get; set; }
        public bool? ExcludeWorktreeBranches { get; set; }
    }
}
