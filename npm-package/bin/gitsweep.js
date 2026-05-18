#!/usr/bin/env node

const { spawnSync } = require("node:child_process");
const { existsSync } = require("node:fs");
const path = require("node:path");

const cliPath = path.resolve(__dirname, "..", "tools", "net10.0", "gitsweep-cli.dll");

if (!existsSync(cliPath)) {
  console.error("GitSweep executable was not found in the npm package.");
  console.error(`Expected: ${cliPath}`);
  process.exit(1);
}

const result = spawnSync("dotnet", [cliPath, ...process.argv.slice(2)], {
  stdio: "inherit",
  windowsHide: false
});

if (result.error) {
  if (result.error.code === "ENOENT") {
    console.error("GitSweep requires the .NET 10 runtime or SDK.");
    console.error("Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and try again.");
  } else {
    console.error(`Failed to start GitSweep: ${result.error.message}`);
  }

  process.exit(1);
}

process.exit(result.status ?? 1);
