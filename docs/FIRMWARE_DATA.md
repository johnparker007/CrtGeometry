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

# Phase 7 compact game database

## Eligibility and assignment

A generated game is a `MameMachines` row that is present, included, non-clone
(`CloneOf` is null or empty), and has
an effective `GameProfileAssignments` row. That table already embodies Phase 4's
precedence rule: a manual assignment replaces/takes precedence over the automatic
video-signature assignment. Unassigned, absent, and excluded machines are omitted.
Clones remain searchable and manually assignable on the desktop, but even manually
assigned clones are not emitted. This intentionally keeps Nano eligibility simple
and its flash use predictable. Generation rejects duplicate ROM keys, profile
IDs outside 1--255, missing referenced profiles, unusable descriptions, excess game
counts, and offset overflow before atomically replacing the header.

## Display-name normalization and ordering

The MAME Description, never the ROM shortname, is the title source. Unicode is
canonically decomposed, combining marks are removed (for example `é` becomes `E`),
letters are upper-cased, smart apostrophes and Unicode dashes become ASCII, unsupported
characters become spaces, and whitespace is collapsed and trimmed. Qualifiers are
preserved rather than guessed away. Names are not truncated to the LCD width. If two
normalized descriptions collide, every colliding entry receives a visible, stable
` [NORMALIZED-ROMNAME]` suffix. ROM names remain in the desktop generation model but
are not emitted to the Nano. Entries sort by group (`#`, then A--Z), normalized name,
and ordinal ROM name, so generation is deterministic and the Nano performs no sort.

## Alphabet and continuous packing

The exact code-zero-through-code-63 alphabet is:

```text
 ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-/.'&+:!?,()[]*=_%#@$<>;^~|
```

Each character's alphabet index is written least-significant-bit first into one
continuous six-bit stream. A name can therefore begin in the middle of a byte and
symbols can cross byte boundaries. `GENERATED_GAME_NAME_BIT_OFFSETS` contains one
`uint32_t` bit offset per game; a length is `(nextOffset-currentOffset)/6`, or
`(GENERATED_TOTAL_NAME_BITS-currentOffset)/6` for the last game. There are no
terminators, length bytes, or per-name padding. A 32-bit offset was selected
intentionally: 16 bits hold only 10,922 characters and are insufficient for the
required 1,500-game stress case. The checked 32-bit format represents over 715 million
characters; game indexes/counts are checked `uint16_t`.

## PROGMEM layout and size

The generated header contains the unchanged 1,280-byte direct profile table and
32-byte validity bitmap, followed by the packed name bytes, four offset bytes per
game, one ProfileId byte per game, and a 27-entry/two-byte (54-byte) jump table.
Thus the reported useful flash data is:

```text
1280 + 32 + ceil(totalNameCharacters * 6 / 8)
     + gameCount * 4 + gameCount + 54
```

The committed database currently has six profiles but no eligible assigned game rows,
so its useful generated-data total is **1,366 bytes**. The one-element placeholders
used to keep empty C++ arrays portable add six physical bytes until games are generated.
The Firmware tab reports each component, game/profile counts, average normalized name
length, longest name, and total before writing.

## Jump table and Nano browser

`GENERATED_ALPHABET_JUMPS` has 27 `uint16_t` entries: `#`, then A through Z. Each is
the first sorted game index in that group, or `GENERATED_GAME_COUNT` when empty. The
Nano reads it directly from flash. Encoder 1 jumps among non-empty groups, encoder 2
moves one game, and encoder 3 deliberately scrolls the current title horizontally so
long titles and collision suffixes can always be revealed. A click
validates and loads the selected game's one-byte ProfileId and copies its five-byte
profile to mutable `currentGeometry`; selection never writes NVRAM. In the editor,
encoder 2 selects a geometry field, encoder 3 edits it, any click writes, and any long
hold returns to the browser. Reselecting reloads immutable generated values.

Only the currently shown title is decoded from PROGMEM into a 41-byte fixed buffer.
The complete database is never copied to the Nano's 2 KB SRAM. Titles longer than 20
characters remain stored; encoder 3 moves the displayed 20-character window without
flicker or automatic timing.

## Physical manual acceptance (not performed by automated tests)

Compile first and record `Sketch uses _____ bytes` of flash and `Global variables use
_____ bytes` of SRAM, comparing both with Phase 6; flash should grow with game data
while SRAM should change only by the small title buffer/state. On the Nano/LCD verify:

1. Boot opens the game browser with uncorrupted text.
2. Group jump, one-game browsing, title scrolling, and all encoders remain reliable;
   finding R-Type is practical.
3. R-Type loads HSH 33, VSL 11, VAM 30, VSC 13, VSH 63 without writing immediately.
4. Edit a value, hold Back, and reselect; the generated value must be restored.
5. With the DPDT switched to Nano, click Write; return the bus to the TV and force a
   source/channel reload, then verify the expected geometry.
6. If two generated games share a profile, verify both load identical geometry.
7. Report whether navigation remains practical at hundreds of games.
