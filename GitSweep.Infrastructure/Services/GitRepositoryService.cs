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
    // never exists, so no hook is ever run.
    private static readonly string NullHooksPath =
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
    private static readonly string[] HardenedGitOptions =
    [
        "--no-pager",
        "-c", "core.fsmonitor=false",
        "-c", "core.pager=cat",
        "-c", "commit.gpgsign=false",
        "-c", "gc.auto=0",
        "-c", "maintenance.auto=false",
        "-c", $"core.hooksPath={NullHooksPath}",
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
        "-c", "sideband.allowControlCharacters=false",
        "-c", "promisor.acceptFromServer=None"
    ];

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
    // upload-pack, ...) from an arbitrary directory; GIT_PROXY_COMMAND names
    // a program git runs for every git:// connection made by `--fetch`.
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
        "GIT_PROXY_COMMAND",
        "GIT_ASKPASS",
        "SSH_ASKPASS",
        "GIT_EXTERNAL_DIFF",
        "GIT_ALLOW_PROTOCOL",
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

        // Safe delete only: never force-delete with -D. A branch git considers
        // unmerged is refused here and reported to the caller instead.
        var result = await ExecuteGitCommandAsync(path, ["branch", "-d", "--", branchName], ct);

        // Un fallo por límite deja el resultado indeterminado, no «no se
        // borró»: `-d` escribe sus avisos de stderr antes de borrar la ref y
        // el «Deleted branch» de stdout después, así que el plazo o el tope
        // pueden saltar antes, durante o después del borrado mientras se
        // mata el árbol. Devolver false haría que clean revirtiera la entrada
        // de la papelera de una rama que quizá ya no existe; se lanza para
        // que la entrada se conserve.
        ThrowIfLimitFailure(result);
        return (result.ExitCode == 0, GitOutputRedactor.Redact(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error));
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

        // El fetch es la única operación de red y lleva su propio plazo,
        // más largo que el de la plomería local. Un origin hostil que acepta
        // la conexión y nunca responde (o que inunda stderr con texto
        // `remote:`) ya no cuelga ni agota la memoria de `clean --fetch`: se
        // mata el árbol de procesos de git y el fetch falla cerrado, así que
        // nunca se detectan ramas gone a partir de un estado remoto a medias.
        (int ExitCode, string Output, string Error) result;
        try
        {
            result = await RunRepositoryGitCommandAsync(
                path,
                ["fetch", "--prune", "--no-tags", "--recurse-submodules=no", "--upload-pack=git-upload-pack", "--refmap=", remoteUrl, "+refs/heads/*:refs/remotes/origin/*"],
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
            // comprobarlo.
            _verifiedNotPartialClone.TryRemove(PartialCloneMemoKey(path), out _);
        }

        var message = string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim();
        return (result.ExitCode == 0, GitOutputRedactor.Redact(message));
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

        // Never overwrite: plain `git branch <name> <sha>` fails when the
        // branch already exists (no -f, no -D anywhere in GitSweep). The SHA
        // operand starts with a hex digit so it can never parse as an option;
        // `--` keeps the branch name from parsing as one either.
        // Un fallo por límite se informa como fallo: restore conserva la
        // entrada de la papelera, y si git llegó a crear la rama, el
        // siguiente restore informa AlreadyExists sin tocar nada.
        var result = await ExecuteGitCommandAsync(path, ["branch", "--", branchName, commitSha], ct);
        return (result.ExitCode == 0, GitOutputRedactor.Redact(string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim()));
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
    /// (plomería local y <c>fetch</c>): antes de lanzarlo comprueba que el
    /// repositorio no es un clon parcial (<see cref="EnsureNotPartialCloneAsync"/>).
    /// En un clon parcial cualquier comando que lea un objeto ausente
    /// (<c>cherry</c>, <c>branch &lt;rama&gt; &lt;sha&gt;</c>, <c>rev-parse
    /// &lt;sha&gt;^{commit}</c>, <c>log</c>...) dispara un lazy fetch que
    /// ejecuta el <c>remote.&lt;promisor&gt;.uploadpack</c> del repositorio, así
    /// que la guarda vive aquí y no en cada método: un método nuevo queda
    /// cubierto sin acordarse de ella. Solo
    /// <see cref="ExecuteObjectFreeGitCommandAsync"/> la omite.
    /// </summary>
    private async Task<(int ExitCode, string Output, string Error)> RunRepositoryGitCommandAsync(
        string workingDirectory,
        string[] arguments,
        TimeSpan timeout,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environmentVariables)
    {
        await EnsureNotPartialCloneAsync(workingDirectory, ct);
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
    /// Ejecuta un comando SIN la guarda de clon parcial. Reservado a los dos
    /// comandos que nunca leen objetos y que hacen falta antes o para la
    /// guarda: localizar el repositorio (<c>rev-parse --git-dir</c>) y la
    /// propia detección (<c>config --get-regexp</c>). Ninguno puede disparar
    /// un lazy fetch: no se resuelve ningún objeto.
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
    /// <c>GIT_TERMINAL_PROMPT=0</c> y <c>GIT_PAGER=cat</c>.
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
