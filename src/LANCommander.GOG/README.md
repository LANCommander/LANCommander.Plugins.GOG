# LANCommander.GOG

Reads GOG game installers and reports what they would install — files, registry entries,
shortcuts, and post-install commands — **without ever executing them**.

Every GOG installer is an [Inno Setup](https://jrsoftware.org/isinfo.php) package built with
GOG's `JR.Inno.Setup` fork. This library decodes that format as data, so it works on any
platform, needs no Windows, and leaves no side effects.

```csharp
using var package = GogInstaller.Open("setup_close_combat_2_2.0b_(17798).exe");

var manifest = package.Manifest;

manifest.AppName;            // "Close Combat 2"
manifest.ProductId;          // "1649233263"
manifest.InnoVersion;        // 5.5.7 (Unicode)
manifest.RegistryEntries;    // typed registry writes
manifest.RunEntries;         // commands the installer would run
manifest.Shortcuts;          // shortcuts it would create
manifest.Scripts.CompiledCode;   // the compiled Pascal script, captured not run

package.DecompileScript().ToPascal();   // and that script, read back as Pascal

foreach (var file in package.Files.Where(f => f.HasContent))
{
    file.DestinationPath;    // "{app}\cc2.exe"
    file.Size;
    file.Sha1Hex;

    using var stream = package.OpenRead(file);   // decompressed on demand
}

await package.ExtractAllAsync("/out");
```

`-N.bin` slices beside the installer are discovered automatically; pass only the `.exe`.

## What you get

| | |
|---|---|
| `Manifest.Files` | Every file, with destination path, size, SHA-1, timestamp, and where its bytes live |
| `Manifest.RegistryEntries` | Root key, subkey, value name, type, and data |
| `Manifest.RunEntries` | Install and uninstall commands, with arguments and working directory |
| `Manifest.Shortcuts` / `IniEntries` / `Directories` / `DeleteEntries` | The remaining declarative sections |
| `Manifest.Languages` / `SetupTypes` / `Components` / `Tasks` | What the wizard would offer |
| `Manifest.Scripts` | Licence and info text, plus the compiled script bytecode |
| `DecompileScript()` | The script decompiled: types, routines, and bodies, as Pascal or PowerShell |
| `Manifest.StagedFiles` | Where a content-addressed payload's blobs actually belong |
| `GogGameInfo` | GOG's `goggame-<id>.info`, including play tasks and the primary executable |

Inno constants such as `{app}` and `{tmp}` are left **unresolved**. Resolving them would
mean running the installer's script, which this library does not do.

### Reading the installer's script

Every GOG installer carries a compiled Pascal script — the `[Code]` section, built by
[RemObjects Pascal Script](https://www.remobjects.com/ps.aspx) and stored as IFPS bytecode.
It is decoded as data and rendered; nothing in it is ever run.

```csharp
var script = package.DecompileScript();

script.Bytecode.Types;           // the script's type table
script.Bytecode.Functions;       // its routines and the host functions it imports
script.Unit.StructuredFraction;  // how much of it reduced to loops and conditionals

File.WriteAllText("script.pas", script.ToPascal());
File.WriteAllText("script.ps1", script.ToPowerShell());
File.WriteAllText("LANCommander.Inno.psm1", InnoApi.ModuleText());
```

The PowerShell is a faithful translation, not a summary: every routine becomes a function and
the control flow keeps its shape. Inno's script API is provided by `LANCommander.Inno.psm1`,
which implements the mechanical part — files, the registry, INI files, strings, paths,
processes — and the emitted script generates a throwing stub for anything else it calls, so a
run that needs the installer itself stops there rather than quietly doing nothing.

### Content-addressed payloads

Some GOG builds stage every game file under `{tmp}` named after a hash and let the script
assemble the real tree afterwards. The mapping back is not lost: GOG's script takes the
destination as a parameter, and Inno stores a `BeforeInstall` hook's parameters as text on the
file entry, so `Manifest.StagedFiles` recovers it by reading rather than by interpreting.

```csharp
foreach (var file in package.Manifest.StagedFiles.Where(f => !f.IsDependency))
{
    file.DestinationPath;   // "Movies\Intro.mov", relative to {app}
    file.FileIndices;       // the payload entries that concatenate to it
}
```

### Reading GOG metadata

```csharp
var scripts = await package.LoadPayloadScriptsAsync();
var info = GogGameInfo.Parse(scripts.PayloadScripts.First(s => s.FileName.EndsWith(".info")).Content);

info.PrimaryPlayTask?.Path;                // "cc2.exe"
info.PrimaryPlayTask?.CompatibilityFlags;  // "NT4SP5 HIGHDPIAWARE"
```

### Scanning a library cheaply

`InstallerInspector.Identify` reads only the loader stub, so it is fast enough to sweep a
directory of multi-gigabyte installers.

```csharp
if (InstallerInspector.TryIdentify(path, out var identity))
    Console.WriteLine($"{identity.Version} — {(identity.UsesExternalSlices ? "sliced" : "embedded")}");
```

## Extraction

`ExtractAllAsync` walks entries in chunk order, so a whole-installer extract is one linear
pass over the payload. By default it writes only `{app}` files and verifies every file
against the SHA-1 recorded in the installer.

```csharp
await package.ExtractAllAsync("/out", new ExtractionOptions
{
    ApplicationFilesOnly = true,   // skip the installer's own {tmp} scratch files
    VerifyChecksums = true,        // fail loudly rather than write corrupt output
    Overwrite = false,
});
```

Anything not written is reported in `ExtractionResult.Skipped` with a reason — nothing is
dropped silently.

## Supported formats

Verified end to end against real installers, with every extracted file checked against its
recorded SHA-1:

| Installer | Inno | Payload | Result |
|---|---|---|---|
| Close Combat 2 | 5.5.7 (u) | embedded | 2,282 files / 778 MB |
| IL-2 Sturmovik 1946 | 5.5.7 (u) | 2 × `.bin` slices | 22,210 files / 8.45 GB |
| Rayman 2 | 5.5.7 (u) | embedded | 2,108 files / 515 MB |
| Soldiers: Heroes of WWII | 5.5.7 (u) | embedded | 252 files / 1.49 GB |
| The Wheel of Time | 5.6.2 (u) | embedded | 613 files / 1.03 GB |
| Close Combat (older build) | 5.5.0 (u) | embedded | 870 files / 417 MB |

Compression: LZMA1, LZMA2, zlib, and stored. Inno's x86 executable transform is reversed,
so extracted binaries are byte-identical to the originals.

## Limitations

- **Content-addressed payloads still extract under their hash names.** `ExtractAllAsync`
  writes what the payload holds. Use `Manifest.StagedFiles` to put the blobs where they
  belong; check `Manifest.UsesContentAddressedPayload` first.
- **Runtime decisions stay unresolved.** `{code:...}` values and `Check` conditions name a
  routine that the decompiled script now shows you, but evaluating one would mean running it.
- **About one routine in ten keeps its jumps.** Those routines come out complete and correct,
  with labels and `goto` where the control flow did not reduce; `ScriptRoutine.IsFullyStructured`
  says which. PowerShell has no `goto`, so those routines throw where the jump was — the Pascal
  output carries them in full.
- **Parameter and local names are positional.** The compiler discarded them. Routine names,
  type names, and global names survive because Pascal Script exports them.
- **Unicode Inno 5.5–5.6 only.** ANSI builds, Inno 6, bzip2 payloads, encrypted (password
  protected) payloads, and the RAR-wrapped payloads of some pre-2013 GOG installers are
  detected and rejected with a clear error rather than mis-parsed.

## Layout

```
src/LANCommander.GOG/         the library (net10.0)
  Inno/Loader/                loader stub, offset table, slice discovery
  Inno/Streams/               block and chunk decoding, codecs, executable transform
  Inno/Structs/               setup header record parsing
  Script/Ifps/                the compiled script container, read and written back
  Script/Analysis/            bytecode to expressions to structured control flow
  Script/Ast/                 the recovered script, in no particular language
  Script/Emit/                disassembly, Pascal, PowerShell, and the shim module
  Manifest/                   the public model
  Gog/                        GOG-specific enrichment, metadata, and staging layout
src/LANCommander.GOG.Cli/     a development tool (identify / info / files / gog / staged /
                              script / extract)
tests/LANCommander.GOG.Tests/ unit tests, plus integration tests over real installers
```

The only dependency is [SharpCompress](https://github.com/adamhathcock/sharpcompress) (MIT)
for LZMA; zlib comes from the base class library. The compiled-script reader is written
against the format as documented by `uPSRuntime.pas` and `uPSUtils.pas` in
[RemObjects Pascal Script](https://github.com/remobjects/pascalscript), cross-checked with
[IFPSTools.NET](https://github.com/Wack0/IFPSTools.NET); no code is taken from either.

## Development

```bash
dotnet build
dotnet test                                   # unit tests only
GOG_SAMPLE_DIR=/path/to/installers dotnet test  # adds the integration tests

dotnet run --project src/LANCommander.GOG.Cli -- info    setup_game.exe
dotnet run --project src/LANCommander.GOG.Cli -- gog     setup_game.exe
dotnet run --project src/LANCommander.GOG.Cli -- staged  setup_game.exe
dotnet run --project src/LANCommander.GOG.Cli -- extract setup_game.exe /out

dotnet run --project src/LANCommander.GOG.Cli -- script setup_game.exe \
    --verify --pascal script.pas --powershell ./ps
```

Install [PowerShell](https://github.com/PowerShell/PowerShell) to enable the tests that check
the emitted script parses; without it they are skipped.

```bash
dotnet tool install --global PowerShell
```

Integration tests are skipped unless `GOG_SAMPLE_DIR` names a directory containing the
installers; they are far too large to commit.

### Verifying a change

Two checks decide whether a change is right, and neither depends on judgement.

Checksum verification covers extraction. Every file location records a SHA-1 of the original
file, so a full extract with `VerifyChecksums` on will catch a mistake in chunk framing,
file offsets, codec selection, or the executable transform:

```bash
dotnet run --project src/LANCommander.GOG.Cli -- extract setup_game.exe /tmp/out --all
```

Round-tripping covers the script reader. There is no script source to compare a decompilation
against, but reading a compiled script and writing it back out must reproduce it byte for byte,
and every instruction must re-encode to the bytes it was read from. A field read at the wrong
width fails both:

```bash
dotnet run --project src/LANCommander.GOG.Cli -- script setup_game.exe --verify
```

## Format notes

Facts established by reading the sample installers, kept here because they are easy to get
wrong and expensive to rediscover:

- GOG's fork replaces the last six bytes of the loader signature, so only the `rDlPtS`
  prefix can be matched; candidates are validated structurally instead.
- The loader offset table for these builds has **no** `UncompressedSizeEXE` field.
  `Offset1 == 0` means the payload is in external slices.
- The setup header is **two** compressed blocks: the header and its entry arrays, then the
  fixed-size file location table. A block's stored size counts the per-4 KiB checksums as
  well as the compressed bytes.
- Slices are numbered from zero in the file table but from one on disk, so slice 0 is
  `-1.bin`. Chunk offsets are absolute within a `.bin`, but relative to the payload start
  when the payload is embedded in the executable.
- The x86 transform rewrites only the **low three bytes** of a `CALL`/`JMP` operand and
  inverts the high byte when bit 23 is set, applied to 64 KiB blocks independently. Using a
  different block size corrupts the instructions that straddle a boundary.
- An entry's two script hooks are stored **after-install first, then before-install**. Reading
  them the other way round is silent: every GOG entry then appears to call `after_install`
  before installing.
- The compiled script is a plain IFPS container — Inno adds no wrapper of its own, and hands
  the bytes straight to `TPSExec.LoadData`.
- The caller pushes a call's parameters in **reverse** declaration order, then the object for a
  method, then the result slot last. A method's object is a slot its declaration never mentions.
- A Pascal Script builtin that takes an untyped parameter — `Length`, `SetArrayLength`, `Inc`
  and the rest — stores a packed descriptor that **under-reports its arity**, because the
  compiler builds the descriptor from the declaration string and adds those parameters after.
- In an exported signature, `@` marks a parameter passed **in** and `!` one passed by
  reference. A `const` parameter is indistinguishable from a plain one.
