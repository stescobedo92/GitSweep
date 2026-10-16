#!/usr/bin/env node

const { spawnSync } = require("node:child_process");
const { existsSync, statSync } = require("node:fs");
const path = require("node:path");
const { resolveDotnet } = require("./resolve-dotnet");

const cliPath = path.resolve(__dirname, "..", "tools", "net10.0", "gitsweep-cli.dll");

if (!existsSync(cliPath)) {
  console.error("GitSweep executable was not found in the npm package.");
  console.error(`Expected: ${cliPath}`);
  process.exit(1);
}

function printDotnetRequired() {
  console.error("GitSweep requires the .NET 10 runtime or SDK.");
  console.error("Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and try again.");
}

// Any stat failure (permissions, invalid path, ...) counts as "not a file" so
// the launcher fails closed instead of crashing on an odd PATH entry.
function isFile(candidate) {
  try {
    return statSync(candidate, { throwIfNoEntry: false })?.isFile() === true;
  } catch {
    return false;
  }
}

// GitSweep runs inside the repository being cleaned, which may be hostile.
// Starting "dotnet" by bare name would let Windows resolve it against the
// current directory first and run a planted dotnet.exe / dotnet.com, so only an
// absolute path from DOTNET_ROOT or a fully qualified PATH entry is ever
// spawned, and there is deliberately no fallback to the bare name.
const dotnetPath = resolveDotnet({
  pathValue: process.env.PATH,
  dotnetRoot: process.env.DOTNET_ROOT,
  platform: process.platform,
  isFile
});

if (dotnetPath === null) {
  printDotnetRequired();
  process.exit(1);
}

const result = spawnSync(dotnetPath, [cliPath, ...process.argv.slice(2)], {
  stdio: "inherit",
  windowsHide: false
});

if (result.error) {
  if (result.error.code === "ENOENT") {
    printDotnetRequired();
  } else {
    console.error(`Failed to start GitSweep: ${result.error.message}`);
  }

  process.exit(1);
}

process.exit(result.status ?? 1);
