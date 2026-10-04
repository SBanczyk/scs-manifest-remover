# scs-manifest-remover

Hides `manifest.sii` (or any other file) inside an Euro Truck Simulator 2 / American Truck Simulator `.scs` archive in HashFS format, without extracting or repacking it. Useful when a mod is packed so that extractors fail on it, but you still need the game to stop seeing one file in order to load a mod.

## Usage

Drag one or more `.scs` files onto `ScsManifestRemover.exe`, or run it from a terminal:

```
ScsManifestRemover.exe "some_mod.scs"
```

Before changing anything the tool copies the original to `<file>.backup`. An existing backup is never overwritten, so running the tool twice keeps the untouched original. To undo, delete the patched file and remove `.backup` from the copy's name.

| Option | Meaning |
|---|---|
| `-p`, `--path <path>` | File to hide, relative to the archive root. Default: `manifest.sii` |
| `-n`, `--dry-run` | Report what would change without writing anything |
| `--no-backup` | Do not create `<file>.backup` |
| `--no-pause` | Do not wait for a key press when started by double-click or drag and drop |

Exit codes: `0` file hidden (or found, in a dry run), `1` file not found, `2` error. With several files the highest code is returned.

## How it works

A HashFS archive does not look files up by name. It keeps a table of entries sorted by the CityHash64 of each path, and the game finds a file by hashing the path it wants and searching that table. The tool computes the hash of `manifest.sii`, finds the matching entry and changes its stored hash to the next value up. The table stays sorted, every other entry and all file data stay byte-for-byte the same, and a lookup for `manifest.sii` no longer finds anything.

Supported formats:

- **HashFS v1**: the table is uncompressed and is patched in place (8 bytes change). If the header points to a bogus offset, a common locking trick, the table is read from the end of the file instead.
- **HashFS v2** (packer from game version 1.50 onwards): the table is zlib-compressed. The tool decompresses it in memory, patches it, recompresses it and checks that the result decompresses to the same bytes before writing. The new table goes back in its original place if it fits; otherwise it is appended to the end of the file and the header is updated to point to it.
- If the archive header sets a salt, both the salted and the plain hash of the path are tried.

ZIP-based `.scs` files are rejected; they store file names in plain text and can be edited with ordinary tools.

## Limitations

- The file's bytes remain in the archive. Only the table entry pointing to them is changed.
- The root directory listing inside the archive still names `manifest.sii`. The game may log a warning about it in `game.log.txt`.
- A mod without a manifest shows up unnamed and without an icon in the in-game mod manager.
- The format handling follows TruckLib.HashFs. Archives that deviate from it in ways the game tolerates but the tool does not expect may report `NOT FOUND` or an error; in both cases the file is left unchanged.

## Building

Requires the .NET SDK (8 or later). The project targets .NET Framework 4.8, which is built into Windows 10 and 11, so the resulting exe needs no separate runtime.

```
dotnet build src/ScsManifestRemover/ScsManifestRemover.csproj -c Release -o out
```

GitHub Actions builds the exe on every push to `main` and attaches it to a GitHub release when a tag such as `v1.0.0` is pushed.

## Credits

- CityHash64 implementation and HashFS format details: [TruckLib.HashFs](https://github.com/sk-zk/TruckLib.HashFs) by sk-zk (MIT).
- CityHash by Geoff Pike and Jyrki Alakuijala (Google), C port by Alexander Nusov, C# port by Dario Wouters.
- Anthropic Claude
