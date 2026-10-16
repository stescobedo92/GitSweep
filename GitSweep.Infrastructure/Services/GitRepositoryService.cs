using GitSweep.Core.Models;
using GitSweep.Core.Services;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GitSweep.Infrastructure.Services;

/// <summary>
/// Acceso a git mediante un único ejecutor endurecido (sin shell, lista de
/// argumentos, entorno saneado). Cada invocación tiene un plazo y topes de
/// salida; cuando se superan, el árbol de procesos de git se mata y el
/// resultado es un fallo, nunca una salida truncada.
/// </summary>
/// <remarks>
/// Un fallo por límite nunca se interpreta como respuesta de git: los
/// métodos que informan de un hecho del repositorio o que borran una rama
/// lanzan <see cref="GitCommandLimitException"/>; los que ya devuelven un
/// valor conservador ante cualquier error de git (detección de merge,
/// señales de riesgo, <c>fetch</c>, creación de rama) lo devuelven también
/// en este caso («no mergeada», conteo desconocido, fallo).
/// Ningún comando se ejecuta contra un clon parcial: el ejecutor compartido
/// lo rechaza antes con <see cref="PartialCloneNotSupportedException"/>,
/// porque en un clon parcial leer un objeto ausente ejecuta el
/// <c>uploadpack</c> configurado por el repositorio (ver
/// <see cref="RunRepositoryGitCommandAsync"/>).
/// </remarks>
public class GitRepositoryService : IGitRepositoryService
{
    /// <summary>
    /// Plazo de cada comando git local. La plomería que usa GitSweep termina
    /// en milisegundos incluso en repositorios grandes; dos minutos solo se
    /// agotan si git queda bloqueado (un lock con espera configurada por el
    /// repositorio, un sistema de archivos colgado...).
    /// </summary>
    internal static readonly TimeSpan LocalCommandTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Plazo de <c>git fetch</c>, la única operación de red: un origin hostil
    /// que acepta la conexión TCP y luego no responde colgaba
    /// <c>clean --fetch</c> para siempre.
    /// </summary>
    internal static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Tope de caracteres acumulados de stdout por invocación: holgado para
    /// listas de ramas de repositorios enormes, pero acotado para que una
    /// salida sin fin no agote la memoria.
    /// </summary>
    internal const int MaxStandardOutputChars = 64 * 1024 * 1024;

    /// <summary>
    /// Tope de caracteres acumulados de stderr por invocación: un remoto
    /// hostil puede inundar stderr con texto <c>remote:</c> durante el fetch.
    /// </summary>
    internal const int MaxStandardErrorChars = 1024 * 1024;

    /// <summary>
    /// Código de salida sintético de una invocación cuyo plazo venció. Es
    /// negativo, así que no coincide con ningún código de git (0, el 1 de
    /// <c>merge-base --is-ancestor</c>/<c>show-ref --verify</c>, 128, 129):
    /// ningún llamador puede leerlo como «sí» ni como un «no» concreto.
    /// </summary>
    internal const int TimedOutExitCode = -2;

    /// <summary>Código de salida sintético de una invocación que superó un tope de salida.</summary>
    internal const int OutputLimitExceededExitCode = -3;

    // Espera máxima a que el árbol de procesos muerto termine de salir, para
    // no dejar a git con locks del repositorio mientras corre el siguiente
    // comando. Matar es inmediato; esto solo acota un caso patológico.
    private static readonly TimeSpan KillWaitTimeout = TimeSpan.FromSeconds(5);

    private const string SyntheticCommitMessage = "GitSweep squash-merge detection";

    // Identity used only so `git commit-tree` works in repositories without a
    // configured user. It never appears in refs, logs or output: the synthetic
    // commit is left dangling for git's garbage collector.
    private static readonly IReadOnlyDictionary<string, string> SyntheticCommitIdentity =
        new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = "GitSweep",
            ["GIT_AUTHOR_EMAIL"] = "gitsweep@localhost",
            ["GIT_COMMITTER_NAME"] = "GitSweep",
            ["GIT_COMMITTER_EMAIL"] = "gitsweep@localhost"
        };

    // Hooks directory that can never contain an executable hook: the null
    // device on Windows, /dev/null elsewhere. Pointing core.hooksPath at a
    // file (or nonexistent path) makes git look for "<path>/<hook>", which
    // never exists, so no hook is ever run. The same device is the (always
    // empty) graft file, see ApplyHardenedEnvironment.
    private static readonly string NullDevicePath =
        OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    // Configuration forced on every git invocation. Command-line `-c` values
    // override repository config, so a hostile cloned repository cannot turn
    // a read-only scan into code execution or an interactive hang through:
    //  - core.fsmonitor: a repo-configured fsmonitor helper program,
    //  - core.pager: a configured pager program (output is piped anyway),
    //  - commit.gpgsign: a repo-configured signing program invoked while
    //    creating the synthetic squash-detection commit,
    //  - gc.auto / maintenance.auto: auto-gc and auto-maintenance run the
    //    repo's `pre-auto-gc` hook, i.e. code shipped inside a cloned repo,
    //  - core.hooksPath: ref updates (branch create/delete in clean,
    //    restore and undo) fire the repo's `reference-transaction` hook,
    //    i.e. code shipped inside a cloned repo, so hooks are pointed at a
    //    location that can never hold an executable hook,
    //  - transport helpers reachable through `fetch --prune`: a cloned repo
    //    can name an arbitrary local program in `core.sshCommand`,
    //    `core.gitProxy`, `core.askPass` or `credential.helper`, the `ext::`
    //    transport runs a shell command by design, and each of those is
    //    executed locally as soon as git talks to the remote. SSH is pinned
    //    to the plain `ssh` binary (the user's ~/.ssh config still applies),
    //    the helper slots are emptied so no config-chosen program runs, and
    //    `ext` is refused outright; with GIT_TERMINAL_PROMPT=0 a fetch that
    //    would need interactive credentials then fails closed instead of
    //    executing code or prompting. The protocol allowlist below refuses
    //    every transport that is not a standard one: without it, a remote
    //    URL such as `foo::bar` (or any `<scheme>::`/`scheme://` URL whose
    //    scheme is not built in) makes git execute a `git-remote-<scheme>`
    //    program found on PATH, so repository config alone could pick a
    //    local program to run. Only https/http/ssh/git/file/local remain.
    //  - sideband.allowControlCharacters=false (SEC-B02): un git reciente (la
    //    clave existe en 2.55, no en 2.43) enmascara por defecto los
    //    caracteres de control que el remoto manda por la banda lateral (el
    //    texto `remote:` de `clean --fetch`) salvo los colores ANSI, y la
    //    config del repositorio puede reactivarlos todos con `true`. Aquí se
    //    fija el valor más estricto (solo pasan LF y TAB). Es solo defensa en
    //    profundidad: una clave `sideband.<url>.allowControlCharacters` del
    //    repositorio tiene prioridad sobre esta genérica (verificado con
    //    2.55). El control real es que el stderr de git nunca llega directo
    //    al terminal: se captura y la CLI lo sanea (TerminalText) antes de
    //    mostrarlo. Un git que no conoce la clave la ignora (verificado con
    //    2.43).
    //  - promisor.acceptFromServer=None (OBS-R2): en git >= 2.49 la config del
    //    repositorio puede aceptar los remotos promisor que anuncia el servidor
    //    y, con promisor.storeFields (>= 2.52), dejar que el servidor escriba
    //    `remote.<n>.partialCloneFilter` durante el fetch, convirtiendo el
    //    repositorio en un clon parcial a mitad de ejecución. "None" (el valor
    //    por defecto de git) no acepta ninguno. Inofensivo en git sin la clave
    //    (verificado con 2.43).
    //  - hook.<evento>.enabled=false (P1-F1): SOLO defensa en profundidad, no
    //    la garantía. git >= 2.54 admite hooks definidos en la config
    //    (`hook.<nombre>.command` + `.event`), que core.hooksPath NO gobierna
    //    (solo mueve el directorio de hooks). Cada ref que GitSweep actualiza
    //    (branch -d, branch al restaurar, update-ref de las keep-refs, las
    //    refs remotas del fetch) dispara reference-transaction, y el
    //    mantenimiento tras un fetch dispara pre-auto-gc. La clave por evento
    //    NO basta: en git 2.54.x no existe (solo hay hook.<nombre>.enabled por
    //    nombre amistoso), y en 2.55 un `hook.reference-transaction.<var>` del
    //    repositorio hace que git lea esta clave como la de un hook con ese
    //    nombre amistoso y el hook real sigue corriendo (RG-01/RR-1). La
    //    garantía real es EnsureNoDangerousConfiguredHooksAsync: rechaza (falla
    //    cerrado) cualquier repositorio cuya config defina un hook para un
    //    evento que GitSweep pueda disparar, antes de tocar nada. La guarda ya
    //    cubre el caso; esta clave se conserva solo como red de seguridad por
    //    si la config cambiara después de la comprobación (su efecto ya no es
    //    la garantía).
    //  - core.alternateRefsCommand= (P1b-03, DOC-V1): con un
    //    objects/info/alternates en el repositorio, `fetch` ejecuta por el shell
    //    el valor configurado para listar las refs de cada alternate. Vacío no
    //    ejecuta nada (git intenta lanzar "" y sigue sin esas refs; verificado
    //    con 2.55 y 2.43).
    //  - fetch.bundleURI= (P1b-05): antes del fetch, git descargaría o copiaría
    //    el bundle que nombra el repositorio y escribiría refs/bundles/* (y
    //    config) fuera del refspec. Vacío no descarga nada.
    //  - http.saveCookies=false (P1b-04): con http.cookieFile, curl escribiría
    //    las cookies del servidor en la ruta que elige el repositorio. Esta
    //    clave genérica pierde frente a una `http.<url>.saveCookies` del
    //    repositorio (gana la URL más específica), así que FetchPruneAsync
    //    además rechaza el fetch si alguna de esas claves vale true.
    //  - core.useReplaceRefs=false y GIT_GRAFT_FILE (I5): refs/replace/* e
    //    info/grafts cambian los padres que git ve, así que un repositorio
    //    hostil podía hacer pasar por mergeada (y por contenida en otra ref)
    //    una rama con commits únicos: `branch --merged`, `merge-base`,
    //    `for-each-ref --contains` y el propio `branch -d` lo creían
    //    (verificado con 2.55 y 2.43). Sin ellos git lee los padres reales.
    //    advice.graftFileDeprecated=false calla el aviso que git imprime al
    //    abrir el fichero de grafts (el dispositivo nulo, ver
    //    ApplyHardenedEnvironment) y que si no ensuciaría cada stderr.
    private static readonly string[] HardenedGitOptions =
    [
        "--no-pager",
        "-c", "core.fsmonitor=false",
        "-c", "core.pager=cat",
        "-c", "commit.gpgsign=false",
        "-c", "gc.auto=0",
        "-c", "maintenance.auto=false",
        "-c", $"core.hooksPath={NullDevicePath}",
        "-c", "hook.reference-transaction.enabled=false",
        "-c", "hook.pre-auto-gc.enabled=false",
        "-c", "protocol.allow=never",
        "-c", "protocol.https.allow=always",
        "-c", "protocol.http.allow=always",
        "-c", "protocol.ssh.allow=always",
        "-c", "protocol.git.allow=always",
        "-c", "protocol.file.allow=always",
        "-c", "protocol.local.allow=always",
        "-c", "protocol.ext.allow=never",
        "-c", "core.sshCommand=ssh",
        "-c", "core.gitProxy=",
        "-c", "core.askPass=",
        "-c", "credential.helper=",
        "-c", "core.alternateRefsCommand=",
        "-c", "fetch.bundleURI=",
        "-c", "http.saveCookies=false",
        "-c", "core.useReplaceRefs=false",
        "-c", "advice.graftFileDeprecated=false",
        "-c", "sideband.allowControlCharacters=false",
        "-c", "promisor.acceptFromServer=None"
    ];

    // Lista de transportes permitidos, la misma que fijan las claves
    // protocol.* de arriba, en el formato de GIT_ALLOW_PROTOCOL.
    private const string AllowedProtocols = "https:http:ssh:git:file:local";

    // Ambient git environment variables removed from every child process so
    // results depend only on the repository path the user selected, never on
    // a GIT_DIR/GIT_WORK_TREE inherited from the caller's environment (which
    // could silently redirect even branch deletion to another repository).
    // GIT_CONFIG_* entries are removed by prefix in ExecuteGitCommandAsync:
    // they inject configuration at command-line precedence (GIT_CONFIG_COUNT/
    // KEY/VALUE, GIT_CONFIG_PARAMETERS) or redirect the global/system config
    // files (GIT_CONFIG_GLOBAL/SYSTEM), either of which could smuggle in a
    // helper program such as core.fsmonitor or a per-command pager.* that
    // `-c core.pager` does not cover. Transport/askpass/diff helpers are
    // removed for the same reason even though no command here performs
    // transport or an external diff: fail closed if one ever does.
    // GIT_EXEC_PATH would make git load its sub-programs (remote helpers,
    // upload-pack, ...) from an arbitrary directory. GIT_PROXY_COMMAND,
    // GIT_ALLOW_PROTOCOL and GIT_GRAFT_FILE are not removed but overwritten
    // in ApplyHardenedEnvironment: their pinned values are what neutralizes
    // the matching repository config.
    private static readonly string[] RemovedGitEnvironmentVariables =
    [
        "GIT_DIR",
        "GIT_COMMON_DIR",
        "GIT_WORK_TREE",
        "GIT_INDEX_FILE",
        "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_NAMESPACE",
        "GIT_QUARANTINE_PATH",
        "GIT_EXEC_PATH",
        "GIT_SSH",
        "GIT_SSH_COMMAND",
        "GIT_ASKPASS",
        "SSH_ASKPASS",
        "GIT_EXTERNAL_DIFF",
        "GIT_PROTOCOL_FROM_USER"
    ];

    private readonly GitCommandLimits _limits;

    public GitRepositoryService()
        : this(GitCommandLimits.Default)
    {
    }

    /// <summary>Solo para pruebas: límites diminutos sin esperar los plazos reales.</summary>
    internal GitRepositoryService(GitCommandLimits limits)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public async Task<bool> IsGitRepositoryAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        // Exento de la guarda de clon parcial (como la propia detección):
        // `rev-parse --git-dir` solo localiza el repositorio y nunca lee
        // objetos, así que no puede disparar un lazy fetch. El siguiente
        // comando sobre un clon parcial es el que lo rechaza.
        var result = await ExecuteObjectFreeGitCommandAsync(path, ["rev-parse", "--git-dir"], ct);
        ThrowIfLimitFailure(result);
        return result.ExitCode == 0;
    }

    public async Task<string> GetDefaultBranchNameAsync(string path, CancellationToken ct = default)
    {
        // Un fallo por límite nunca activa los respaldos de abajo (HEAD local,
        // "main"): la rama destino se excluye de las candidatas, así que
        // adivinarla podría ofrecer para borrado la rama por defecto real.

        // First, try to get the remote default branch
        var remoteResult = await ExecuteGitCommandAsync(path, ["symbolic-ref", "refs/remotes/origin/HEAD"], ct);
        ThrowIfLimitFailure(remoteResult);
        if (remoteResult.ExitCode == 0)
        {
            return remoteResult.Output.Replace("refs/remotes/origin/", string.Empty).Trim();
        }

        // Fallback to local HEAD
        var localResult = await ExecuteGitCommandAsync(path, ["symbolic-ref", "--short", "HEAD"], ct);
        ThrowIfLimitFailure(localResult);
        return localResult.ExitCode == 0 ? localResult.Output.Trim() : "main";
    }

    public async Task<List<BranchInfo>> GetLocalBranchesAsync(string path, string targetBranch, CancellationToken ct = default)
    {
        if (!IsValidBranchName(targetBranch))
        {
            throw new ArgumentException("Target branch name is invalid.", nameof(targetBranch));
        }

        var branches = new List<BranchInfo>();

        // Nunca candidatas: la rama actual ni las que están en worktrees
        // (git rechazaría borrarlas, pero excluirlas aquí evita ofrecerlas).
        var currentBranch = await GetCurrentBranchAsync(path, ct);
        var worktreeBranches = await GetWorktreeBranchesAsync(path, ct);
        var neverCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(currentBranch)) neverCandidates.Add(currentBranch);
        foreach (var worktreeBranch in worktreeBranches) neverCandidates.Add(worktreeBranch);

        // Get merged branches. The target is qualified as refs/heads/<name>:
        // a bare name would resolve a same-named tag before the branch
        // (gitrevisions order), which could misreport what is merged.
        // Un fallo por límite lanza en vez de seguir con una lista vacía:
        // «sin ramas»/«ninguna mergeada» sería un hecho falso.
        var mergedResult = await ExecuteGitCommandAsync(path, ["branch", "--merged", $"refs/heads/{targetBranch}"], ct);
        ThrowIfLimitFailure(mergedResult);
        var mergedBranchNames = ParseBranchList(mergedResult.Output);

        // Get all local branches with their last commit date.
        // %(refname:strip=2) always yields the plain branch name; the more
        // common %(refname:short) would return "heads/<name>" when a tag
        // shares the name, which would then fail to resolve as a branch.
        // %(upstream:track) is "[gone]" when the upstream is configured but
        // its remote-tracking ref is gone (after `git fetch --prune` removed
        // it); %(upstream:short) is empty when there is no upstream at all.
        // Fields are TAB-separated: a branch or upstream name may legally
        // contain '|' (git ref format allows it), and a printable delimiter
        // inside a name would let one field forge another (for example a
        // fake "[gone]" track). Ref names can never contain control
        // characters, so TAB cannot be smuggled in through a name.
        var allBranchesResult = await ExecuteGitCommandAsync(
            path,
            ["for-each-ref", "--sort=-committerdate", "--format=%(refname:strip=2)\t%(committerdate:iso8601)\t%(upstream:short)\t%(upstream:track)", "refs/heads/"],
            ct
        );
        ThrowIfLimitFailure(allBranchesResult);

        foreach (var line in allBranchesResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 2) continue;

            var name = parts[0];
            if (!IsValidBranchName(name)) continue;

            var date = DateTime.TryParse(parts[1], out var commitDate) ? commitDate : (DateTime?)null;
            var upstreamName = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;
            var upstreamTrack = parts.Length > 3 ? parts[3] : string.Empty;
            var isUpstreamGone = upstreamName is not null && IsGoneTrack(upstreamTrack);

            // Skip the target branch itself
            if (name.Equals(targetBranch, StringComparison.OrdinalIgnoreCase)) continue;
            if (neverCandidates.Contains(name)) continue;

            if (mergedBranchNames.Contains(name))
            {
                branches.Add(new BranchInfo(name, date, true, BranchMergeKind.Ancestor, upstreamName, isUpstreamGone));
                continue;
            }

            // `git branch --merged` misses squash/rebase merges because the
            // branch tip never becomes an ancestor of the target. Detect
            // those locally (merge-base + commit-tree / cherry).
            var isSquashOrRebaseMerged = await IsSquashOrRebaseMergedAsync(path, targetBranch, name, ct);
            branches.Add(new BranchInfo(
                name,
                date,
                false,
                isSquashOrRebaseMerged ? BranchMergeKind.SquashOrRebase : BranchMergeKind.NotMerged,
                upstreamName,
                isUpstreamGone));
        }

        return branches;
    }

    public async Task<(bool Success, string ErrorMessage)> DeleteBranchAsync(string path, string branchName, CancellationToken ct = default)
    {
        if (!IsValidBranchName(branchName))
        {
            return (false, $"Invalid branch name: '{branchName}'.");
        }

        // Defensa en profundidad: nunca borrar la rama actual ni una rama en
        // un worktree, aunque el llamador no haya filtrado las candidatas.
        var currentBranch = await GetCurrentBranchAsync(path, ct);
        if (!string.IsNullOrWhiteSpace(currentBranch) &&
            string.Equals(currentBranch, branchName, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Branch '{branchName}' is the current branch and cannot be deleted.");
        }

        var worktreeBranches = await GetWorktreeBranchesAsync(path, ct);
        if (worktreeBranches.Any(b => string.Equals(b, branchName, StringComparison.OrdinalIgnoreCase)))
        {
            return (false, $"Branch '{branchName}' is checked out in a worktree and cannot be deleted.");
        }

        // `git branch -d` no basta para no perder trabajo: acepta una rama
        // «totalmente mergeada en su upstream», y el upstream sale de la
        // configuración del repositorio. Un .git/config hostil con
        // `branch.<rama>.remote = .` y `branch.<rama>.merge = refs/heads/<rama>`
        // convierte a la rama en su propio upstream, así que `-d` la borra
        // aunque tenga commits únicos sin subir. Tras el borrado esos commits
        // solo constan en la entrada JSON de la papelera (el reflog se va con
        // la rama), el siguiente `git gc` puede podarlos y `undo` ya no puede
        // restaurarla. Por eso, antes de `-d`, se exige de forma independiente
        // de la configuración que otra ref (rama local, rama remota de
        // seguimiento o tag) contenga la punta: si ninguna la contiene, o si
        // git no da una respuesta válida, la rama no se borra. Después de
        // `-d` se repite la comprobación sobre el estado real (ver abajo).
        // Punta resuelta siempre como refs/heads/<rama>^{commit} (nunca un
        // tag homónimo) y validada como SHA hexadecimal completo; un fallo
        // por límite lanza dentro de GetBranchTipShaAsync.
        var tipSha = await GetBranchTipShaAsync(path, branchName, ct);
        if (tipSha is null)
        {
            return (false, ReachabilityUnverifiedMessage(branchName));
        }

        var refusal = await GetPreDeleteRefusalAsync(path, branchName, tipSha, ct);
        if (refusal is not null)
        {
            return (false, refusal);
        }

        // Safe delete only: never force-delete with -D. Both checks must
        // pass: the tip is reachable from another ref (above) and git itself
        // considers the branch merged; a branch git refuses is reported to
        // the caller instead.
        var result = await ExecuteGitCommandAsync(path, ["branch", "-d", "--", branchName], ct);

        // Un fallo por límite deja el resultado indeterminado, no «no se
        // borró»: `-d` escribe sus avisos de stderr antes de borrar la ref y
        // el «Deleted branch» de stdout después, así que el plazo o el tope
        // pueden saltar antes, durante o después del borrado mientras se
        // mata el árbol. Devolver false haría que clean revirtiera la entrada
        // de la papelera de una rama que quizá ya no existe; se lanza para
        // que la entrada se conserve.
        ThrowIfLimitFailure(result);
        var message = GitOutputRedactor.Redact(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
        if (result.ExitCode != 0)
        {
            return (false, message);
        }

        // La comprobación previa no basta por sí sola: un alias del propio
        // fichero de la ref en el sistema de archivos (un junction o enlace
        // simbólico a un directorio bajo refs/remotes/ o refs/tags/, o un
        // enlace a fichero cuyo destino no empieza por "refs/") no es una ref
        // simbólica para git: for-each-ref lo lista como una ref normal, con
        // %(symref) vacío, que «contiene» siempre la punta actual de la rama.
        // Al borrar el fichero de la rama, el alias desaparece con él. Por
        // eso, ya borrada, se vuelve a preguntar qué refs contienen el SHA
        // resuelto antes del borrado: el alias ya no existe, así que la
        // respuesta refleja el estado real sin depender de cómo esté montado
        // .git/refs. Si ninguna otra ref lo contiene, la rama se vuelve a
        // crear en esa punta y el borrado se informa como rechazado.
        return await IsTipStillContainedAfterDeleteAsync(path, branchName, tipSha)
            ? (true, message)
            : (false, UnreachableTipMessage(branchName));
    }

    private static string UnreachableTipMessage(string branchName) =>
        $"Branch '{branchName}' has commits that are not reachable from any other branch, remote-tracking branch or tag; it was not deleted.";

    private static string ReachabilityUnverifiedMessage(string branchName) =>
        $"Could not verify that branch '{branchName}' is reachable from another branch, remote-tracking branch or tag; it was not deleted.";

    private static string NotRecreatableMessage(string branchName) =>
        $"Branch '{branchName}' cannot be recreated by git under this name, so a deletion could not be undone if it left commits unreachable; it was not deleted.";

    private static string RecreationUnverifiedMessage(string branchName) =>
        $"Could not verify that branch '{branchName}' could be recreated by git if its deletion had to be undone; it was not deleted.";

    private static string CaseVariantMessage(string branchName) =>
        $"Another branch whose name differs from '{branchName}' only in letter case exists, so git could not reliably recreate '{branchName}' if its deletion had to be undone; it was not deleted.";

    /// <summary>
    /// Comprobaciones previas al borrado, independientes de la configuración
    /// del repositorio. Devuelve <c>null</c> si la rama puede pasar a
    /// <c>git branch -d</c> y, si no, el mensaje (en inglés) con el que
    /// <see cref="DeleteBranchAsync"/> la rechaza sin ejecutarlo.
    /// </summary>
    /// <remarks>
    /// Exige que git pueda volver a crear la rama con ese nombre (la
    /// comprobación posterior al borrado la recrea si hace falta) y que otra
    /// ref real contenga su punta. Un fallo de git o una salida inesperada
    /// también rechaza el borrado. Un fallo por límite lanza
    /// <see cref="GitCommandLimitException"/>, igual que los guardas de rama
    /// actual y worktree que lo preceden: git no respondió y el borrado no
    /// llega a intentarse, así que clean se detiene sin borrar nada más.
    /// </remarks>
    private async Task<string?> GetPreDeleteRefusalAsync(string path, string branchName, string tipSha, CancellationToken ct)
    {
        // Si el borrado hubiera que deshacerlo, `git branch -- <rama> <sha>`
        // debe poder crearla de nuevo. Un repositorio hostil puede traer una
        // rama que git borra pero ya no deja crear: un nombre reservado
        // (refs/heads/HEAD, que git rechaza como nombre de rama nueva) o un
        // conflicto directorio/fichero con otra ref (abajo). Una rama así no
        // se borra: su borrado no se podría deshacer. Los cerrojos
        // abandonados (refs/heads/<rama>.lock, packed-refs.lock) no hacen
        // falta aquí: con ellos ya falla el propio `-d`, y config.lock no
        // impide recrearla. El nombre ya validado nunca empieza por '-', así
        // que no puede leerse como opción.
        // O-3/RR-6: en un FS sensible a mayúsculas donde conviven `Feature` y
        // `feature`, recrear `feature` tras `-d` lo rechaza CreateBranchAsync
        // (variante de caja), así que la recreación posterior fallaría y el
        // borrado acabaría en BranchDeletionVerificationException. Se rechaza
        // aquí, antes del `-d`, con el mensaje propio de este contexto.
        switch (await ExistsBranchDifferingOnlyInCaseAsync(path, branchName, ct))
        {
            case null:
                return RecreationUnverifiedMessage(branchName);
            case true:
                return CaseVariantMessage(branchName);
        }

        var nameCheck = await ExecuteGitCommandAsync(path, ["check-ref-format", "--branch", branchName], ct);
        ThrowIfLimitFailure(nameCheck);
        if (nameCheck.ExitCode != 0)
        {
            return NotRecreatableMessage(branchName);
        }

        // Conflicto directorio/fichero, en las dos direcciones que comprueba
        // git al crear una ref:
        //  1. Cada prefijo propio de refs/heads/<rama>, empezando por "refs" y
        //     "refs/heads", buscado por su nombre exacto. Un packed-refs hostil
        //     puede nombrar cualquiera de ellos (git nunca escribe esas refs y
        //     aun así deja trabajar en las ramas existentes); buscarlos por
        //     nombre evita recorrer todas las refs en cada borrado.
        //     `rev-parse --verify --symbolic-full-name` ve también refs cuyo
        //     objeto no existe. Como aplica las reglas de abreviatura, solo
        //     cuenta como ausente un 1 (no existe) o un nombre completo
        //     distinto (resolvió otra ref, p. ej. la rama refs/heads/refs). Si
        //     imprime el propio prefijo, existe; si sale con 0 sin imprimir
        //     nada, el nombre es ambiguo (el prefijo exacto más otra ref
        //     homónima), y eso tampoco es «ausente». Los prefijos empiezan
        //     por "refs", así que no pueden leerse como opción (sin
        //     --end-of-options, que rev-parse repetiría en la salida).
        //  2. Las refs bajo refs/heads/<rama>/, listadas. for-each-ref omite
        //     (solo avisa en stderr) una ref con nombre inválido, que para git
        //     sigue ocupando el sitio: un aviso que nombra una ref bajo ese
        //     prefijo también rechaza el borrado.
        // Sin distinguir mayúsculas: solo puede añadir rechazos, nunca
        // quitarlos. El nombre validado no tiene caracteres glob, así que el
        // patrón del listado es un prefijo literal.
        var ownRef = $"refs/heads/{branchName}";
        var components = ownRef.Split('/');
        for (var length = 1; length < components.Length; length++)
        {
            var prefix = string.Join('/', components, 0, length);
            var lookup = await ExecuteGitCommandAsync(
                path, ["rev-parse", "--verify", "--quiet", "--symbolic-full-name", prefix], ct);
            ThrowIfLimitFailure(lookup);
            var resolved = lookup.Output.Trim();
            if (lookup.ExitCode == 1 ||
                (lookup.ExitCode == 0 && resolved.Length > 0 &&
                 !string.Equals(resolved, prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return lookup.ExitCode == 0 && resolved.Length > 0
                ? NotRecreatableMessage(branchName)
                : RecreationUnverifiedMessage(branchName);
        }

        var descendants = await ExecuteGitCommandAsync(
            path, ["for-each-ref", "--format=%(refname)", ownRef + "/"], ct);
        ThrowIfLimitFailure(descendants);
        if (descendants.ExitCode != 0)
        {
            return RecreationUnverifiedMessage(branchName);
        }

        var listedDescendants = descendants.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (listedDescendants.Length > 0)
        {
            return listedDescendants.All(line => line.TrimEnd('\r').StartsWith(ownRef + "/", StringComparison.OrdinalIgnoreCase))
                ? NotRecreatableMessage(branchName)
                : RecreationUnverifiedMessage(branchName);
        }

        if (descendants.Error.Contains(ownRef + "/", StringComparison.OrdinalIgnoreCase))
        {
            return NotRecreatableMessage(branchName);
        }

        return await IsTipContainedInAnotherRefAsync(path, branchName, tipSha, ct) switch
        {
            true => null,
            false => UnreachableTipMessage(branchName),
            null => ReachabilityUnverifiedMessage(branchName)
        };
    }

    /// <summary>
    /// Pregunta qué refs reales (ramas locales, ramas remotas de seguimiento,
    /// tags) contienen <paramref name="tipSha"/>, aparte de
    /// <c>refs/heads/&lt;rama&gt;</c> y de las keep-refs de GitSweep y sus
    /// alias. <c>true</c>: al menos una; <c>false</c>: ninguna; <c>null</c>:
    /// git falló o dio una salida inesperada. Un fallo por límite lanza
    /// <see cref="GitCommandLimitException"/>.
    /// </summary>
    private async Task<bool?> IsTipContainedInAnotherRefAsync(string path, string branchName, string tipSha, CancellationToken ct)
    {
        // La keep-ref de la rama ya existe cuando se llega aquí (clean guarda
        // en la papelera antes de borrar) y contiene la punta por construcción.
        // CreateTrashKeepRefAsync impide que sea un alias de una rama remota o
        // un tag, pero si un fallo futuro lo dejara pasar, contarla haría pasar
        // toda rama (T4R3-01). Por eso nunca cuenta una ref bajo refs/gitsweep/
        // ni una cuyo último componente sea el id de una keep-ref existente o
        // creada en esta ejecución. Un id es un GUID aleatorio, así que esto
        // nunca excluye una ref real del usuario; si no se puede listar, la
        // respuesta es «no verificado».
        var keepRefIds = await GetTrashKeepRefIdsAsync(path, ct);
        if (keepRefIds is null)
        {
            return null;
        }

        // Ningún argumento puede leerse como opción: el SHA validado empieza
        // por un dígito hexadecimal y los patrones son constantes. Campos
        // separados por TAB, que ningún nombre de ref puede contener.
        // %(symref) identifica las refs simbólicas: no guardan commits
        // propios, y un repositorio hostil puede plantar una (por ejemplo
        // refs/remotes/origin/HEAD) que apunte a la propia rama; al borrarla
        // quedaría colgando, así que nunca cuentan como «otra ref».
        string[] refPrefixes = ["refs/heads/", "refs/remotes/", "refs/tags/"];
        var result = await ExecuteGitCommandAsync(
            path, ["for-each-ref", "--contains", tipSha, "--format=%(refname)%09%(symref)", .. refPrefixes], ct);
        ThrowIfLimitFailure(result);
        if (result.ExitCode != 0)
        {
            return null;
        }

        // La propia rama se compara sin distinguir mayúsculas, como el resto
        // del servicio: en un sistema de archivos que no las distingue, una
        // variante de mayúsculas puede ser la misma ref que borra `-d`.
        // Ignorarla solo puede rechazar un borrado, nunca permitirlo.
        var ownRef = $"refs/heads/{branchName}";
        var containedElsewhere = false;
        foreach (var rawLine in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = rawLine.TrimEnd('\r').Split('\t');
            if (fields.Length != 2 ||
                !refPrefixes.Any(prefix => fields[0].StartsWith(prefix, StringComparison.Ordinal)))
            {
                return null;
            }

            if (fields[1].Length == 0 &&
                !string.Equals(fields[0], ownRef, StringComparison.OrdinalIgnoreCase) &&
                !IsKeepRefOrAlias(fields[0], keepRefIds))
            {
                containedElsewhere = true;
            }
        }

        return containedElsewhere;
    }

    /// <summary>
    /// Comprobación posterior a un <c>git branch -d</c> que tuvo éxito.
    /// Devuelve <c>true</c> si otra ref real sigue conteniendo
    /// <paramref name="tipSha"/>. Si ninguna lo contiene, vuelve a crear la
    /// rama en esa punta (nunca con <c>-f</c>) y devuelve <c>false</c>, para
    /// que el borrado se informe como rechazado y clean revierta la entrada
    /// de la papelera de una rama que vuelve a existir.
    /// </summary>
    /// <remarks>
    /// Nunca devuelve <c>false</c> con la rama sin recrear: clean revertiría
    /// entonces la entrada de la papelera, quizá el único registro del SHA.
    /// <list type="bullet">
    /// <item>Si la recreación falla, lanza
    /// <see cref="BranchDeletionVerificationException"/> con el nombre y el
    /// SHA: la entrada se conserva y clean se detiene.</item>
    /// <item>Si la comprobación misma falla, la rama se recrea igualmente
    /// (lo seguro ante la duda) y después se lanza: la
    /// <see cref="GitCommandLimitException"/> original si fue un límite, y
    /// <see cref="BranchDeletionVerificationException"/> en otro caso.</item>
    /// </list>
    /// Ninguna de las dos la traga un <c>catch</c> filtrado de la CLI. Usa
    /// <see cref="CancellationToken.None"/>: con la rama ya borrada, la
    /// comprobación y la recreación deben terminar (cada comando sigue
    /// acotado por su plazo); <c>gc.auto=0</c> forzado impide que se poden
    /// objetos entretanto.
    /// </remarks>
    private async Task<bool> IsTipStillContainedAfterDeleteAsync(string path, string branchName, string tipSha)
    {
        var ct = CancellationToken.None;
        bool? containedElsewhere;
        GitCommandLimitException? limitFailure = null;
        try
        {
            containedElsewhere = await IsTipContainedInAnotherRefAsync(path, branchName, tipSha, ct);
        }
        catch (GitCommandLimitException ex)
        {
            containedElsewhere = null;
            limitFailure = ex;
        }

        if (containedElsewhere == true)
        {
            return true;
        }

        string? recreationFailure = null;
        var (created, createError) = await CreateBranchAsync(path, branchName, tipSha, ct);
        if (!created)
        {
            recreationFailure = string.IsNullOrWhiteSpace(createError) ? "git branch failed." : createError.Trim();
        }
        else
        {
            try
            {
                var recreatedTip = await GetBranchTipShaAsync(path, branchName, ct);
                if (!string.Equals(recreatedTip, tipSha, StringComparison.OrdinalIgnoreCase))
                {
                    recreationFailure = "the recreated branch does not point at its original tip.";
                }
            }
            catch (GitCommandLimitException ex)
            {
                recreationFailure = ex.Message;
            }
        }

        if (recreationFailure is not null)
        {
            var reachability = containedElsewhere is null
                ? "GitSweep could not verify that another branch, remote-tracking branch or tag still contains its tip"
                : "no other branch, remote-tracking branch or tag contains its tip";
            var failureMessage =
                $"Branch '{branchName}' was deleted, but {reachability} {tipSha}, and recreating the branch failed: {recreationFailure.TrimEnd('.')}. " +
                $"Its commit is safe: the GitSweep trash and a keep-ref under refs/gitsweep/trash/ hold {tipSha}, so 'git gc' will not discard it.";
            throw limitFailure is null
                ? new BranchDeletionVerificationException(failureMessage)
                : new BranchDeletionVerificationException(failureMessage, limitFailure);
        }

        if (limitFailure is not null)
        {
            // La rama ya está recreada en su punta, pero un límite nunca es
            // una respuesta de git: se relanza la excepción original.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(limitFailure);
        }

        if (containedElsewhere is null)
        {
            throw new BranchDeletionVerificationException(
                $"Could not verify that the commits of branch '{branchName}' are still reachable after deleting it; the branch was recreated at its tip {tipSha}.");
        }

        return false;
    }

    public async Task<(bool Success, string ErrorMessage)> FetchPruneAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path))
        {
            return (false, "Repository path does not exist.");
        }

        ct.ThrowIfCancellationRequested();

        // Resolve the origin URL and fetch from the URL itself, never from the
        // remote name: `git fetch origin` also applies the repository's own
        // `remote.origin.fetch` refspecs, and a hostile cloned repo can set
        // that refspec to `+refs/heads/*:refs/heads/*`, which makes a plain
        // `fetch --prune` force-update and even prune (delete) LOCAL branches.
        //
        // Fetching by URL is NOT enough on its own, though: git still looks up
        // a configured remote BY NAME, and for `fetch <url>` the name is the
        // URL string, so a hostile repo can plant a section keyed by that
        // exact URL — `[remote "<origin-url>"]` — whose values git then
        // applies to this "anonymous" fetch (verified, OBS-2/OBS-3):
        //  - `uploadpack = <program>` would run <program> locally (through the
        //    shell) for a file/local URL. `--upload-pack=git-upload-pack` on
        //    the argv pins the helper to git's own, overriding that config.
        //  - `fetch = +refs/heads/*:refs/heads/*` is applied as the
        //    OPPORTUNISTIC-UPDATE refmap and force-overwrites (or prunes) local
        //    branches. `--refmap=` (empty) clears that configured refmap, so
        //    only the explicit command-line refspec below is honored.
        // With both pinned, the single refspec updates or prunes only
        // remote-tracking refs under refs/remotes/origin/*: a local branch is
        // never created, moved, deleted or force-updated (--no-tags also keeps
        // fetch from touching local tags, and submodules are never recursed
        // into). A URL starting with '-' is refused so it can never be parsed
        // as a git option (for example a forged `--upload-pack=<program>`).
        // Without an `origin` remote the fetch fails closed: gone detection is
        // defined against origin (see GetDefaultBranchNameAsync), and silently
        // succeeding would let callers trust stale remote state.
        // GIT_TERMINAL_PROMPT=0 (set by the shared executor) makes the fetch
        // fail closed instead of prompting for credentials, and transport
        // helpers and protocols are pinned by the shared executor so repo
        // config cannot substitute a program of its own. Like every command,
        // both git calls here go through the partial-clone guard of the shared
        // executor. The reported message is redacted before it leaves this
        // method: git errors quote the remote URL, which may embed
        // credentials, and callers print the message verbatim.
        var remoteUrlResult = await ExecuteGitCommandAsync(path, ["remote", "get-url", "origin"], ct);
        if (IsLimitFailure(remoteUrlResult))
        {
            return (false, GitOutputRedactor.Redact(remoteUrlResult.Error));
        }

        var remoteUrl = remoteUrlResult.Output.Trim();
        if (remoteUrlResult.ExitCode != 0 || remoteUrl.Length == 0 || remoteUrl.StartsWith('-'))
        {
            return (false, "Remote 'origin' is not configured; cannot fetch and prune.");
        }

        var cookieRefusal = await GetSaveCookiesRefusalAsync(path, ct);
        if (cookieRefusal is not null)
        {
            return (false, cookieRefusal);
        }

        // El fetch es la única operación de red y lleva su propio plazo,
        // más largo que el de la plomería local. Un origin hostil que acepta
        // la conexión y nunca responde (o que inunda stderr con texto
        // `remote:`) ya no cuelga ni agota la memoria de `clean --fetch`: se
        // mata el árbol de procesos de git y el fetch falla cerrado, así que
        // nunca se detectan ramas gone a partir de un estado remoto a medias.
        (int ExitCode, string Output, string Error) result;
        try
        {
            // --no-prune-tags (P1b-06): un fetch.pruneTags/remote.<url>.pruneTags
            // del repositorio añadiría refs/tags/*:refs/tags/* y podaría tags
            // LOCALES. Hoy git ya no lo aplica cuando hay un refspec en la línea
            // de comandos, pero esa regla no está documentada: se fija también
            // aquí.
            result = await RunRepositoryGitCommandAsync(
                path,
                ["fetch", "--prune", "--no-prune-tags", "--no-tags", "--recurse-submodules=no", "--upload-pack=git-upload-pack", "--refmap=", remoteUrl, "+refs/heads/*:refs/remotes/origin/*"],
                _limits.FetchTimeout,
                ct,
                null);
        }
        finally
        {
            // OBS-R2: el fetch es el único comando de GitSweep que puede hacer
            // que la config del repositorio cambie a mitad de ejecución (un
            // servidor que anuncia un remoto promisor y una config con
            // promisor.acceptFromServer + promisor.storeFields), así que tras cada
            // intento (éxito, fallo, límite o cancelación) se olvida el «no es
            // un clon parcial» memorizado y el siguiente comando vuelve a
            // comprobarlo. Igual con la verificación de hooks configurados: el
            // fetch es el único comando que podría dejar config nueva.
            _verifiedNotPartialClone.TryRemove(PartialCloneMemoKey(path), out _);
            _verifiedNoDangerousConfiguredHooks.TryRemove(PartialCloneMemoKey(path), out _);
        }

        var message = string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim();
        return (result.ExitCode == 0, GitOutputRedactor.Redact(message));
    }

    // Claves http.<url>.saveCookies, en la forma normalizada de
    // `git config --get-regexp` (sección y variable en minúsculas). La genérica
    // http.saveCookies no encaja (exige una subsección): la neutraliza el -c.
    private const string UrlScopedSaveCookiesRegex = @"^http\..+\.savecookies$";

    /// <summary>
    /// P1b-04: <c>null</c> si ninguna clave <c>http.&lt;url&gt;.saveCookies</c>
    /// vale true; si no, el mensaje (en inglés) con el que se rechaza el fetch.
    /// </summary>
    /// <remarks>
    /// Con <c>http.cookieFile</c> y <c>http.saveCookies</c>, curl escribe al
    /// terminar las cookies que mandó el servidor, como un fichero de cookies
    /// de Netscape, en la ruta que eligió el repositorio: un fichero fuera del
    /// repositorio (p. ej. ~/.bashrc) sobrescrito con líneas que controla el
    /// servidor. La clave genérica la neutraliza el -c http.saveCookies=false,
    /// pero la de una URL gana a la genérica (la coincidencia más específica
    /// gana, esté en el fichero que esté) y no se puede fijar sin conocer la
    /// URL efectiva, que el propio repositorio puede redirigir
    /// (<c>[remote "&lt;url&gt;"] url</c>, insteadOf). Por eso se rechaza el
    /// fetch si alguna clave con URL vale true. <c>--type=bool</c> normaliza
    /// el valor; uno que no es booleano hace fallar a git, y cualquier
    /// respuesta que no sea «ninguna» o una lista legible también rechaza el
    /// fetch. El patrón es una constante: no puede leerse como opción.
    /// </remarks>
    private async Task<string?> GetSaveCookiesRefusalAsync(string path, CancellationToken ct)
    {
        const string unverified =
            "Could not check the repository's http.<url>.saveCookies settings; refusing to fetch.";

        var result = await ExecuteGitCommandAsync(
            path, ["config", "-z", "--type=bool", "--get-regexp", UrlScopedSaveCookiesRegex], ct);
        if (result.ExitCode == 1 && result.Output.Length == 0)
        {
            return null;
        }

        if (result.ExitCode != 0)
        {
            return unverified;
        }

        var records = result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (records.Length == 0)
        {
            return unverified;
        }

        foreach (var record in records)
        {
            var newline = record.IndexOf('\n');
            var value = newline < 0 ? string.Empty : record[(newline + 1)..].Trim();
            if (value == "false")
            {
                continue;
            }

            return value == "true"
                ? "The repository configuration sets http.<url>.saveCookies, which would let the remote write a cookie file at a path chosen by the repository; refusing to fetch. Remove that setting, or run clean without --fetch."
                : unverified;
        }

        return null;
    }

    public async Task<bool> IsSquashOrRebaseMergedAsync(string path, string targetBranch, string branchName, CancellationToken ct = default)
    {
        if (!IsValidBranchName(targetBranch) || !IsValidBranchName(branchName))
        {
            return false;
        }

        if (string.Equals(targetBranch, branchName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Directory.Exists(path))
        {
            return false;
        }

        ct.ThrowIfCancellationRequested();

        // Always resolve operands as local branches. A bare name follows
        // gitrevisions order (refs/tags/<name> before refs/heads/<name>), so
        // a tag sharing the branch name would shadow the branch and could
        // fabricate a merge for a branch whose changes are not in the target.
        // IsValidBranchName rejects "..", so the refs/heads/ prefix cannot be
        // escaped. `--end-of-options` keeps the operand from ever being
        // parsed as an option (defense in depth behind the name validation).
        var targetRef = $"refs/heads/{targetBranch}";
        var branchRef = $"refs/heads/{branchName}";

        // Cada paso exige ExitCode == 0 para seguir hacia «mergeada»; un
        // fallo por límite (código negativo, salida vacía) acaba en false,
        // el valor conservador, igual que cualquier otro error de git.

        // Rebase / cherry-pick case: every commit on the branch already has a
        // patch-equivalent commit on the target. `git cherry` marks those
        // commits with '-', and commits still missing from the target with '+'.
        var cherryResult = await ExecuteGitCommandAsync(path, ["cherry", "--end-of-options", targetRef, branchRef], ct);
        if (cherryResult.ExitCode == 0)
        {
            var cherryLines = NonEmptyLines(cherryResult.Output);
            if (cherryLines.Count > 0 && cherryLines.All(line => line.StartsWith('-')))
            {
                return true;
            }
        }

        // Squash case: a squash merge collapses the whole branch into a single
        // commit on the target, so per-commit patch comparison cannot see it.
        // Rebuild the branch's cumulative change as one synthetic commit on
        // top of the merge-base and ask `git cherry` whether an equivalent
        // patch already exists on the target.
        var mergeBaseResult = await ExecuteGitCommandAsync(path, ["merge-base", "--end-of-options", targetRef, branchRef], ct);
        if (mergeBaseResult.ExitCode != 0)
        {
            return false;
        }

        var mergeBase = mergeBaseResult.Output.Trim();
        if (!IsHexSha(mergeBase))
        {
            return false;
        }

        var branchTreeResult = await ExecuteGitCommandAsync(path, ["rev-parse", $"{branchRef}^{{tree}}"], ct);
        var mergeBaseTreeResult = await ExecuteGitCommandAsync(path, ["rev-parse", $"{mergeBase}^{{tree}}"], ct);
        if (branchTreeResult.ExitCode != 0 || mergeBaseTreeResult.ExitCode != 0)
        {
            return false;
        }

        var branchTree = branchTreeResult.Output.Trim();
        var mergeBaseTree = mergeBaseTreeResult.Output.Trim();
        if (!IsHexSha(branchTree) || !IsHexSha(mergeBaseTree))
        {
            return false;
        }

        // No net change relative to the merge-base: be conservative and do
        // not claim a squash/rebase merge. This also covers the ancestor
        // case, which `git branch --merged` already reports.
        if (string.Equals(branchTree, mergeBaseTree, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Write the synthetic commit into a throwaway quarantine object
        // directory (with the repository's objects as alternate) instead of
        // the scanned repository: detection must stay read-only, so a scan
        // never mutates the user's repository or writes into a repo that the
        // caller may only have read access to. Fail closed if the quarantine
        // cannot be set up.
        var commonDirResult = await ExecuteGitCommandAsync(
            path, ["rev-parse", "--path-format=absolute", "--git-common-dir"], ct);
        if (commonDirResult.ExitCode != 0)
        {
            return false;
        }

        var repositoryObjects = Path.Combine(commonDirResult.Output.Trim(), "objects");
        if (!Directory.Exists(repositoryObjects))
        {
            return false;
        }

        var quarantineDirectory = Path.Combine(
            Path.GetTempPath(), "GitSweep", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(quarantineDirectory);
            var quarantineEnvironment = new Dictionary<string, string>(SyntheticCommitIdentity)
            {
                ["GIT_OBJECT_DIRECTORY"] = quarantineDirectory,
                ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = repositoryObjects
            };

            var syntheticResult = await ExecuteGitCommandAsync(
                path,
                ["commit-tree", branchTree, "-p", mergeBase, "-m", SyntheticCommitMessage],
                ct,
                quarantineEnvironment);
            if (syntheticResult.ExitCode != 0)
            {
                return false;
            }

            var syntheticCommit = syntheticResult.Output.Trim();
            if (!IsHexSha(syntheticCommit))
            {
                return false;
            }

            var squashCherryResult = await ExecuteGitCommandAsync(
                path, ["cherry", "--end-of-options", targetRef, syntheticCommit], ct, quarantineEnvironment);
            if (squashCherryResult.ExitCode != 0)
            {
                return false;
            }

            // A single synthetic commit: '-' means its patch already exists on
            // the target (squash-merged); '+' means it does not.
            return squashCherryResult.Output.TrimStart().StartsWith('-');
        }
        finally
        {
            try
            {
                if (Directory.Exists(quarantineDirectory))
                {
                    Directory.Delete(quarantineDirectory, recursive: true);
                }
            }
            catch (Exception)
            {
                // Best effort only; the directory lives under the OS temp root.
            }
        }
    }

    public async Task<BranchRiskSignals> GetBranchRiskSignalsAsync(
        string path, string targetBranch, string branchName, CancellationToken ct = default)
    {
        if (!IsValidBranchName(targetBranch) || !IsValidBranchName(branchName) ||
            string.Equals(targetBranch, branchName, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(path))
        {
            return BranchRiskSignals.Unknown;
        }

        ct.ThrowIfCancellationRequested();
        var targetRef = $"refs/heads/{targetBranch}";
        var branchRef = $"refs/heads/{branchName}";

        // Un fallo por límite en cualquier paso da el valor conservador: no
        // mergeada (solo ExitCode == 0 confirma el ancestro; el código
        // sintético es distinto del 1 de --is-ancestor), conteos null
        // (desconocido, nunca cero), sin upstream (más peligrosa, no menos)
        // y no gone.
        var ancestorResult = await ExecuteGitCommandAsync(
            path, ["merge-base", "--is-ancestor", "--end-of-options", branchRef, targetRef], ct);
        var isReallyMerged = ancestorResult.ExitCode == 0 ||
            await IsSquashOrRebaseMergedAsync(path, targetBranch, branchName, ct);

        // Commits únicos por parche: 'git cherry' marca con '+' los que no
        // tienen equivalente en el destino. Con merge real el parche
        // acumulado ya está en el destino, así que el conteo es 0.
        int? uniqueCommitCount;
        if (isReallyMerged)
        {
            uniqueCommitCount = 0;
        }
        else
        {
            var cherryResult = await ExecuteGitCommandAsync(
                path, ["cherry", "--end-of-options", targetRef, branchRef], ct);
            uniqueCommitCount = cherryResult.ExitCode == 0
                ? NonEmptyLines(cherryResult.Output).Count(line => line.StartsWith('+'))
                : null;
        }

        // Sin subir: contra el upstream si existe; sin upstream, todo lo que
        // no está en el destino solo existe en local.
        var ancestryUnique = await CountCommitsAsync(path, $"{targetRef}..{branchRef}", ct);
        // Sin --end-of-options: `git rev-parse` lo repetiría en la salida.
        // El nombre ya está validado (nunca empieza por '-') y va como un
        // único argumento de ArgumentList, sin shell.
        var upstreamResult = await ExecuteGitCommandAsync(
            path, ["rev-parse", "--abbrev-ref", $"{branchName}@{{upstream}}"], ct);
        var upstream = upstreamResult.ExitCode == 0 ? upstreamResult.Output.Trim() : string.Empty;

        bool hasUpstream;
        int? unpushedCommitCount;
        if (upstream.Length > 0 && !upstream.StartsWith('-'))
        {
            hasUpstream = true;
            unpushedCommitCount = await CountCommitsAsync(path, $"{upstream}..{branchRef}", ct);
        }
        else
        {
            hasUpstream = false;
            unpushedCommitCount = ancestryUnique;
        }

        // Gone: upstream configured but its remote-tracking ref is missing
        // (for-each-ref reports %(upstream:track) as "[gone]"). When gone,
        // `rev-parse <branch>@{upstream}` fails above, so HasUpstream is
        // false and unpushed falls back to "not in target": fail-closed.
        var trackResult = await ExecuteGitCommandAsync(
            path, ["for-each-ref", "--format=%(upstream:short)\t%(upstream:track)", branchRef], ct);
        var isUpstreamGone = false;
        if (trackResult.ExitCode == 0)
        {
            // TAB-separated for the same reason as GetLocalBranchesAsync:
            // neither an upstream name nor the track field can contain a
            // control character, so no field can forge the other.
            var trackParts = trackResult.Output.TrimEnd('\r', '\n').Split('\t');
            isUpstreamGone = trackParts.Length == 2 && trackParts[0].Length > 0 &&
                IsGoneTrack(trackParts[1]);
        }

        return new BranchRiskSignals(uniqueCommitCount, unpushedCommitCount, hasUpstream, isReallyMerged, isUpstreamGone);
    }

    private async Task<int?> CountCommitsAsync(string path, string range, CancellationToken ct)
    {
        var result = await ExecuteGitCommandAsync(path, ["rev-list", "--count", "--end-of-options", range], ct);
        return result.ExitCode == 0 && int.TryParse(result.Output.Trim(), out var count) && count >= 0
            ? count
            : null;
    }

    public async Task<string?> GetGitCommonDirectoryAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        var result = await ExecuteGitCommandAsync(
            path, ["rev-parse", "--path-format=absolute", "--git-common-dir"], ct);
        ThrowIfLimitFailure(result);
        if (result.ExitCode != 0)
        {
            return null;
        }

        var gitDir = result.Output.Trim();
        if (gitDir.Length == 0)
        {
            return null;
        }

        // --path-format=absolute should already be absolute; normalize anyway
        // so callers can safely build contained paths from it.
        return Path.GetFullPath(gitDir);
    }

    public async Task<string?> GetBranchTipShaAsync(string path, string branchName, CancellationToken ct = default)
    {
        if (!IsValidBranchName(branchName) || !Directory.Exists(path))
        {
            return null;
        }

        var result = await ExecuteGitCommandAsync(
            path, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branchName}^{{commit}}"], ct);

        // null significa «la rama no existe» (BranchExistsAsync lo usa para
        // permitir que restore la cree): un fallo por límite no puede
        // convertirse en esa respuesta.
        ThrowIfLimitFailure(result);
        if (result.ExitCode != 0)
        {
            return null;
        }

        var sha = result.Output.Trim();
        return IsHexSha(sha) ? sha : null;
    }

    public async Task<bool> BranchExistsAsync(string path, string branchName, CancellationToken ct = default)
    {
        return await GetBranchTipShaAsync(path, branchName, ct) is not null;
    }

    public async Task<(bool Success, string ErrorMessage)> CreateBranchAsync(
        string path, string branchName, string commitSha, CancellationToken ct = default)
    {
        if (!IsValidBranchName(branchName))
        {
            return (false, $"Invalid branch name: '{branchName}'.");
        }

        if (!IsHexSha(commitSha))
        {
            return (false, "Invalid commit SHA.");
        }

        // P3-T1 (O8): en un sistema de archivos que no distingue mayúsculas
        // (Windows, macOS), crear refs/heads/feature con un `Feature`
        // empaquetado deja un fichero suelto que git lee también como
        // refs/heads/Feature: la rama existente pasaría a apuntar al commit
        // restaurado. Se rechaza si ya hay una rama cuyo nombre solo difiere
        // en mayúsculas, en todos los sistemas (solo añade rechazos).
        // N-1: un fallo por límite en este listado se informa como fallo (no
        // se relanza), para cumplir el contrato de CreateBranchAsync («nunca
        // lanza»): así restore conserva la entrada y su keep-ref y sigue el
        // lote, y la recreación posterior al borrado acaba en
        // BranchDeletionVerificationException como siempre. El guardado del
        // propio GetPreDeleteRefusalAsync sí relanza (coherente con sus pares).
        bool? caseVariant;
        try
        {
            caseVariant = await ExistsBranchDifferingOnlyInCaseAsync(path, branchName, ct);
        }
        catch (GitCommandLimitException ex)
        {
            return (false, ex.Message);
        }

        switch (caseVariant)
        {
            case null:
                return (false, "Could not list the existing branches.");
            case true:
                return (false, $"A branch whose name differs from '{branchName}' only in letter case already exists; git could not keep both apart on a case-insensitive file system, so '{branchName}' was not created.");
        }

        // Never overwrite: plain `git branch <name> <start>` fails when the
        // branch already exists (no -f, no -D anywhere in GitSweep). `--`
        // keeps the branch name from parsing as an option, and the start
        // point starts with a hex digit, so it cannot parse as one either.
        // V-01 / O-F1: el punto de partida va como `<sha>^{commit}`, nunca como
        // el SHA desnudo. `git branch` pasa el punto de partida por dwim_ref,
        // que sustituye un SHA de 40/64 hex por el valor de una ref con ese
        // mismo nombre (p. ej. un tag `<sha>` que llegó con un fetch): la rama
        // se creaba en otro commit y restore/undo borraban después la entrada
        // y la keep-ref del commit que nunca se restauró. Ninguna ref puede
        // llamarse `<sha>^{commit}`, así que git usa el objeto y lo pela a
        // commit (verificado con 2.55).
        // Un fallo por límite se informa como fallo: restore conserva la
        // entrada de la papelera, y si git llegó a crear la rama, el
        // siguiente restore la encuentra y decide sin crear nada.
        var result = await ExecuteGitCommandAsync(path, ["branch", "--", branchName, $"{commitSha}^{{commit}}"], ct);
        var message = GitOutputRedactor.Redact(string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim());
        if (result.ExitCode != 0)
        {
            return (false, message);
        }

        // Nunca fiarse solo de que git terminó con 0: la rama debe apuntar al
        // commit pedido. Si no (o si no se puede comprobar), se informa como
        // fallo, así que restore conserva la entrada y su keep-ref.
        string? createdTip;
        try
        {
            createdTip = await GetBranchTipShaAsync(path, branchName, ct);
        }
        catch (GitCommandLimitException ex)
        {
            return (false, ex.Message);
        }

        return string.Equals(createdTip, commitSha, StringComparison.OrdinalIgnoreCase)
            ? (true, message)
            : (false, $"git created branch '{branchName}', but it does not point at commit {commitSha.ToLowerInvariant()}.");
    }

    /// <summary>
    /// <c>true</c> si ya existe una rama cuyo nombre coincide con
    /// <paramref name="branchName"/> salvo en mayúsculas (otra ref distinta,
    /// misma caja ignorada); <c>false</c> si no; <c>null</c> si no se puede
    /// listar. El patrón del listado es una constante. Un fallo por límite
    /// lanza.
    /// </summary>
    private async Task<bool?> ExistsBranchDifferingOnlyInCaseAsync(string path, string branchName, CancellationToken ct)
    {
        var listing = await ExecuteGitCommandAsync(path, ["for-each-ref", "--format=%(refname)", "refs/heads/"], ct);
        ThrowIfLimitFailure(listing);
        if (listing.ExitCode != 0)
        {
            return null;
        }

        var ownRef = $"refs/heads/{branchName}";
        return listing.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Any(refName => string.Equals(refName, ownRef, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(refName, ownRef, StringComparison.Ordinal));
    }

    // Espacio de nombres propio de GitSweep, fuera de refs/heads, refs/remotes
    // y refs/tags: las comprobaciones de alcanzabilidad nunca cuentan una
    // keep-ref (ni un alias suyo, ver IsKeepRefOrAlias) como «otra rama», así
    // que su presencia no altera la decisión de borrado; solo mantiene vivo el
    // commit frente a `git gc`.
    private const string TrashKeepRefNamespace = "refs/gitsweep/";
    private const string TrashKeepRefPrefix = "refs/gitsweep/trash/";

    // Directorios, relativos al directorio git común, por los que git escribe
    // el fichero suelto de una keep-ref: refs/gitsweep/trash/<id>.
    private static readonly string[] TrashKeepRefDirectories = ["refs", "gitsweep", "trash"];

    // Ids de todas las keep-refs que este servicio ha intentado crear en esta
    // ejecución (se anotan antes de crearlas). Las comprobaciones de
    // alcanzabilidad nunca cuentan una ref cuyo último componente sea uno de
    // estos ids, aunque git no liste la keep-ref bajo refs/gitsweep/ (un
    // junction en refs/gitsweep/trash hace que git for Windows escriba el
    // fichero en el destino y deje de verlo con su propio nombre).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _trashKeepRefIdsThisRun =
        new(StringComparer.OrdinalIgnoreCase);

    private static string KeepRefPathUnsafeMessage(string directory) =>
        $"'{directory}' in the repository's git directory is a symbolic link, a junction or another reparse point, or is not a plain directory, so the keep-ref could be redirected into another ref namespace";

    private const string KeepRefAliasMessage =
        "the keep-ref is also visible under another ref name (a linked directory in the repository's ref store), where a fetch or prune could delete it";

    private const string KeepRefUnverifiedMessage =
        "GitSweep could not verify that git sees the keep-ref only under its own name";

    public async Task<(bool Success, string ErrorMessage)> CreateTrashKeepRefAsync(
        string path, string trashId, string commitSha, CancellationToken ct = default)
    {
        if (!TrashService.IsValidTrashId(trashId) || !IsHexSha(commitSha))
        {
            return (false, "the trash id or the commit SHA is invalid");
        }

        var keepRef = TrashKeepRefPrefix + trashId;
        var sha = commitSha.ToLowerInvariant();

        // Se anota antes de tocar nada, para que ninguna ref con este id cuente
        // jamás como «otra ref» en esta ejecución, se cree o no la keep-ref.
        _trashKeepRefIdsThisRun.TryAdd(trashId, 0);

        // 1. Sistema de archivos, antes de crear nada. Un enlace en
        // refs/gitsweep o refs/gitsweep/trash (un junction, que no requiere
        // privilegios, o un enlace simbólico relativo que viaja en un tarball)
        // hace que git escriba la keep-ref físicamente en su destino: bajo
        // refs/remotes/<remoto> pasa a ser una rama remota de seguimiento que
        // el siguiente `fetch --prune` (incluido el de `clean --fetch`) borra,
        // bajo refs/tags un tag que `--prune-tags` borra, y en ambos casos la
        // comprobación previa al borrado la contaría como «otra ref». Por eso
        // cada componente existente debe ser un directorio real; los ausentes
        // los crea git como directorios reales.
        var commonDirectory = await GetGitCommonDirectoryAsync(path, ct);
        if (commonDirectory is null)
        {
            return (false, "the repository's git directory could not be resolved");
        }

        var unsafeDirectory = FindUnsafeKeepRefDirectory(commonDirectory);
        if (unsafeDirectory is not null)
        {
            return (false, KeepRefPathUnsafeMessage(unsafeDirectory));
        }

        // 2. Solo-creación: el valor antiguo todo-ceros (de la longitud del
        // SHA, 40 o 64 según el formato de objeto del repo) exige que la ref no
        // exista. Así, un refs/gitsweep o refs/gitsweep/trash plantado
        // (empaquetado o como fichero) hace fallar la creación y el guardado
        // falla cerrado. --no-deref escribe la propia ref, nunca el destino de
        // un symref. core.logAllRefUpdates=false: ningún reflog que un enlace
        // en logs/ pudiera desviar fuera de .git. El nombre empieza por
        // "refs/" y los SHA por un dígito hexadecimal, así que ningún operando
        // puede leerse como opción.
        var zeroOid = new string('0', sha.Length);
        try
        {
            var create = await ExecuteGitCommandAsync(
                path, ["-c", "core.logAllRefUpdates=false", "update-ref", "--no-deref", keepRef, sha, zeroOid], ct);
            ThrowIfLimitFailure(create);
            if (create.ExitCode != 0)
            {
                return (false, "git could not create it");
            }

            var refusal = await GetCreatedKeepRefRefusalAsync(path, keepRef, trashId, sha, ct);
            if (refusal is null)
            {
                return (true, string.Empty);
            }

            await DeleteTrashKeepRefAsync(path, trashId, sha, CancellationToken.None);
            return (false, refusal);
        }
        catch (Exception)
        {
            // Límite o cancelación con la keep-ref quizá creada: se quita (si
            // existe con este valor) y se relanza; el guardado falla cerrado.
            await DeleteTrashKeepRefAsync(path, trashId, sha, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Comprobaciones de una keep-ref recién creada: <c>null</c> si resuelve a
    /// <paramref name="sha"/> y git la ve solo con su propio nombre; si no, el
    /// motivo (en inglés) del rechazo. Un fallo por límite lanza
    /// <see cref="GitCommandLimitException"/>.
    /// </summary>
    /// <remarks>
    /// Defensa en profundidad, independiente del sistema de archivos (cubre
    /// también un enlace en otro sitio que apunte a refs/gitsweep/trash, un
    /// montaje...): todo alias de la keep-ref es el mismo fichero visto a
    /// través de un directorio enlazado, así que su nombre termina en
    /// <c>/&lt;id&gt;</c>, y el id es un GUID aleatorio que ninguna otra ref
    /// puede llevar por azar. Se rechaza:
    /// <list type="bullet">
    /// <item>cualquier otro nombre listado cuyo último componente sea el id;</item>
    /// <item>un aviso de stderr que nombre el id: for-each-ref solo avisa de
    /// una ref con nombre inválido (p. ej. bajo refs/remotes/origin/a..b),
    /// pero `fetch --prune` sí la borra, y el fichero de la keep-ref con ella;</item>
    /// <item>que la propia keep-ref no aparezca en el listado: es el mismo
    /// recorrido de refs que usa `git gc` para decidir qué conservar, así que
    /// una keep-ref invisible para él no protegería nada.</item>
    /// </list>
    /// Quitarla después con comparar-y-borrar nunca borra una ref del usuario:
    /// la creación solo-creación tuvo éxito, así que ningún fichero existía en
    /// esa ruta y el borrado por nombre solo quita el que git acaba de crear
    /// (cualquier alias es ese mismo fichero; una ref distinta con el mismo id
    /// no se toca).
    /// </remarks>
    private async Task<string?> GetCreatedKeepRefRefusalAsync(
        string path, string keepRef, string trashId, string sha, CancellationToken ct)
    {
        // Nunca fiarse solo de la creación: verificar que resuelve al SHA.
        var verify = await ExecuteGitCommandAsync(
            path, ["rev-parse", "--verify", "--quiet", keepRef], ct);
        ThrowIfLimitFailure(verify);
        if (verify.ExitCode != 0 ||
            !string.Equals(verify.Output.Trim(), sha, StringComparison.OrdinalIgnoreCase))
        {
            return "it does not resolve to the saved commit";
        }

        // Todas las refs (sin patrón: un alias puede estar en cualquier
        // espacio de nombres) que apuntan exactamente al SHA, acotado por las
        // refs en ese commit. El SHA validado empieza por un dígito
        // hexadecimal, así que no puede leerse como opción.
        var listing = await ExecuteGitCommandAsync(
            path, ["for-each-ref", "--points-at", sha, "--format=%(refname)"], ct);
        ThrowIfLimitFailure(listing);
        if (listing.ExitCode != 0)
        {
            return KeepRefUnverifiedMessage;
        }

        var keepRefListed = false;
        foreach (var rawLine in listing.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var refName = rawLine.TrimEnd('\r');
            if (!refName.StartsWith("refs/", StringComparison.Ordinal))
            {
                return KeepRefUnverifiedMessage;
            }

            if (string.Equals(refName, keepRef, StringComparison.Ordinal))
            {
                keepRefListed = true;
            }
            else if (string.Equals(LastRefComponent(refName), trashId, StringComparison.OrdinalIgnoreCase))
            {
                return KeepRefAliasMessage;
            }
        }

        if (listing.Error.Contains(trashId, StringComparison.OrdinalIgnoreCase))
        {
            return KeepRefAliasMessage;
        }

        return keepRefListed ? null : KeepRefUnverifiedMessage;
    }

    /// <summary>
    /// Inspecciona, sin seguir enlaces, los directorios por los que git
    /// escribirá la keep-ref bajo <paramref name="commonDirectory"/>.
    /// Devuelve <c>null</c> si <c>refs</c> es un directorio real y
    /// <c>refs/gitsweep</c> y <c>refs/gitsweep/trash</c> son directorios reales
    /// o no existen; si no, el nombre relativo (<c>refs</c>,
    /// <c>refs/gitsweep</c> o <c>refs/gitsweep/trash</c>) del primero que no
    /// lo es.
    /// </summary>
    /// <remarks>
    /// También <c>refs</c>: un enlace ahí traslada todo el almacén de refs a
    /// otro directorio, y lo que ese directorio sea para otro repositorio (por
    /// ejemplo sus refs/remotes) queda fuera de lo que git puede comprobar
    /// aquí. Los componentes se comprueban en orden, así que la ruta de cada
    /// uno solo atraviesa directorios ya verificados como reales. Cualquier
    /// error al inspeccionar cuenta como inseguro.
    /// </remarks>
    private static string? FindUnsafeKeepRefDirectory(string commonDirectory)
    {
        var current = commonDirectory;
        var relative = string.Empty;
        foreach (var component in TrashKeepRefDirectories)
        {
            current = Path.Combine(current, component);
            relative = relative.Length == 0 ? component : relative + "/" + component;
            switch (InspectKeepRefDirectory(current))
            {
                case KeepRefDirectoryState.RealDirectory:
                    continue;
                case KeepRefDirectoryState.Absent when relative != "refs":
                    // git crea como directorios reales este y los siguientes.
                    return null;
                default:
                    return relative;
            }
        }

        return null;
    }

    private enum KeepRefDirectoryState
    {
        Absent,
        RealDirectory,
        Unsafe
    }

    private static KeepRefDirectoryState InspectKeepRefDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);

            // LinkTarget lee el propio enlace sin seguirlo: no es null para un
            // enlace simbólico ni para un junction, aunque su destino no
            // exista (Exists, en cambio, sigue el enlace y diría «ausente»).
            if (info.LinkTarget is not null)
            {
                return KeepRefDirectoryState.Unsafe;
            }

            if (!info.Exists)
            {
                // Un fichero (u otro objeto que no es un directorio) en medio
                // no es «ausente».
                return File.Exists(path) ? KeepRefDirectoryState.Unsafe : KeepRefDirectoryState.Absent;
            }

            // Cualquier otro punto de reanálisis (no solo enlaces y junctions)
            // también se rechaza.
            var attributes = info.Attributes;
            return (attributes & FileAttributes.ReparsePoint) == 0 && (attributes & FileAttributes.Directory) != 0
                ? KeepRefDirectoryState.RealDirectory
                : KeepRefDirectoryState.Unsafe;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            return KeepRefDirectoryState.Unsafe;
        }
    }

    private static string LastRefComponent(string refName) =>
        refName[(refName.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Ids de keep-ref que las comprobaciones de alcanzabilidad excluyen: los
    /// que este servicio intentó crear en esta ejecución más los de las
    /// keep-refs que git lista bajo <c>refs/gitsweep/</c>. <c>null</c> si el
    /// listado falla o da una salida inesperada; un fallo por límite lanza
    /// <see cref="GitCommandLimitException"/>.
    /// </summary>
    private async Task<HashSet<string>?> GetTrashKeepRefIdsAsync(string path, CancellationToken ct)
    {
        var ids = new HashSet<string>(_trashKeepRefIdsThisRun.Keys, StringComparer.OrdinalIgnoreCase);

        // El patrón es una constante: no puede leerse como opción. Solo
        // cuentan los últimos componentes con forma de id (32 hex): un enlace
        // en refs/gitsweep hacia otro espacio de nombres listaría aquí también
        // nombres como "main", que nunca deben excluirse.
        var listing = await ExecuteGitCommandAsync(
            path, ["for-each-ref", "--format=%(refname)", TrashKeepRefNamespace], ct);
        ThrowIfLimitFailure(listing);
        if (listing.ExitCode != 0)
        {
            return null;
        }

        foreach (var rawLine in listing.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var refName = rawLine.TrimEnd('\r');
            if (!refName.StartsWith(TrashKeepRefNamespace, StringComparison.Ordinal))
            {
                return null;
            }

            var lastComponent = LastRefComponent(refName);
            if (TrashService.IsValidTrashId(lastComponent))
            {
                ids.Add(lastComponent);
            }
        }

        return ids;
    }

    // Una keep-ref o un alias suyo nunca cuenta como «otra ref» que contenga
    // la punta: no es una rama, rama remota ni tag del usuario, y un alias bajo
    // refs/remotes o refs/tags desaparece con el siguiente prune.
    private static bool IsKeepRefOrAlias(string refName, HashSet<string> keepRefIds) =>
        refName.StartsWith(TrashKeepRefNamespace, StringComparison.OrdinalIgnoreCase) ||
        keepRefIds.Contains(LastRefComponent(refName));

    public async Task<bool> DeleteTrashKeepRefAsync(
        string path, string trashId, string commitSha, CancellationToken ct = default)
    {
        if (!TrashService.IsValidTrashId(trashId) || !IsHexSha(commitSha))
        {
            return false;
        }

        var keepRef = TrashKeepRefPrefix + trashId;

        // Comparar-y-borrar: git solo quita la ref si aún apunta a <commitSha>,
        // así que una ref con otro valor jamás se borra y una ausente falla sin
        // efecto. Best-effort: cualquier fallo (incluido un límite) deja la
        // keep-ref, lo cual es seguro (solo fija el commit) y nunca bloquea una
        // restauración.
        try
        {
            var result = await ExecuteGitCommandAsync(
                path, ["update-ref", "-d", "--no-deref", keepRef, commitSha.ToLowerInvariant()], ct);
            return result.ExitCode == 0;
        }
        catch (GitCommandLimitException)
        {
            return false;
        }
    }

    public async Task<string?> GetCurrentBranchAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path)) return null;
        var result = await ExecuteGitCommandAsync(path, ["branch", "--show-current"], ct);
        // null quitaría la protección de la rama actual: lanzar, no adivinar.
        ThrowIfLimitFailure(result);
        if (result.ExitCode != 0) return null;
        var name = result.Output.Trim();
        return IsValidBranchName(name) ? name : null;
    }

    public async Task<IReadOnlyList<string>> GetWorktreeBranchesAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path)) return [];
        var result = await ExecuteGitCommandAsync(path, ["worktree", "list", "--porcelain"], ct);
        // Una lista vacía quitaría la protección de las ramas en worktrees.
        ThrowIfLimitFailure(result);
        if (result.ExitCode != 0) return [];
        var branches = new List<string>();
        foreach (var rawLine in result.Output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!line.StartsWith("branch ", StringComparison.Ordinal)) continue;
            var refName = line["branch ".Length..].Trim();
            const string prefix = "refs/heads/";
            if (!refName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var name = refName[prefix.Length..];
            if (IsValidBranchName(name) && !branches.Contains(name, StringComparer.OrdinalIgnoreCase))
                branches.Add(name);
        }
        return branches;
    }

    public async Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path)) return null;
        var result = await ExecuteGitCommandAsync(path, ["rev-parse", "--show-toplevel"], ct);
        // Sin raíz, GitSweepConfigService buscaría .gitsweep.json en otra
        // ruta y podría cargar la configuración por defecto (sin las
        // protecciones del usuario): lanzar, no devolver null.
        ThrowIfLimitFailure(result);
        if (result.ExitCode != 0) return null;
        var root = result.Output.Trim();
        return root.Length == 0 ? null : Path.GetFullPath(root);
    }

    /// <summary>
    /// Validates a branch name before it is passed to git. Besides the usual
    /// ref-format rules, a leading '-' is rejected so a crafted name can never
    /// be interpreted as a git option. All git invocations also use
    /// <see cref="ProcessStartInfo.ArgumentList"/> (no shell), so a name is
    /// always a single argument.
    /// </summary>
    internal static bool IsValidBranchName(string? branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName) || branchName.Length > 255)
        {
            return false;
        }

        if (branchName[0] is '-' or '.' or '/' ||
            branchName[^1] is '.' or '/' ||
            branchName is "@" ||
            branchName.Contains("..", StringComparison.Ordinal) ||
            branchName.Contains("//", StringComparison.Ordinal) ||
            branchName.Contains("@{", StringComparison.Ordinal) ||
            branchName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var c in branchName)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\')
            {
                return false;
            }
        }

        foreach (var component in branchName.Split('/'))
        {
            if (component.Length == 0 ||
                component[0] == '.' ||
                component.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    // %(upstream:track) reports exactly "[gone]" for a deleted upstream;
    // anything else ("[ahead 1]", "[behind 2]", empty) is not gone. An
    // exact match keeps a value that merely contains the word (or a forged
    // field) from being read as a deleted upstream.
    private static bool IsGoneTrack(string track) =>
        track.Trim().Equals("[gone]", StringComparison.OrdinalIgnoreCase);

    private static List<string> NonEmptyLines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool IsHexSha(string value) =>
        value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    /// <summary>Shared SHA validation for trash storage: full 40/64-char hex only.</summary>
    internal static bool IsValidCommitSha(string? value) =>
        value is not null && IsHexSha(value);

    private static HashSet<string> ParseBranchList(string output)
    {
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimStart('*', '+', ' '))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsLimitFailure((int ExitCode, string Output, string Error) result) =>
        result.ExitCode is TimedOutExitCode or OutputLimitExceededExitCode;

    // Un fallo por límite significa que git no llegó a responder. Los métodos
    // que informan de un hecho del repositorio no pueden convertirlo en su
    // valor por defecto («no existe», «sin rama actual», «sin worktrees»...),
    // que el llamador leería como señal segura: lanzan con el mensaje en
    // inglés que compuso el ejecutor (subcomando y límite).
    private static void ThrowIfLimitFailure((int ExitCode, string Output, string Error) result)
    {
        if (IsLimitFailure(result))
        {
            throw new GitCommandLimitException(result.Error);
        }
    }

    private Task<(int ExitCode, string Output, string Error)> ExecuteGitCommandAsync(
        string workingDirectory, string[] arguments, CancellationToken ct) =>
        ExecuteGitCommandAsync(workingDirectory, arguments, ct, null);

    private Task<(int ExitCode, string Output, string Error)> ExecuteGitCommandAsync(
        string workingDirectory,
        string[] arguments,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environmentVariables) =>
        RunRepositoryGitCommandAsync(
            workingDirectory,
            arguments,
            _limits.LocalCommandTimeout,
            ct,
            environmentVariables);

    /// <summary>
    /// Punto único por el que pasa todo comando git contra un repositorio
    /// (plomería local y <c>fetch</c>): antes de lanzarlo aplica dos guardas,
    /// que viven aquí y no en cada método para que un método nuevo quede
    /// cubierto sin acordarse de ellas:
    /// <list type="number">
    /// <item><see cref="EnsureNotPartialCloneAsync"/>: en un clon parcial
    /// cualquier comando que lea un objeto ausente (<c>cherry</c>,
    /// <c>branch &lt;rama&gt; &lt;sha&gt;</c>, <c>rev-parse &lt;sha&gt;^{commit}</c>,
    /// <c>log</c>...) dispara un lazy fetch que ejecuta el
    /// <c>remote.&lt;promisor&gt;.uploadpack</c> del repositorio.</item>
    /// <item><see cref="EnsureNoDangerousConfiguredHooksAsync"/>: la config del
    /// repositorio puede definir un hook (git &gt;= 2.54) para un evento que
    /// GitSweep dispara (p. ej. <c>reference-transaction</c>), que
    /// <c>core.hooksPath</c> no gobierna.</item>
    /// </list>
    /// Solo <see cref="ExecuteObjectFreeGitCommandAsync"/> las omite.
    /// </summary>
    private async Task<(int ExitCode, string Output, string Error)> RunRepositoryGitCommandAsync(
        string workingDirectory,
        string[] arguments,
        TimeSpan timeout,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environmentVariables)
    {
        await EnsureNotPartialCloneAsync(workingDirectory, ct);
        await EnsureNoDangerousConfiguredHooksAsync(workingDirectory, ct);
        return await RunGitProcessAsync(
            workingDirectory,
            arguments,
            timeout,
            _limits.MaxStandardOutputChars,
            _limits.MaxStandardErrorChars,
            ct,
            environmentVariables);
    }

    /// <summary>
    /// Ejecuta un comando SIN las guardas de <see cref="RunRepositoryGitCommandAsync"/>.
    /// Reservado a los comandos que nunca leen objetos y que hacen falta antes
    /// o para las propias guardas: localizar el repositorio
    /// (<c>rev-parse --git-dir</c>), detectar el clon parcial y listar los
    /// hooks configurados (ambos <c>config --get-regexp</c>). Ninguno puede
    /// disparar un lazy fetch ni correría un hook por una actualización de ref:
    /// no se resuelve ningún objeto ni se toca ninguna ref.
    /// </summary>
    private Task<(int ExitCode, string Output, string Error)> ExecuteObjectFreeGitCommandAsync(
        string workingDirectory, string[] arguments, CancellationToken ct) =>
        RunGitProcessAsync(
            workingDirectory,
            arguments,
            _limits.LocalCommandTimeout,
            _limits.MaxStandardOutputChars,
            _limits.MaxStandardErrorChars,
            ct);

    // Claves que hacen de un remoto un promisor (git las lee en
    // promisor-remote.c): extensions.partialClone, remote.<n>.promisor y
    // remote.<n>.partialclonefilter. Verificado con git 2.55 y 2.43: cada una
    // por separado basta para que git haga lazy fetch, incluido un
    // partialclonefilter SIN promisor. git normaliza a minúsculas la sección y
    // la variable en --get-regexp; la subsección (nombre del remoto) conserva
    // su caja y la cubre «.*». La subsección es opcional (OBS-R1): con
    // `[remote] promisor = true` (clave remote.promisor, sin subsección) git
    // registra un remoto promisor de nombre vacío y también hace lazy fetch
    // (verificado con GIT_TRACE en 2.55 y 2.43). El clasificador trata
    // remote.promisor igual que remote.<n>.promisor.
    private const string PartialCloneConfigRegex =
        @"^(extensions\.partialclone|remote\.(.*\.)?(promisor|partialclonefilter))$";

    // Rutas (normalizadas) que esta instancia ya comprobó que NO son un clon
    // parcial: la detección corre una vez por repositorio y no una por
    // comando. Solo se memoriza el resultado seguro; un clon parcial o un
    // resultado indeterminado se vuelven a rechazar en cada llamada.
    private readonly ConcurrentDictionary<string, bool> _verifiedNotPartialClone =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// Falla cerrado si el repositorio es un clon parcial (promisor) o si no
    /// se puede determinar: lanza <see cref="PartialCloneNotSupportedException"/>.
    /// Lee la configuración con un <c>git config --get-regexp</c> de solo
    /// lectura (incluye include.path/includeIf y la config de worktree igual
    /// que el propio git), que nunca resuelve objetos y por tanto no puede
    /// disparar el lazy fetch que pretende evitar.
    /// </summary>
    private async Task EnsureNotPartialCloneAsync(string workingDirectory, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(workingDirectory);
        var key = PartialCloneMemoKey(workingDirectory);
        if (_verifiedNotPartialClone.ContainsKey(key))
        {
            return;
        }

        var result = await ExecuteObjectFreeGitCommandAsync(
            workingDirectory, ["config", "-z", "--get-regexp", PartialCloneConfigRegex], ct);

        // Un fallo por límite deja la respuesta indeterminada: fallar cerrado,
        // nunca suponer «no es un clon parcial».
        ThrowIfLimitFailure(result);

        switch (ClassifyPartialCloneConfig(result.ExitCode, result.Output))
        {
            case PartialCloneConfigStatus.NotPartialClone:
                _verifiedNotPartialClone.TryAdd(key, true);
                return;

            case PartialCloneConfigStatus.PartialClone:
                throw new PartialCloneNotSupportedException(
                    $"The repository at '{fullPath}' is a partial clone (it fetches missing objects on demand " +
                    "from a promisor remote). GitSweep refuses to run git in a partial clone, because reading a " +
                    "missing object would make git fetch it and run a program configured by the repository " +
                    "(remote.<name>.uploadpack). Run GitSweep in a full clone of the repository.");

            default:
                // Ni 0 ni 1 (config ilegible, error fatal...) o un 0 sin
                // registros: no se puede confirmar que NO es un clon parcial.
                // No se incluye stderr: podría citar texto del repositorio.
                throw new PartialCloneNotSupportedException(
                    $"Could not determine whether the repository at '{fullPath}' is a partial clone " +
                    $"(git config exited with code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}). " +
                    "GitSweep refuses to continue, because in a partial clone reading a missing object could " +
                    "make git run a program configured by the repository.");
        }
    }

    // Clave del memo: la ruta completa normalizada del directorio de trabajo.
    private static string PartialCloneMemoKey(string workingDirectory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));

    // Eventos de hook que GitSweep NO puede disparar con ninguno de sus
    // comandos: solo los disparan git commit/merge/am/rebase/checkout/switch/
    // push/receive-pack/send-email/p4, que GitSweep nunca ejecuta. Lista
    // blanca con denegación por defecto (RG-01): un evento configurado que no
    // esté aquí hace rechazar el repositorio. Por eso NO están, a propósito:
    // reference-transaction y pre-auto-gc (los que GitSweep dispara),
    // post-index-change y fsmonitor-watchman (relacionados con el índice/
    // fsmonitor, por prudencia) y cualquier evento desconocido o futuro.
    // Tomada de githooks(5) de git 2.54/2.55.
    private static readonly HashSet<string> HarmlessHookEvents = new(StringComparer.Ordinal)
    {
        "applypatch-msg", "pre-applypatch", "post-applypatch",
        "pre-commit", "pre-merge-commit", "prepare-commit-msg", "commit-msg", "post-commit",
        "pre-rebase", "post-checkout", "post-merge", "pre-push", "post-rewrite",
        "pre-receive", "update", "proc-receive", "post-receive", "post-update", "push-to-checkout",
        "sendemail-validate",
        "p4-changelist", "p4-prepare-changelist", "p4-post-changelist", "p4-pre-submit"
    };

    // Lista cada clave que ENLAZA un hook a un evento en la config efectiva
    // (incluye include.path/includeIf, como git). `(.*\.)?` cubre las tres
    // formas que git enlaza (RG2-01): sin subsección (`[hook] event`, clave
    // `hook.event`), con subsección vacía (`[hook ""] event`, clave
    // `hook..event`) y con un nombre amistoso cualquiera, con puntos o caja
    // propia (`hook.<nombre>.event`). Igual que PartialCloneConfigRegex con
    // `(.*\.)?` para OBS-R1. Es de solo lectura y no resuelve objetos.
    private const string ConfiguredHookEventRegex = @"^hook\.(.*\.)?event$";

    // Tope de nombres de evento que se citan en el mensaje de rechazo: una
    // config hostil puede declarar miles (RG2-O1/M-7); el resto se resume como
    // «and N more».
    private const int MaxReportedHookEvents = 5;

    // Rutas ya verificadas sin hooks configurados peligrosos: se comprueba una
    // vez por repositorio, no una por comando (se olvida tras cada fetch, el
    // único comando que podría dejar config nueva). Solo se memoriza el
    // resultado seguro; un rechazo se vuelve a evaluar.
    private readonly ConcurrentDictionary<string, bool> _verifiedNoDangerousConfiguredHooks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// Falla cerrado (lanza <see cref="RepositoryHookNotSupportedException"/>)
    /// si la configuración del repositorio define un hook por configuración
    /// (git &gt;= 2.54) para un evento que GitSweep puede disparar, o si no se
    /// puede leer la lista de eventos. Independiente de la versión de git y de
    /// cualquier semántica de <c>hook.*.enabled</c> (RG-01/RR-1): se decide solo
    /// por el nombre del evento, nunca por el valor del comando, y nunca se
    /// construyen argumentos <c>-c</c> a partir de texto del repositorio.
    /// </summary>
    private async Task EnsureNoDangerousConfiguredHooksAsync(string workingDirectory, CancellationToken ct)
    {
        var key = PartialCloneMemoKey(workingDirectory);
        if (_verifiedNoDangerousConfiguredHooks.ContainsKey(key))
        {
            return;
        }

        var result = await ExecuteObjectFreeGitCommandAsync(
            workingDirectory, ["config", "-z", "--get-regexp", ConfiguredHookEventRegex], ct);

        // Un fallo por límite deja la respuesta indeterminada: fallar cerrado.
        ThrowIfLimitFailure(result);

        var refusal = ClassifyConfiguredHookEvents(result.ExitCode, result.Output);
        if (refusal is null)
        {
            _verifiedNoDangerousConfiguredHooks.TryAdd(key, true);
            return;
        }

        throw new RepositoryHookNotSupportedException(refusal);
    }

    /// <summary>
    /// Clasifica la salida de <c>git config -z --get-regexp</c> con
    /// <see cref="ConfiguredHookEventRegex"/>: <c>null</c> si no hay ningún hook
    /// configurado para un evento que GitSweep pueda disparar; si no, el mensaje
    /// (en inglés) del rechazo, que nombra el/los evento(s) saneados y nunca el
    /// valor del comando. Falla cerrado ante cualquier salida inesperada.
    /// </summary>
    /// <param name="exitCode">1 = ninguna clave de evento de hook (nada que
    /// revisar); 0 = hubo coincidencias; cualquier otro código es
    /// indeterminado.</param>
    /// <param name="output">Registros separados por NUL: <c>clave LF valor</c>,
    /// o solo <c>clave</c> si no tiene valor.</param>
    internal static string? ClassifyConfiguredHookEvents(int exitCode, string output)
    {
        if (exitCode == 1)
        {
            return null;
        }

        const string unreadable =
            "GitSweep could not read the repository's configured git hooks (hook.<name>.event); refusing to continue.";
        if (exitCode != 0)
        {
            return unreadable;
        }

        var records = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (records.Length == 0)
        {
            // Un 0 promete al menos una coincidencia: salida inesperada.
            return unreadable;
        }

        var dangerousEvents = new List<string>();
        foreach (var record in records)
        {
            var newline = record.IndexOf('\n');
            // Una clave sin valor (`[hook "x"] event` a secas) no nombra ningún
            // evento conocido, pero es config de hook inesperada: fallar cerrado.
            var value = newline < 0 ? string.Empty : record[(newline + 1)..];
            var eventName = value.Trim();
            if (HarmlessHookEvents.Contains(eventName))
            {
                continue;
            }

            var display = SanitizeHookEventName(eventName);
            if (!dangerousEvents.Contains(display, StringComparer.Ordinal))
            {
                dangerousEvents.Add(display);
            }
        }

        if (dangerousEvents.Count == 0)
        {
            return null;
        }

        // Acota la lista de nombres: una config hostil puede declarar miles.
        var shown = dangerousEvents.Take(MaxReportedHookEvents).ToList();
        var listed = string.Join(", ", shown);
        if (dangerousEvents.Count > shown.Count)
        {
            listed += $", and {(dangerousEvents.Count - shown.Count).ToString(CultureInfo.InvariantCulture)} more";
        }

        return
            "The repository configuration defines a git hook (hook.<name>.command) for an event GitSweep does not " +
            $"allow because its own commands could trigger it: {listed}. Such a hook runs a program chosen by the " +
            "repository, and core.hooksPath does not disable it, so GitSweep refuses to run any git command here. " +
            "Remove the hook.<name>.event / hook.<name>.command settings for that event, or run GitSweep elsewhere.";
    }

    // Nombre de evento para el mensaje: solo letras ASCII, dígitos, '.', '_' y
    // '-' (todos los nombres de evento reales lo son); cualquier otro carácter
    // del texto del repositorio se sustituye por '?', y la longitud se acota.
    // El mensaje además pasa por el saneo de terminal de la CLI.
    private static string SanitizeHookEventName(string value)
    {
        if (value.Length == 0)
        {
            return "(empty)";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Length > 64 ? value[..64] : value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '?');
        }

        return builder.ToString();
    }

    internal enum PartialCloneConfigStatus
    {
        NotPartialClone,
        PartialClone,
        Undetermined
    }

    /// <summary>
    /// Clasifica la salida de <c>git config -z --get-regexp</c> con
    /// <see cref="PartialCloneConfigRegex"/>. Falla cerrado: cualquier clave
    /// encontrada marca un clon parcial, salvo un <c>remote.&lt;n&gt;.promisor</c>
    /// cuyo valor es una de las formas explícitas de «falso» de git
    /// (<c>false</c>/<c>no</c>/<c>off</c>/<c>0</c>). git acepta como verdadero
    /// <c>true</c>/<c>yes</c>/<c>on</c>/<c>1</c> en cualquier caja y también la
    /// clave sin valor, así que nada de eso puede pasar por «no es parcial».
    /// </summary>
    /// <param name="exitCode">1 = ninguna clave (no es parcial); 0 = hubo
    /// coincidencias; cualquier otro código es indeterminado.</param>
    /// <param name="output">Registros separados por NUL: <c>clave\nvalor</c>, o
    /// solo <c>clave</c> si no tiene valor.</param>
    internal static PartialCloneConfigStatus ClassifyPartialCloneConfig(int exitCode, string output)
    {
        if (exitCode == 1)
        {
            return PartialCloneConfigStatus.NotPartialClone;
        }

        if (exitCode != 0)
        {
            return PartialCloneConfigStatus.Undetermined;
        }

        var records = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (records.Length == 0)
        {
            // Un 0 promete al menos una coincidencia: salida inesperada.
            return PartialCloneConfigStatus.Undetermined;
        }

        foreach (var record in records)
        {
            if (!IsExplicitlyDisabledPromisor(record))
            {
                return PartialCloneConfigStatus.PartialClone;
            }
        }

        return PartialCloneConfigStatus.NotPartialClone;
    }

    // true solo para «remote.<n>.promisor» con un valor falso explícito. Una
    // clave sin valor (registro sin '\n') es un «true» implícito para git, y un
    // valor vacío u otra cosa también se trata como promisor (fallar cerrado).
    private static bool IsExplicitlyDisabledPromisor(string record)
    {
        var newline = record.IndexOf('\n');
        if (newline < 0)
        {
            return false;
        }

        var key = record[..newline].Trim();
        if (!key.StartsWith("remote.", StringComparison.OrdinalIgnoreCase) ||
            !key.EndsWith(".promisor", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = record[(newline + 1)..].Trim();
        return value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("0", StringComparison.Ordinal);
    }

    /// <summary>
    /// Ejecutor único de git. Devuelve el código de salida y la salida
    /// completa, o un fallo sintético (<see cref="TimedOutExitCode"/> /
    /// <see cref="OutputLimitExceededExitCode"/>, salida vacía y un error en
    /// inglés) cuando vence <paramref name="timeout"/> o stdout/stderr
    /// superan su tope; en ambos casos se mata todo el árbol de procesos.
    /// La cancelación del llamador mata el árbol y lanza
    /// <see cref="OperationCanceledException"/>, como siempre.
    /// Es el ejecutor crudo, SIN la guarda de clon parcial: el código de
    /// producción que actúa sobre un repositorio pasa siempre por
    /// <see cref="RunRepositoryGitCommandAsync"/> (las pruebas lo usan directo
    /// para medir plazos y topes).
    /// </summary>
    internal static async Task<(int ExitCode, string Output, string Error)> RunGitProcessAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int maxOutputChars,
        int maxErrorChars,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        // Se lanza git por ruta absoluta de confianza y nunca por nombre: en
        // Windows un nombre desnudo hace que CreateProcess busque primero en
        // el directorio actual, y el directorio por defecto de GitSweep es el
        // repositorio analizado, asi que un git.exe plantado en un clon
        // hostil se ejecutaria saltandose todo el endurecimiento de abajo.
        // Si git no se encuentra se falla cerrado (InvalidOperationException).
        var gitPath = GitExecutableLocator.GetRequiredPath();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = gitPath,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // No inherited terminal: a child that asks for credentials
                // or confirmation (ssh, credential helpers) gets EOF at
                // once and fails closed instead of prompting or hanging.
                // The pipe is closed right after Start() below.
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var option in HardenedGitOptions)
        {
            process.StartInfo.ArgumentList.Add(option);
        }

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        ApplyHardenedEnvironment(process.StartInfo.Environment, OperatingSystem.IsWindows());

        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                process.StartInfo.Environment[key] = value;
            }
        }

        ct.ThrowIfCancellationRequested();
        process.Start();

        // stdin cerrado nada más arrancar: el hijo nunca hereda la terminal
        // (stdin sigue redirigido) y cualquier lectura de stdin recibe EOF al
        // instante en vez de esperar hasta el plazo.
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // El hijo ya terminó y cerró su extremo de la tubería.
        }

        // Plazo por invocación: un token enlazado con el del llamador al que
        // se añade CancelAfter. Se cancela por el plazo o por el usuario; ct
        // por sí solo distingue después la cancelación del usuario (que
        // relanza) del plazo vencido (que devuelve un fallo).
        using var limitSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limitSource.CancelAfter(timeout);
        var limitToken = limitSource.Token;

        // stdout y stderr se leen a la vez (un hijo bloqueado escribiendo en
        // una tubería llena nunca termina) y cada uno en un búfer acotado. El
        // lector que supera su tope deja de acumular y lo avisa aquí, para
        // matar el árbol de inmediato sin esperar al plazo.
        var overflow = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputTask = ReadBoundedAsync(process.StandardOutput, maxOutputChars, overflow, limitToken);
        var errorTask = ReadBoundedAsync(process.StandardError, maxErrorChars, overflow, limitToken);
        var completion = Task.WhenAll(process.WaitForExitAsync(limitToken), outputTask, errorTask);

        var completed = false;
        try
        {
            // WaitAsync garantiza despertar al vencer el plazo aunque una
            // lectura de tubería no admita cancelación (Windows) o un nieto
            // huérfano mantenga la tubería abierta tras salir git.
            var first = await Task.WhenAny(completion, overflow.Task).WaitAsync(limitToken);
            if (first == completion)
            {
                await completion;
                completed = true;
            }
        }
        catch (OperationCanceledException) when (limitSource.IsCancellationRequested)
        {
            // Plazo vencido o cancelación del usuario: se distingue abajo.
        }
        finally
        {
            if (!completed)
            {
                // No dejar ningún git vivo: podría seguir con locks del
                // repositorio, terminar una escritura más tarde o seguir
                // escupiendo salida que ya nadie lee.
                limitSource.Cancel();
                await KillProcessTreeAsync(process);
                ObserveAbandonedReads(completion);
            }
        }

        if (completed && outputTask.Result is { } output && errorTask.Result is { } error)
        {
            return (process.ExitCode, output, error);
        }

        if (!completed)
        {
            // La cancelación del usuario conserva su comportamiento: lanza.
            ct.ThrowIfCancellationRequested();
        }

        // Nunca se devuelve salida truncada como éxito: salida vacía, código
        // sintético distinto de cualquier código de git y un error en inglés.
        var subcommand = arguments.Count > 0 ? arguments[0] : "command";
        if (overflow.Task.IsCompleted)
        {
            var limit = overflow.Task.Result.ToString("N0", CultureInfo.InvariantCulture);
            return (OutputLimitExceededExitCode, string.Empty,
                $"git {subcommand} output exceeded the {limit}-character limit.");
        }

        var seconds = timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        return (TimedOutExitCode, string.Empty, $"git {subcommand} timed out after {seconds} seconds.");
    }

    /// <summary>
    /// Entorno endurecido del proceso git hijo (se aplica sobre una copia del
    /// entorno del proceso actual): quita las variables de git heredadas,
    /// reduce PATH a sus entradas totalmente cualificadas y fuerza
    /// <c>GIT_TERMINAL_PROMPT=0</c>, <c>GIT_PAGER=cat</c>,
    /// <c>GIT_NO_LAZY_FETCH=1</c>, la lista de transportes permitidos
    /// (<c>GIT_ALLOW_PROTOCOL</c>), un <c>GIT_PROXY_COMMAND</c> vacío y un
    /// fichero de grafts vacío (<c>GIT_GRAFT_FILE</c>).
    /// </summary>
    internal static void ApplyHardenedEnvironment(IDictionary<string, string?> environment, bool isWindows)
    {
        foreach (var variable in RemovedGitEnvironmentVariables)
        {
            environment.Remove(variable);
        }

        // Indexed/prefixed families (GIT_CONFIG_COUNT, GIT_CONFIG_KEY_<n>,
        // GIT_CONFIG_VALUE_<n>, GIT_CONFIG_PARAMETERS, GIT_CONFIG_GLOBAL,
        // GIT_CONFIG_SYSTEM, GIT_TRACE*) cannot be listed exhaustively.
        foreach (var variable in environment.Keys
                     .Where(key => key.StartsWith("GIT_CONFIG", StringComparison.OrdinalIgnoreCase) ||
                                   key.StartsWith("GIT_TRACE", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            environment.Remove(variable);
        }

        // PATH solo con entradas totalmente cualificadas (mismo filtro que
        // GitExecutableLocator): git resuelve sus programas auxiliares (sh de
        // los alias, ssh del fetch, helpers remotos) en PATH desde su
        // directorio de trabajo, que es el repositorio analizado, así que una
        // entrada vacía o relativa ("." , "bin") los buscaría dentro de un
        // clon hostil. Si no queda ninguna entrada se quita PATH en vez de
        // dejarlo vacío: POSIX trata un PATH vacío como el directorio actual.
        // En Windows el nombre de la variable no distingue mayúsculas.
        var pathComparison = isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var key in environment.Keys
                     .Where(key => string.Equals(key, "PATH", pathComparison))
                     .ToList())
        {
            var filtered = GitExecutableLocator.FilterPathVariable(environment[key], isWindows);
            if (filtered.Length == 0)
            {
                environment.Remove(key);
            }
            else
            {
                environment[key] = filtered;
            }
        }

        // Never block on an interactive credential/terminal prompt: all git
        // operations here are local plumbing and must fail closed instead.
        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GIT_PAGER"] = "cat";

        // En un clon parcial (promisor) le faltan objetos a git, y cualquier
        // comando que lea uno ausente (`git cherry` en la detección de squash/
        // rebase, `git branch <rama> <sha>` al restaurar...) dispara un «lazy
        // fetch» del remoto promisor que ejecuta por el shell el programa de
        // `remote.<promisor>.uploadpack` configurado por el repositorio (OBS-1).
        // GIT_NO_LAZY_FETCH=1 desactiva ese fetch perezoso, pero solo lo
        // respetan git >= 2.44 y las versiones que lo retroportan. Es defensa en
        // profundidad: el control principal es que el ejecutor se niega a correr
        // cualquier comando contra un clon parcial (EnsureNotPartialCloneAsync).
        environment["GIT_NO_LAZY_FETCH"] = "1";

        // P1b-01: una clave protocol.<nombre>.allow=always del repositorio
        // tiene prioridad sobre el protocol.allow=never fijado con -c (la clave
        // por protocolo gana a la genérica, sea cual sea el fichero), así que
        // `<x>::<url>` (o remote.<url>.vcs, o un insteadOf hacia él) volvía a
        // ejecutar `git remote-<x>`, y con un `alias.remote-<x> = !<cmd>` del
        // repositorio, un comando de shell. GIT_ALLOW_PROTOCOL se impone a
        // toda la configuración (git(1)): solo quedan los transportes
        // estándar.
        environment["GIT_ALLOW_PROTOCOL"] = AllowedProtocols;

        // P1b-02: core.gitProxy es «gana la primera coincidencia», así que el
        // valor del repositorio se lee antes que el -c core.gitProxy= y ganaba:
        // cada conexión git:// ejecutaba el programa que eligió el
        // repositorio. Con GIT_PROXY_COMMAND definida git ni siquiera mira
        // core.gitProxy, y vacía significa «sin proxy».
        environment["GIT_PROXY_COMMAND"] = string.Empty;

        // I5: el fichero de grafts del repositorio (info/grafts) inventa padres
        // de commits. Apuntar GIT_GRAFT_FILE al dispositivo nulo lo sustituye
        // por un fichero siempre vacío (el aviso de git al abrirlo lo calla
        // advice.graftFileDeprecated=false).
        environment["GIT_GRAFT_FILE"] = NullDevicePath;
    }

    // Lee un flujo del hijo acumulando como mucho maxChars caracteres.
    // Devuelve null (y lo avisa por overflow) en cuanto el siguiente bloque
    // superaría el tope, sin acumularlo: nunca hay salida truncada.
    private static async Task<string?> ReadBoundedAsync(
        StreamReader reader, int maxChars, TaskCompletionSource<int> overflow, CancellationToken ct)
    {
        var text = new StringBuilder();
        var chunk = new char[16 * 1024];
        while (true)
        {
            var read = await reader.ReadAsync(chunk.AsMemory(), ct);
            if (read == 0)
            {
                return text.ToString();
            }

            if (read > maxChars - text.Length)
            {
                overflow.TrySetResult(maxChars);
                return null;
            }

            text.Append(chunk, 0, read);
        }
    }

    private static async Task KillProcessTreeAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Best effort only; the process may have already exited.
        }

        try
        {
            using var grace = new CancellationTokenSource(KillWaitTimeout);
            await process.WaitForExitAsync(grace.Token);
        }
        catch (Exception)
        {
            // Best effort only: the kill was already requested.
        }
    }

    // Las lecturas abandonadas tras matar el árbol terminan solas (EOF,
    // cancelación o flujo cerrado); se observa su posible excepción para que
    // no aparezca como excepción de tarea no observada.
    private static void ObserveAbandonedReads(Task completion) =>
        _ = completion.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
