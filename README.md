# Map Validation Checker

A CLI tool that inspects Trackmania GBX map files (`.Map.Gbx`) and reports whether the Author Time looks "normally validated", "set by plugin", "validated via validation ghost", "supported by GPS", "supported by replay", or "manually overridden".

It supports:

* **Single file** mode (`--single`)
* **Batch folder** mode (`--batch`)
* Optional **replay evidence** matching (`--replays`)
* Optional **manual override JSON** (`--manual`)
* JSON output to stdout (and optionally file)
* Optional progress updates to stderr during long replay/map scans (counts, rate, ETA)

---

## What this tool checks

For each **map**:

### 1) Manual overrides (highest priority)

If the map UID exists in the manual JSON, the result becomes:

* `type: "manual"`
* `validated: "Yes"` if `valid: true`
* `validated: "Maybe"` if `valid: false`
* `note` is copied through

See: [Manual overrides](#manual-overrides)

### 2) Validation ghost (strong evidence)

If the map has a `RaceValidateGhost`:

* If `RaceValidateGhost.RaceTime == AuthorTime` → `validated: "Yes"`, `type: "validationghost"`
* If it exists but **does not match** → `error: "validation ghost time mismatch"`

### 3) Replay ↔ map matching (strong external evidence)

If replays are provided (`--replays`) and:

* Replay UID matches map UID, **and**
* Any replay ghost time equals map `AuthorTime` (milliseconds)

Then:

* `validated: "Yes"`, `type: "replay"`
* If `--include-path`, also outputs `replayPath`

See: [Replay matching](#replay-matching)

### 4) GPS ghost check (optional)

If GPS scan is enabled (default):

* If GPS record data contains a time within a tolerance of map `AuthorTime`:

  * `type: "gps"`
  * `validated: "Maybe"` by default
  * `validated: "Yes"` if `--strict-gps` is enabled

By default, GPS matching allows ±100 ms because GPS times are stored to the nearest tenth of a second. You can disable GPS scanning with `--no-gps` or change the tolerance with `--gps-threshold-ms`.

### 5) Script metadata validation (normal vs plugin suspicion)

Reads `Race_AuthorRaceWaypointTimes` from script metadata:

* If metadata finish time == `AuthorTime` **and** waypoint count matches checkpoint count → `validated: "Yes"`, `type: "normal"`
* Otherwise → `validated: "Maybe"`, `type: "plugin"`
* If metadata missing/unreadable → `validated: "Unknown"`, `type: "normal"`

---

## Output format

### Single mode

Outputs a **single JSON object**.

Example:

```json
{"uid":"abcd...","validated":"Yes","type":"normal"}
```

### Batch mode

Outputs a **JSON array**, one element per scanned file:

```json
[
  {"path":"C:/Maps/a.Map.Gbx","uid":"...","validated":"Yes","type":"normal"},
  {"path":"C:/Maps/readme.txt","error":"not a gbx file"}
]
```

### Fields

| Field        | Type                            | Description                                                      |
| ------------ | ------------------------------- | ---------------------------------------------------------------- |
| `uid`        | string                          | Map UID (when available)                                         |
| `validated`  | `"Yes" \| "Maybe" \| "Unknown"` | Status label                                                     |
| `type`       | string                          | `normal`, `plugin`, `validationghost`, `gps`, `replay`, `manual` |
| `note`       | string?                         | Optional note (manual / debug info)                              |
| `path`       | string?                         | Included if `--include-path`                                     |
| `mapName`    | string?                         | Map name (omit with `--no-map-name`)                             |
| `replayPath` | string?                         | Included if `--include-path` **and** replay matched              |
| `error`      | string?                         | Error message for that item                                      |

---

## CLI usage

### Basic

```bash
MapValidationChecker --single <mapFile>
MapValidationChecker --batch  <mapFolder>
```

### Full flag list

Required (exactly one):

* `--single <file>`
* `--batch <folder>`

Optional:

* `--replays <file-or-folder>`
  Provide a replay file or folder. Used to match replay ghost times against map `AuthorTime`.

* `--manual <file>`
  Manual overrides JSON (object or array). See format below.

* `--recursive`
  Recursively scan subfolders (affects both `--batch` map scanning and replay scanning).

* `--pretty`
  Pretty-print JSON.

* `--include-path`
  Include the `path` field for single too (and `replayPath` when matched).

* `--no-map-name`
  Omit the `mapName` field from JSON output.

* `--progress`
  Print periodic scan progress to stderr.

* `--progress-interval <sec>`
  Progress update interval in seconds (default: 5).

* `--output <file>`
  Write JSON output to file **in addition to stdout**.

GPS options:

* `--strict-gps`
  If GPS ghost time matches author time, mark as `validated: "Yes"` (instead of `"Maybe"`).

* `--no-gps`
  Disable GPS scanning entirely.

* `--gps-threshold-ms <ms>`
  GPS author time tolerance in milliseconds (default: 100). This exists because GPS times are stored to the nearest tenth of a second.

Traversal safety:

* `--max-depth <n>`
  Limit reflection traversal depth used.

Help:

* `--help`

---

<a id="manual-overrides"></a>

## Manual override JSON format (`--manual`)

The file can be **either a single object** or an **array of objects** with this structure:

```json
{ "valid": true, "uid": "SOMEUID", "note": "some note" }
```

Example (array):

```json
[
  { "valid": true,  "uid": "ABC123", "note": "Validated via video proof" },
  { "valid": false, "uid": "DEF456", "note": "Suspicious, keep flagged" }
]
```

Notes:

* `note` is optional
* The loader tolerates `True/False` by normalizing to `true/false` (handy if you accidentally wrote Python-style booleans)

---

<a id="replay-matching"></a>

## Replay matching (`--replays`)

Replay matching requires:

* Replay contains (or can derive) a map UID
* Replay contains one or more ghosts
* If **any** ghost race time (ms) equals the map’s `AuthorTime` (ms), the map gets:

  * `validated: "Yes"`
  * `type: "replay"`

This is intended as *supporting evidence* for author time correctness.

---

## Examples

### 1) Single map

```bash
MapValidationChecker --single "C:\Maps\MyMap.Map.Gbx"
```

### 2) Batch folder, recursive, pretty, include path

```bash
MapValidationChecker --batch "C:\Maps" --recursive --pretty --include-path
```

### 3) Batch maps + replay folder evidence

```bash
MapValidationChecker --batch "C:\Maps" --replays "C:\Replays" --recursive --pretty
```

### 4) Manual override file

```bash
MapValidationChecker --batch "C:\Maps" --manual "manual.json" --pretty
```

### 5) Write to disk as well

```bash
MapValidationChecker --batch "C:\Maps" --pretty --output "results.json"
```

### 6) Disable GPS scanning (speed up)

```bash
MapValidationChecker --batch "C:\Maps" --no-gps
```

### 7) Treat GPS match as validated (strict)

```bash
MapValidationChecker --single "C:\Maps\MyMap.Map.Gbx" --strict-gps
```

### 8) Limit GPS traversal depth

```bash
MapValidationChecker --batch "C:\Maps" --max-depth 8 --pretty
```

### 9) Customize GPS tolerance

```bash
MapValidationChecker --single "C:\Maps\MyMap.Map.Gbx" --gps-threshold-ms 150
```

---

## Build & publish (Windows)

### Prereqs

* Install **.NET SDK** (8+ recommended)

### Build

```bash
dotnet build -c Release
```

### Publish single-file EXE (self-contained)

```bash
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishTrimmed=false `
  -p:EnableCompressionInSingleFile=true `
  -p:AssemblyName=MapValidationChecker
```

Output EXE:

```
bin/Release/net*/win-x64/publish/MapValidationChecker.exe
```

---

## Known limitations / disclaimers

* This tool **cannot guarantee** legitimacy of an Author Time.
* A time can "look correct" while still being copied from a different map version.
* GPS ghosts can exist for reasons unrelated to validation (cutfixes, guides, etc.).
* Replays provide stronger evidence, but still don’t guarantee the replay was made on the exact same map version.
