# MAME XML import

## Supported XML

Phase 2 imports both the older `<game>` representation (including the expected MAME
0.139-era output) and the newer `<machine>` representation. The root `build`, `debug`,
and `mameconfig` values are recorded as opaque source metadata; importing does not
depend on parsing a semantic version. Missing optional elements and attributes are
accepted. Entries without a `name` cannot be identified and are skipped.

The importer uses a forward-only `XmlReader` and a subtree reader for one machine at
a time. It never constructs an `XDocument` or holds the catalogue in memory. Each
parsed machine is filtered and written immediately inside one SQLite transaction.
This supports large `-listxml` files while ensuring malformed XML, cancellation, or a
database error rolls back the complete attempted import.

## Retained data

The machine shortname (`RomName`) is the stable key. Imported fields are description,
year, manufacturer, `cloneof`, runnable/BIOS/device/mechanical flags, and nullable coin
count. Every display is retained in source order. Display columns cover type, active
width and height, rotation, refresh as a SQLite `REAL`, pixel clock, and horizontal and
vertical total/blank start/blank end timing. A JSON copy of **all** display attributes
is also retained so useful attributes from an older or newer schema are not discarded.
Refresh is parsed invariantly as a number (for example `60.606061`), not as formatted UI
text. No Phase 4 matching assumptions are made.

Each successful import records its source filename, UTC import time, duration, source
metadata, and summary counts. These are MAME-owned fields. Profile/calibration fields
are deliberately not introduced or modified in this phase.

## Initial filtering policy

Filtering is a separate `MameFilterPolicy`. All applicable reasons are stored as flags,
so an entry can be diagnosed rather than reduced to one arbitrary primary reason:

- BIOS, device, mechanical, or explicitly non-runnable;
- no display;
- displays are all explicitly non-raster (an absent display type remains eligible for
  compatibility with old XML);
- coin count is explicitly zero.

Coin input greater than zero is a strong positive arcade signal. Because some old or
unusual machines omit coin metadata, a missing coin attribute is treated as unknown,
not zero. This heuristic intentionally cannot distinguish every casino/slot/video-poker
machine, and may include unusual non-arcade systems or exclude valid coinless titles.
All excluded entries and their raw classification data remain available for refining
the policy later.

## Reimport behavior

Reimport is an upsert keyed by shortname. MAME-owned machine fields and displays are
replaced, avoiding duplicates and stale displays. A successful generation marks entries
not present in that XML as absent rather than deleting them. This is reversible and
leaves room for future user-owned profile assignments. A failed generation is rolled
back, including its import history record, and the previous catalogue remains intact.

## UI and limitations

The **MAME Import** tab selects an XML file, runs parsing/database work on a background
thread, displays periodic machine-count progress, and reports build, totals, display
coverage, duration, and exclusion counts. Phase 2 intentionally provides no searchable
games catalogue, category database, profile assignment, or video-mode matching.
