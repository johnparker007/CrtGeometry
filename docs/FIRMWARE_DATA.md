# Generated firmware profile data

## Ownership and output

The desktop application generates `firmware/CrtGeometryController/GeneratedDatabase.h`
from the `GeometryProfiles` rows in its authoritative SQLite database. **The generated
header must not be manually edited.** Handwritten Arduino behavior remains in
`CrtGeometryController.ino`; the generator never searches or rewrites that sketch.
The header is committed so firmware builds are reproducible and profile changes are
reviewable. CSV remains optional interchange only and is not a firmware-generation
source.

The Firmware / Arduino tab lets the user select the firmware directory. The output
filename is always `GeneratedDatabase.h`. A repository-relative directory is offered
when discoverable, but no checkout-specific absolute path is embedded. Generation is
written to a temporary file beside the destination and then moved over the destination,
so validation or write failures do not truncate a known-good header.

## AVR representation

`GeneratedGeometryProfile` is exactly five `uint8_t` fields in this order:

```text
HSH, VSL, VAM, VSC, VSH
```

Every value is validated as 0–63 and every ID as 1–255. The generated
`GENERATED_PROFILES[256]` is a direct-index table: profile ID is the array index.
Index 0 is an explicit all-zero invalid entry, and unused indexes are zero filled.
Zero values do not imply absence because an all-zero profile is legitimate.

Presence is represented independently by `GENERATED_PROFILE_VALIDITY`, a 32-byte
bitmap. Bit `id & 7` of byte `id >> 3` is set exactly when that profile exists. Bit 0
is always clear. The header also exposes `GENERATED_PROFILE_COUNT`,
`GENERATED_MAX_PROFILE_ID`, and `GENERATED_PROFILE_VALIDITY_BYTES`.

Both arrays use AVR `PROGMEM`; the 1,280-byte profile table and 32-byte bitmap consume
an estimated **1,312 bytes of flash and no table-sized SRAM allocation**. The sketch's
`generatedProfileExists(uint8_t)` reads bitmap bytes with `pgm_read_byte`, while
`loadGeneratedProfile(uint8_t, Geometry&)` uses `memcpy_P` for one five-byte record and
copies it into mutable working geometry. The rest of the UI does not access raw
PROGMEM. Temporary manual edits therefore never modify generated data, and returning
to the selector restores the selected generated profile.

## Determinism and validation

Profiles are loaded from SQLite, validated for IDs, geometry ranges, and duplicate
IDs, and sorted by numeric ID. All 256 slots and all bitmap bytes are emitted in fixed
order with LF line endings and UTF-8 without a byte-order mark. No timestamp, machine
path, notes, or other volatile data is included. Identical SQLite profile state thus
produces byte-for-byte identical output. Validation completes before any destination
file is touched.

Phase 6 deliberately contains geometry profiles only. Phase 7 may add compact game
names, game-to-profile mappings, and browsing indexes without changing the direct
profile lookup contract documented here.
