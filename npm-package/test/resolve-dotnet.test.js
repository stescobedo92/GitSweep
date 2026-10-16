const { describe, test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const { resolveDotnet } = require("../bin/resolve-dotnet.js");

// Fake file-system probe that records every candidate it is asked about, so a
// test can assert not only the result but exactly what was (and was not) probed.
function probe(existing = []) {
  const probed = [];
  return {
    probed,
    isFile: (candidate) => {
      probed.push(candidate);
      return existing.includes(candidate);
    }
  };
}

// A probe that claims every file exists: any candidate that reaches it would
// be returned, so "returns null / probes nothing" proves the entry was skipped
// before the file system was consulted.
function alwaysFile() {
  const probed = [];
  return {
    probed,
    isFile: (candidate) => {
      probed.push(candidate);
      return true;
    }
  };
}

describe("resolveDotnet on win32", () => {
  test("resolves dotnet.exe from the first PATH entry that contains it", () => {
    const fake = probe(["C:\\b\\dotnet.exe"]);

    const result = resolveDotnet({
      pathValue: "C:\\a;C:\\b;C:\\c",
      platform: "win32",
      isFile: fake.isFile
    });

    assert.equal(result, "C:\\b\\dotnet.exe");
    // First match wins: C:\c is never probed.
    assert.deepEqual(fake.probed, ["C:\\a\\dotnet.exe", "C:\\b\\dotnet.exe"]);
  });

  test("skips relative, empty and '.' entries even when isFile says true", () => {
    const fake = alwaysFile();

    const result = resolveDotnet({
      pathValue: [".", "", "  ", "bin", ".\\bin", "..\\up", "tools\\dotnet", "..", "dotnet.exe"].join(";"),
      platform: "win32",
      isFile: fake.isFile
    });

    assert.equal(result, null);
    assert.deepEqual(fake.probed, []);
  });

  test("rejects drive-relative entries such as C:foo and C:", () => {
    const fake = alwaysFile();

    const result = resolveDotnet({
      pathValue: "C:foo;C:;C:foo\\bar;c:dotnet",
      platform: "win32",
      isFile: fake.isFile
    });

    assert.equal(result, null);
    assert.deepEqual(fake.probed, []);
  });

  test("rejects rooted-without-drive entries such as \\foo and /foo", () => {
    const fake = alwaysFile();

    const result = resolveDotnet({
      pathValue: "\\foo;\\;/foo;/;\\foo\\bar",
      platform: "win32",
      isFile: fake.isFile
    });

    assert.equal(result, null);
    assert.deepEqual(fake.probed, []);
  });

  test("accepts drive-letter-rooted entries with either slash style", () => {
    const fake = probe();

    resolveDotnet({
      pathValue: "C:\\a;D:/b",
      platform: "win32",
      isFile: fake.isFile
    });

    assert.deepEqual(fake.probed, ["C:\\a\\dotnet.exe", "D:\\b\\dotnet.exe"]);
  });

  test("accepts UNC entries", () => {
    const fake = probe(["\\\\server\\share\\dotnet\\dotnet.exe"]);

    const result = resolveDotnet({
      pathValue: "\\\\server\\share\\dotnet",
      platform: "win32",
      isFile: fake.isFile
    });

    assert.equal(result, "\\\\server\\share\\dotnet\\dotnet.exe");
  });

  test("strips surrounding double quotes and whitespace from entries", () => {
    const fake = probe(["C:\\Program Files\\dotnet\\dotnet.exe"]);

    const result = resolveDotnet({
      pathValue: ' "C:\\Program Files\\dotnet" ;C:\\other',
      platform: "win32",
      isFile: fake.isFile
    });

    assert.equal(result, "C:\\Program Files\\dotnet\\dotnet.exe");
    assert.deepEqual(fake.probed, ["C:\\Program Files\\dotnet\\dotnet.exe"]);
  });

  test("a quoted relative entry is still rejected", () => {
    const fake = alwaysFile();

    const result = resolveDotnet({
      pathValue: '"bin";"."',
      platform: "win32",
      isFile: fake.isFile
    });

    assert.equal(result, null);
    assert.deepEqual(fake.probed, []);
  });

  test("probes only dotnet.exe, never .com, .cmd or .bat", () => {
    const fake = probe();

    const result = resolveDotnet({
      pathValue: "C:\\a;C:\\b",
      dotnetRoot: "C:\\root",
      platform: "win32",
      isFile: fake.isFile
    });

    assert.equal(result, null);
    assert.deepEqual(fake.probed, [
      "C:\\root\\dotnet.exe",
      "C:\\a\\dotnet.exe",
      "C:\\b\\dotnet.exe"
    ]);
    for (const candidate of fake.probed) {
      assert.match(candidate, /\\dotnet\.exe$/);
    }
  });

  test("does not return a dotnet.com / dotnet.cmd / dotnet.bat that happens to exist", () => {
    const fake = probe([
      "C:\\a\\dotnet.com",
      "C:\\a\\dotnet.cmd",
      "C:\\a\\dotnet.bat",
      "C:\\a\\dotnet"
    ]);

    const result = resolveDotnet({
      pathValue: "C:\\a",
      platform: "win32",
      isFile: fake.isFile
    });

    assert.equal(result, null);
  });

  test("splits on ';' only, not ':'", () => {
    const fake = probe();

    resolveDotnet({
      pathValue: "C:\\a;C:\\b",
      platform: "win32",
      isFile: fake.isFile
    });

    // If ':' were also a delimiter "C" would be a (relative) entry and the
    // drive letters would be torn off the paths.
    assert.deepEqual(fake.probed, ["C:\\a\\dotnet.exe", "C:\\b\\dotnet.exe"]);
  });
});

describe("resolveDotnet on posix", () => {
  for (const platform of ["linux", "darwin"]) {
    test(`uses ':' as delimiter and the bare name 'dotnet' (${platform})`, () => {
      const fake = probe(["/usr/share/dotnet/dotnet"]);

      const result = resolveDotnet({
        pathValue: "/usr/local/bin:/usr/share/dotnet:/opt/other",
        platform,
        isFile: fake.isFile
      });

      assert.equal(result, "/usr/share/dotnet/dotnet");
      assert.deepEqual(fake.probed, [
        "/usr/local/bin/dotnet",
        "/usr/share/dotnet/dotnet"
      ]);
    });
  }

  test("skips relative, empty and '.' entries even when isFile says true", () => {
    const fake = alwaysFile();

    const result = resolveDotnet({
      pathValue: [".", "", "bin", "./bin", "../up", "..", "tools/dotnet", " ", "~/bin"].join(":"),
      platform: "linux",
      isFile: fake.isFile
    });

    assert.equal(result, null);
    assert.deepEqual(fake.probed, []);
  });

  test("does not treat ';' as a delimiter", () => {
    const fake = probe();

    resolveDotnet({
      pathValue: "/a;/b:/c",
      platform: "linux",
      isFile: fake.isFile
    });

    assert.deepEqual(fake.probed, ["/a;/b/dotnet", "/c/dotnet"]);
  });

  test("does not strip quotes on posix, so a quoted entry stays relative and is skipped", () => {
    const fake = alwaysFile();

    const result = resolveDotnet({
      pathValue: '"/usr/bin"',
      platform: "linux",
      isFile: fake.isFile
    });

    assert.equal(result, null);
    assert.deepEqual(fake.probed, []);
  });

  test("never probes dotnet.exe", () => {
    const fake = probe(["/usr/bin/dotnet.exe"]);

    const result = resolveDotnet({
      pathValue: "/usr/bin",
      platform: "linux",
      isFile: fake.isFile
    });

    assert.equal(result, null);
    assert.deepEqual(fake.probed, ["/usr/bin/dotnet"]);
  });
});

describe("resolveDotnet dotnetRoot (DOTNET_ROOT)", () => {
  test("an absolute win32 root is probed before any PATH entry", () => {
    const fake = probe(["C:\\custom\\dotnet\\dotnet.exe", "C:\\a\\dotnet.exe"]);

    const result = resolveDotnet({
      pathValue: "C:\\a",
      dotnetRoot: "C:\\custom\\dotnet",
      platform: "win32",
      isFile: fake.isFile
    });

    assert.equal(result, "C:\\custom\\dotnet\\dotnet.exe");
    assert.deepEqual(fake.probed, ["C:\\custom\\dotnet\\dotnet.exe"]);
  });

  test("an absolute posix root is probed before any PATH entry", () => {
    const fake = probe(["/opt/dotnet/dotnet", "/usr/bin/dotnet"]);

    const result = resolveDotnet({
      pathValue: "/usr/bin",
      dotnetRoot: "/opt/dotnet",
      platform: "linux",
      isFile: fake.isFile
    });

    assert.equal(result, "/opt/dotnet/dotnet");
    assert.deepEqual(fake.probed, ["/opt/dotnet/dotnet"]);
  });

  test("falls through to PATH when the root has no dotnet", () => {
    const fake = probe(["/usr/bin/dotnet"]);

    const result = resolveDotnet({
      pathValue: "/usr/bin",
      dotnetRoot: "/opt/dotnet",
      platform: "linux",
      isFile: fake.isFile
    });

    assert.equal(result, "/usr/bin/dotnet");
    assert.deepEqual(fake.probed, ["/opt/dotnet/dotnet", "/usr/bin/dotnet"]);
  });

  test("a relative root is ignored (posix)", () => {
    const fake = alwaysFile();

    const result = resolveDotnet({
      pathValue: "/usr/bin",
      dotnetRoot: "tools/dotnet",
      platform: "linux",
      isFile: fake.isFile
    });

    assert.equal(result, "/usr/bin/dotnet");
    assert.deepEqual(fake.probed, ["/usr/bin/dotnet"]);
  });

  for (const relativeRoot of [".", "dotnet", "..\\dotnet", "C:dotnet", "\\dotnet", "/dotnet"]) {
    test(`a non-absolute win32 root is ignored: ${JSON.stringify(relativeRoot)}`, () => {
      const fake = alwaysFile();

      const result = resolveDotnet({
        pathValue: "C:\\a",
        dotnetRoot: relativeRoot,
        platform: "win32",
        isFile: fake.isFile
      });

      assert.equal(result, "C:\\a\\dotnet.exe");
      assert.deepEqual(fake.probed, ["C:\\a\\dotnet.exe"]);
    });
  }

  for (const emptyRoot of [undefined, null, "", "   "]) {
    test(`an empty root is ignored: ${JSON.stringify(emptyRoot)}`, () => {
      const fake = probe();

      resolveDotnet({
        pathValue: "/usr/bin",
        dotnetRoot: emptyRoot,
        platform: "linux",
        isFile: fake.isFile
      });

      assert.deepEqual(fake.probed, ["/usr/bin/dotnet"]);
    });
  }

  test("a relative root alone resolves nothing", () => {
    const fake = alwaysFile();

    const result = resolveDotnet({
      dotnetRoot: ".",
      platform: "linux",
      isFile: fake.isFile
    });

    assert.equal(result, null);
    assert.deepEqual(fake.probed, []);
  });
});

describe("resolveDotnet with nothing to find", () => {
  test("returns null when no candidate exists", () => {
    const fake = probe();

    const result = resolveDotnet({
      pathValue: "/usr/bin:/usr/local/bin",
      dotnetRoot: "/opt/dotnet",
      platform: "linux",
      isFile: fake.isFile
    });

    assert.equal(result, null);
    assert.equal(fake.probed.length, 3);
  });

  for (const pathValue of [undefined, null, "", "   ", ";;;", ":::"]) {
    test(`returns null without probing for PATH ${JSON.stringify(pathValue)}`, () => {
      for (const platform of ["win32", "linux"]) {
        const fake = alwaysFile();

        const result = resolveDotnet({ pathValue, platform, isFile: fake.isFile });

        assert.equal(result, null);
        assert.deepEqual(fake.probed, []);
      }
    });
  }

  test("throws instead of guessing when no isFile probe is supplied", () => {
    assert.throws(
      () => resolveDotnet({ pathValue: "/usr/bin", platform: "linux" }),
      TypeError
    );
  });
});

// Regression guard for the actual vulnerability (CWE-426): the launcher must
// resolve dotnet through the module and must never fall back to the bare name
// or hand the command to a shell. The full launcher cannot be run here because
// the bundled DLL only exists in release builds, so assert on its source.
describe("bin/gitsweep.js launcher wiring", () => {
  const source = fs.readFileSync(path.join(__dirname, "..", "bin", "gitsweep.js"), "utf8");

  test("uses resolve-dotnet.js with the process environment", () => {
    assert.match(source, /require\(\s*["']\.\/resolve-dotnet(\.js)?["']\s*\)/);
    assert.match(source, /process\.env\.PATH/);
    assert.match(source, /process\.env\.DOTNET_ROOT/);
    assert.match(source, /process\.platform/);
  });

  test("never spawns dotnet by bare name", () => {
    assert.doesNotMatch(source, /spawnSync\(\s*["'`]dotnet/);
    assert.doesNotMatch(source, /\|\|\s*["']dotnet["']/);
  });

  test("never enables a shell", () => {
    assert.doesNotMatch(source, /shell\s*:/);
  });
});
