# CSV interchange format (version 1)

CSV interchange is an editable backup of **application-owned configuration**, not a
database replacement. SQLite remains authoritative during normal operation. An export
is one ordinary ZIP file containing five UTF-8, RFC 4180 CSV files. Cells are not JSON,
and commas, quotes, Unicode, and embedded newlines in notes are supported.

The archive and every row order are deterministic. ZIP entry timestamps are fixed;
there is no export-time field. Profiles sort by ID; calibration history sorts by
signature then calibration ID; mappings sort by signature; assignments sort by ROM
shortname case-insensitively (with its ordinal spelling as the tie-breaker).

## Files and columns

`metadata.csv` has one row and one integer column, `FormatVersion`. Version 1 is the
only version accepted in Phase 5.

`profiles.csv` contains `Id,HSH,VSL,VAM,VSC,VSH,Notes`. ID is an integer from 1 through
255 and is never renumbered. Each geometry value is an integer from 0 through 63.
`Notes` is optional: an empty cell restores a null note. For example:

```csv
Id,HSH,VSL,VAM,VSC,VSH,Notes
17,33,11,30,13,63,"R-Type, measured on the cabinet"
```

`calibrations.csv` contains
`CalibrationId,ProfileId,SourceRomName,Width,Height,Rotation,RefreshMicroHz,CreatedAtUtc`.
All historical calibration events are retained, including their stable positive IDs
and original round-trip UTC timestamp. `SourceRomName`, not a mutable description, is
the provenance key. Width/height and refresh are positive integers, rotation is the
canonical 0–359 integer, and refresh is the exact integer microhertz value—not display
text or floating point Hz.

`mappings.csv` contains
`Width,Height,Rotation,RefreshMicroHz,ProfileId,CalibrationId`. Each canonical signature
is unique. Its calibration must exist in `calibrations.csv` and have exactly the same
profile and signature. This preserves the active mapping independently of history.

`assignments.csv` contains
`RomName,ProfileId,AssignmentType,Width,Height,Rotation,RefreshMicroHz,UpdatedAtUtc`.
`AssignmentType` is exactly `Automatic` or `Manual`. Automatic assignments require all
four canonical signature fields. Manual assignments require those cells to be empty.
Rows are restored verbatim rather than regenerated from the current catalogue, so a
manual override remains manual and the exported automatic state remains reproducible.

## Validation and MAME dependency

The complete archive is parsed and validated before the Apply button is enabled. The
preview reports file/row counts, inserts, updates, unresolved ROM names, and detailed
errors. Validation detects missing files or headers, malformed CSV/integers/timestamps,
duplicate IDs/signatures/ROM assignments, range errors, invalid assignment types,
missing profile/calibration references, inconsistent active mappings, and malformed
signatures.

Profiles can be imported without a MAME catalogue when all three MAME-related files
contain only headers. Otherwise every calibration source and assignment ROM must exist
in `MameMachines`. The same rule reports missing ROMs whether the catalogue is absent
or merely does not contain that shortname; references are never discarded. Import the
appropriate `mame.xml`, then validate again. The imported catalogue, displays, and MAME
import history are deliberately neither exported nor modified by CSV import.

## Apply modes and transactions

**Merge** upserts every supplied profile by ID, calibration by calibration ID, mapping
by canonical signature, and assignment by ROM shortname. Application-owned rows not
mentioned remain unchanged. The preview labels each incoming row as an insert or
update count.

**Replace user configuration** deletes assignments, mappings, calibration history, and
profiles in dependency order, then recreates precisely the validated CSV state. It does
not delete or replace MAME machines, displays, or import history.

Both modes use one SQLite transaction with foreign keys enabled. Data is inserted in
profile, calibration, mapping, assignment order. A database error rolls back the whole
operation. Validation is a preview, so a catalogue change between preview and Apply can
still cause an FK error; that error is safely rolled back rather than partially applied.
