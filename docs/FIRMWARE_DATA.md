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
characters become spaces, and whitespace is collapsed and trimmed. Parenthesized
qualifiers are removed for the Nano display only. Generated Nano-only names are capped at 40
characters before packing; authoritative SQLite descriptions remain unchanged. A
final post-cap collision pass adds a stable supported-character disambiguator derived
from RomName while remaining within 40 characters. ROM names remain in the model but
are not emitted to the Nano. Entries sort by group (`#`, then A--Z), normalized name,
and ordinal ROM name, so generation is deterministic and the Nano performs no sort.

## Alphabet and continuous packing

The exact code-zero-through-code-63 alphabet is:

```text
 ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-/.'&+:!?,()[]*=_%#@$<>;^~|
```

Each character's alphabet index is written least-significant-bit first into one
continuous six-bit stream. A name can therefore begin in the middle of a byte and
symbols can cross byte boundaries. `GENERATED_GAME_NAME_SYMBOL_OFFSETS` contains one
`uint16_t` six-bit-symbol index per game. Firmware multiplies it by six to obtain the
bit offset; a length is the difference between adjacent symbol indexes (or the total
bit count for the last game). There are no terminators, length bytes, or per-name
padding. Generation fails clearly if the complete stream exceeds 65,535 symbols;
this representation covers 49,152 packed bytes, beyond the Nano's flash capacity,
without incorrectly assuming that non-byte-aligned bit offsets fit in 16 bits.

## PROGMEM layout and size

The generated header contains the unchanged 1,280-byte direct profile table and
32-byte validity bitmap, followed by the packed name bytes, two offset bytes per
game, one ProfileId byte per game, and a 27-entry/two-byte (54-byte) jump table.
Thus the reported useful flash data is:

```text
1280 + 32 + ceil(totalNameCharacters * 6 / 8)
     + gameCount * 2 + gameCount + 54
```

The committed database currently has six profiles but no eligible assigned game rows,
so its useful generated-data total is **1,366 bytes**. The one-element placeholders
used to keep empty C++ arrays portable add four physical bytes until games are generated.
The Firmware tab reports each component; effective-assignment, Nano-selection,
Mahjong-exclusion, and generated counts; average normalized name length; longest name;
and total before writing. Only explicitly Nano-selected games are emitted. Mahjong is
matched as a case-insensitive standalone word in the desktop description and excluded
from export without changing catalogue or assignment state.

## Jump table and Nano browser

`GENERATED_ALPHABET_JUMPS` has 27 `uint16_t` entries: `#`, then A through Z. Each is
the first sorted game index in that group, or `GENERATED_GAME_COUNT` when empty. The
Nano reads it directly from flash. Encoder 1 jumps among non-empty groups, and encoder
2 wraps only among games in that group. Either short click resolves the internal
ProfileId, loads the generated geometry, and writes/verifies in one action. ProfileId
and geometry are hidden in browser mode. Either hold enters manual mode; there E1
selects a field, E2 edits, either click writes, and either hold restores the generated
profile and returns.

Exactly one selected title is shown. Characters 0--19 are decoded from PROGMEM into a
21-byte row buffer for LCD row 2, then characters 20--39 separately for row 3. Unused
cells are cleared, so short titles leave row 3 blank. Horizontal scrolling and the
permanent 41-byte title buffer have been removed.

## Nano pin allocation and backlight hardware

```text
LCD:       D12 RS, D11 E, D5 D4, D4 D5, D3 D6, D2 D7
I2C:       A4 SDA, A5 SCL
Encoder 1: A3 CLK, D10 DT, D9 SW
Encoder 2: D8 CLK, D7 DT, D6 SW
Free:      A0, A1, A2, D13
```

The free pins are deliberately not assigned to bus switching, IR, or backlight until
those circuits are finalized. The manual DPDT remains authoritative and the future
bus/reload abstraction points are no-ops.

Backlight state uses wrap-safe `millis()` timing independently of menus: 30 seconds
ordinary inactivity, 5 seconds after successful Apply, and the ordinary timeout after
failure. A first event while logically dark is consumed only to wake. No safe switched
backlight GPIO exists in the committed hardware, so physical blanking awaits:

```text
Nano GPIO -> transistor/MOSFET control -> LCD backlight
```

The existing LCD current limiting remains unchanged; the full backlight current must
not be driven directly by an ATmega328P GPIO. A finalized pin belongs only inside
`setBacklight(bool)`. PWM dimming is not implemented.

## Physical manual acceptance (not performed by automated tests)

Compile first and record `Sketch uses _____ bytes` of flash and `Global variables use
_____ bytes` of SRAM, comparing both with Phase 6; flash should grow with game data
while SRAM should change only by the small title buffer/state. On the Nano/LCD verify:

1. **Browser:** confirm exactly one title is shown, E1 changes non-empty alphabet
   groups, E2 stays within the group, and the candidate is always unambiguous.
2. **Apply:** select R-Type, switch the DPDT to Nano, and click either encoder once.
   Confirm that one click writes/verifies, no Enter click is needed, and R-Type remains
   selected when the browser is next rendered. Return the bus to TV and reload AV.
3. **Manual:** hold either encoder on R-Type, use E1 to select each geometry parameter,
   use E2 to edit, and click either button to write. Hold either button to return and
   confirm the generated R-Type values were restored.
4. **Backlight state:** because switched hardware is not yet committed, enable debug
   logging and verify logical OFF after 30 seconds, wake-only first movement/click,
   ordinary action on the next event, successful-Apply OFF after 5 seconds, and no
   NVRAM write from a dark-state wake click. Repeat physical light checks after the
   transistor/MOSFET stage is wired.
5. **Memory:** report `Sketch uses _____ bytes` flash and
   `Global variables use _____ bytes` SRAM from the Arduino compile output.
