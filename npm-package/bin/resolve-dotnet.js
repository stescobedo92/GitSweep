const path = require("node:path");

// Locates the `dotnet` host as an absolute path so the launcher never starts it
// by bare name.
//
// Why: on Windows, libuv (spawnSync) resolves a bare command name by searching
// the current working directory first and then PATH, trying `.com` and `.exe`.
// GitSweep is run inside the repository being cleaned, which may be a hostile
// clone, so a planted `dotnet.exe` / `dotnet.com` there would run with the
// user's privileges before GitSweep even starts (CWE-426). Only fully
// qualified locations are accepted here, so nothing is ever resolved against
// the current directory. Mirrors GitExecutableLocator.cs on the .NET side.

// Rooted on a drive ("C:\x", "C:/x") or a UNC share ("\\server\share"). This
// is stricter than path.win32.isAbsolute, which also accepts "\x" and "/x"
// (relative to the current drive, i.e. still dependent on the current
// directory); drive-relative "C:x" is rejected by both.
const WIN32_DRIVE_ROOTED = /^[A-Za-z]:[\\/]/;
const WIN32_UNC = /^[\\/]{2}[^\\/]/;

function isFullyQualified(entry, platform) {
  if (platform === "win32") {
    return path.win32.isAbsolute(entry) && (WIN32_DRIVE_ROOTED.test(entry) || WIN32_UNC.test(entry));
  }

  return path.posix.isAbsolute(entry);
}

function normalizeEntry(rawEntry, platform) {
  let entry = String(rawEntry).trim();

  // Windows lets PATH entries be wrapped in double quotes to protect ';'.
  if (platform === "win32" && entry.length >= 2 && entry.startsWith('"') && entry.endsWith('"')) {
    entry = entry.slice(1, -1).trim();
  }

  return entry;
}

/**
 * Pure resolver (no global state) so it can be tested on any platform.
 *
 * @param {object} options
 * @param {string | null | undefined} options.pathValue Value of PATH.
 * @param {string | null | undefined} options.dotnetRoot Value of DOTNET_ROOT;
 *   probed first, but only when it is a fully qualified path.
 * @param {string} [options.platform] Defaults to process.platform. Selects the
 *   delimiter (`;` on win32, `:` elsewhere), the file name (`dotnet.exe` on
 *   win32, `dotnet` elsewhere) and the absolute-path rules.
 * @param {(candidate: string) => boolean} options.isFile File-system probe.
 * @returns {string | null} Absolute path of the first match, or null.
 */
function resolveDotnet({ pathValue, dotnetRoot, platform = process.platform, isFile } = {}) {
  if (typeof isFile !== "function") {
    throw new TypeError("resolveDotnet requires an isFile(candidate) function.");
  }

  const isWindows = platform === "win32";
  // Only dotnet.exe on Windows: never .com / .cmd / .bat (cmd.exe would
  // interpret the latter two, and bare-name lookup also tries .com first).
  const name = isWindows ? "dotnet.exe" : "dotnet";
  const delimiter = isWindows ? ";" : ":";
  const join = isWindows ? path.win32.join : path.posix.join;

  const directories = [];
  if (typeof dotnetRoot === "string") {
    directories.push(dotnetRoot);
  }
  if (typeof pathValue === "string") {
    directories.push(...pathValue.split(delimiter));
  }

  for (const rawEntry of directories) {
    const entry = normalizeEntry(rawEntry, platform);

    // Empty or not fully qualified ("." , "bin", ".\bin", "\x", "C:x"): it
    // would resolve against the current directory, which in a hostile clone is
    // attacker-controlled.
    if (entry.length === 0 || !isFullyQualified(entry, platform)) {
      continue;
    }

    const candidate = join(entry, name);
    if (isFile(candidate)) {
      return candidate;
    }
  }

  return null;
}

module.exports = { resolveDotnet };
