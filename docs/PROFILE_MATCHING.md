# Calibration and profile matching

## Canonical video signature

Phase 4 treats a video mode as the tuple **active width, active height, normalized
rotation, and refresh in integer microhertz**. Rotation is normalized into 0–359
degrees. Refresh is parsed numerically from MAME and rounded to the nearest
microhertz (six decimal places), with midpoint values rounded away from zero. Thus
`60.6060611` and `60.6060614` match, while `60.606063` does not match
`60.606061`. Display text is never compared.

This deliberately narrow initial heuristic does not discard richer data. PixelClock,
HTotal, HBStart, HBEnd, VTotal, VBStart, VBEnd, and RawAttributesJson remain in
`MameDisplays` and are shown in game details so later hardware evidence can refine
the signature.

## Primary display rule

An automatic signature is available only when a machine has exactly one raster
display (an absent type remains raster-compatible for old MAME XML) and that display
has width, height, rotation, and a finite positive refresh. No raster display, missing
fields, or multiple raster displays produces no signature. Multiple raster displays
are explicitly reported as ambiguous; Phase 4 does not guess that display zero is
primary. Such a game can still receive a manual profile assignment.

## Calibration, propagation, and reuse

The calibration screen reuses catalogue search: description, ROM shortname,
manufacturer, and year. A selected title supplies its stable ROM shortname without
requiring the user to know it. The preview finds currently present, included games
with the same canonical signature. Confirmation records a calibration event, makes
that event the active mapping for the signature, and assigns its profile automatically
to those games.

Profiles are value objects for reuse: if HSH, VSL, VAM, VSC, and VSH exactly equal an
existing profile, the lowest-ID identical profile is reused. Otherwise the normal
lowest-free ID in 1–255 is allocated. Profiles without a MAME calibration remain
supported.

Each calibration event retains source ROM, profile, signature, and timestamp. The
source title is joined from the current MAME catalogue rather than copied. Historical
events remain stored; the signature mapping points to the newest confirmed event.
Recalibrating a signature deterministically replaces its one active mapping and all
of its automatic assignments.

## Manual override precedence and import ownership

A manual assignment is stored distinctly from an automatic assignment. Propagation
uses a conditional upsert and never replaces a manual row. Resetting an override
removes it and restores the current automatic signature mapping when one exists;
otherwise the game becomes unassigned.

MAME import owns catalogue fields and display rows only. It upserts machines by ROM
shortname and never deletes profile assignments, signature mappings, calibration
events, profile notes, or profiles. Machines absent from a later import are marked
absent, and automatic propagation considers only currently present, included games.

Profile deletion is blocked by SQLite foreign keys while any assignment,
calibration, or active signature mapping references it. Remove those references
before deleting the profile.
