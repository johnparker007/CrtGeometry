/*
    CRT Geometry Controller
    =======================

    Arduino Nano / ATmega328P
    HD44780 20x4 LCD
    3 rotary encoders
    ST M24C16W EEPROM

    Memory optimised:
      - Debug strings use F()
      - Generated geometry profiles stay in PROGMEM
      - Only the selected profile is copied into working SRAM

    ------------------------------------------------------------
    UI
    ------------------------------------------------------------

    GAME BROWSER

      Encoder 1 turn: jump #/A-Z group
      Encoder 2 turn: browse within group
      Encoder 3 turn: horizontally scroll long title

      Any encoder click:
        Load selected profile into editable working geometry

    GEOMETRY EDITOR

      Encoder 2 turn:
        Select HSH/VSL/VAM/VSC/VSH

      Encoder 3 turn:
        Adjust selected parameter

      ANY encoder click:
        WRITE currently displayed geometry to NVRAM

      ANY encoder hold:
        BACK to profile selector and restore generated values

    ------------------------------------------------------------
    ROTARY DECODING
    ------------------------------------------------------------

    Uses full quadrature decoding rather than sampling DT on a
    single CLK edge.

    This rejects invalid/bouncing transitions and accumulates a
    complete encoder detent before reporting +/-1.

    ------------------------------------------------------------
    EEPROM MAP
    ------------------------------------------------------------

      HSH   0x0103
      VSL   0x0109
      VAM   0x010A
      VSC   0x010B
      VSH   0x010C
      CHECK 0x01AD

    Encodings:

      HSH raw = service-menu HSH - 38
      VSL raw = UI value
      VAM raw = UI value - 4
      VSC raw = UI value
      VSH raw = UI value + 2

    ------------------------------------------------------------
    HSH FIRMWARE BUG
    ------------------------------------------------------------

    TV appears to apply approximately +7 to HSH when reloading
    NVRAM.

    Presets therefore contain the DESIRED effective HSH.

    Example desired HSH = 33:

      33 - 7  = service-menu-equivalent 26
      26 - 38 = raw -12 = 0xF4

    ------------------------------------------------------------
    DEBUG
    ------------------------------------------------------------

    Set DEBUG_LOGGING to 0 for final standalone use if wanted.
*/

#define DEBUG_LOGGING 0

#include <Wire.h>
#include <LiquidCrystal.h>
#include <avr/pgmspace.h>
#include "GeneratedDatabase.h"


// ============================================================
// GENERAL CONFIGURATION
// ============================================================

const unsigned long SERIAL_BAUD_RATE = 115200;

// Empirically observed HSH reload error.
const int8_t HSH_RELOAD_OFFSET = 7;


// ============================================================
// ROTARY CONFIGURATION
// ============================================================

/*
    Most common mechanical encoders produce four valid quadrature
    transitions per physical detent.

    If this version later requires TWO physical clicks for one
    displayed step, change this from 4 to 2.
*/
const int8_t ROTARY_TRANSITIONS_PER_DETENT = 4;

const unsigned long BUTTON_DEBOUNCE_MS = 30;
const unsigned long BACK_HOLD_MS       = 800;


// ============================================================
// LCD
// ============================================================

// RS, E, D4, D5, D6, D7
LiquidCrystal lcd(12, 11, 5, 4, 3, 2);


// ============================================================
// ENCODER PINS
// ============================================================

// Encoder 1 - main navigation
const uint8_t ENC1_CLK = A3;
const uint8_t ENC1_DT  = 10;
const uint8_t ENC1_SW  = 9;

// Encoder 2 - parameter selection
const uint8_t ENC2_CLK = 8;
const uint8_t ENC2_DT  = 7;
const uint8_t ENC2_SW  = 6;

// Encoder 3 - geometry adjustment
const uint8_t ENC3_CLK = A0;
const uint8_t ENC3_DT  = A1;
const uint8_t ENC3_SW  = A2;


// ============================================================
// M24C16 EEPROM ADDRESSES
// ============================================================

const uint16_t ADDR_HSH      = 0x0103;
const uint16_t ADDR_VSL      = 0x0109;
const uint16_t ADDR_VAM      = 0x010A;
const uint16_t ADDR_VSC      = 0x010B;
const uint16_t ADDR_VSH      = 0x010C;
const uint16_t ADDR_CHECKSUM = 0x01AD;


// ============================================================
// GEOMETRY
// ============================================================

enum GeometryParameter
{
    PARAM_HSH = 0,
    PARAM_VSL,
    PARAM_VAM,
    PARAM_VSC,
    PARAM_VSH,

    PARAM_COUNT
};


struct Geometry
{
    uint8_t hsh;
    uint8_t vsl;
    uint8_t vam;
    uint8_t vsc;
    uint8_t vsh;
};


struct RawGeometry
{
    uint8_t hsh;
    uint8_t vsl;
    uint8_t vam;
    uint8_t vsc;
    uint8_t vsh;
    uint8_t checksum;
};


// ============================================================
// MENU STATE / WORKING GEOMETRY
// ============================================================

enum MenuLevel
{
    MENU_GAME_BROWSER = 0,
    MENU_GEOMETRY
};

MenuLevel menuLevel = MENU_GAME_BROWSER;
uint8_t selectedProfileId = 0;
uint16_t selectedGameIndex = 0;
uint8_t selectedAlphabetGroup = 0;
uint16_t gameNameScroll = 0;

Geometry currentGeometry = { 33, 11, 30, 13, 63 };
GeometryParameter selectedParameter = PARAM_HSH;

// ============================================================
// ROTARY ENCODER STATE
// ============================================================

enum ButtonEvent
{
    BUTTON_NONE = 0,
    BUTTON_CLICK,
    BUTTON_LONG_PRESS
};


struct RotaryData
{
    uint8_t pinCLK;
    uint8_t pinDT;
    uint8_t pinSW;

    // Previous 2-bit quadrature state.
    uint8_t previousAB;

    // Accumulates valid quadrature transitions.
    int8_t transitionAccumulator;

    // Push-button debounce state.
    bool lastButtonReading;
    bool stableButtonState;

    unsigned long lastDebounceTime;
    unsigned long pressStartTime;

    bool longPressFired;
};


RotaryData encoder1;
RotaryData encoder2;
RotaryData encoder3;

/*
    Arduino's sketch preprocessor inserts function prototypes near
    the first function definition. Keep explicit declarations for
    functions that use sketch-defined rotary types so generated
    prototypes can never appear before RotaryData/ButtonEvent.
*/
uint8_t readRotaryAB(const RotaryData& rotary);
void setupRotary(RotaryData& rotary, uint8_t pinCLK, uint8_t pinDT, uint8_t pinSW);
int8_t updateRotaryRotation(RotaryData& rotary);
ButtonEvent updateRotaryButton(RotaryData& rotary);


// ============================================================
// GENERATED PROFILE DATABASE
// ============================================================

bool generatedProfileExists(uint8_t profileId)
{
    if (profileId == 0)
        return false;

    uint8_t validityByte = (uint8_t)pgm_read_byte(
        &GENERATED_PROFILE_VALIDITY[profileId >> 3]);

    return (validityByte & (uint8_t)(1U << (profileId & 7))) != 0;
}

bool loadGeneratedProfile(uint8_t profileId, Geometry& result)
{
    if (!generatedProfileExists(profileId))
        return false;

    GeneratedGeometryProfile generated;
    memcpy_P(&generated, &GENERATED_PROFILES[profileId], sizeof(generated));
    result.hsh = generated.hsh;
    result.vsl = generated.vsl;
    result.vam = generated.vam;
    result.vsc = generated.vsc;
    result.vsh = generated.vsh;
    return true;
}

uint8_t generatedGameProfileId(uint16_t index)
{
    return index < GENERATED_GAME_COUNT ? pgm_read_byte(&GENERATED_GAME_PROFILE_IDS[index]) : 0;
}

uint32_t generatedGameNameBitOffset(uint16_t index)
{
    if (index >= GENERATED_GAME_COUNT) return GENERATED_TOTAL_NAME_BITS;
    return pgm_read_dword(&GENERATED_GAME_NAME_BIT_OFFSETS[index]);
}

uint16_t generatedGameNameLength(uint16_t index)
{
    if (index >= GENERATED_GAME_COUNT) return 0;
    uint32_t end = index + 1 < GENERATED_GAME_COUNT
        ? generatedGameNameBitOffset(index + 1) : GENERATED_TOTAL_NAME_BITS;
    return (uint16_t)((end - generatedGameNameBitOffset(index)) / 6);
}

char generatedNameCharacter(uint32_t bitOffset)
{
    uint16_t byteIndex = (uint16_t)(bitOffset >> 3);
    uint8_t shift = bitOffset & 7;
    uint16_t bits = pgm_read_byte(&GENERATED_GAME_NAME_BITS[byteIndex]);
    if (shift > 2) bits |= (uint16_t)pgm_read_byte(&GENERATED_GAME_NAME_BITS[byteIndex + 1]) << 8;
    uint8_t symbol = (bits >> shift) & 0x3F;
    return (char)pgm_read_byte(&GENERATED_NAME_ALPHABET[symbol]);
}

void decodeGeneratedGameName(uint16_t index, char* output, uint8_t capacity)
{
    if (capacity == 0) return;
    uint16_t length = generatedGameNameLength(index);
    uint32_t bit = generatedGameNameBitOffset(index);
    uint8_t count = length < capacity - 1 ? length : capacity - 1;
    for (uint8_t i = 0; i < count; ++i, bit += 6) output[i] = generatedNameCharacter(bit);
    output[count] = '\0';
}

void decodeGeneratedGameNameWindow(uint16_t index, uint16_t start, char* output, uint8_t capacity)
{
    if (capacity == 0) return;
    uint16_t length = generatedGameNameLength(index);
    if (start >= length) start = 0;
    uint8_t count = length - start < capacity - 1 ? length - start : capacity - 1;
    uint32_t bit = generatedGameNameBitOffset(index) + (uint32_t)start * 6;
    for (uint8_t i = 0; i < count; ++i, bit += 6) output[i] = generatedNameCharacter(bit);
    output[count] = '\0';
}

// ============================================================
// QUADRATURE DECODER TABLE
// ============================================================

/*
    Index:

      previousAB << 2 | currentAB

    Values:

       0 = no valid movement
      +1 = one valid quadrature transition
      -1 = one valid quadrature transition opposite direction

    Invalid transitions caused by contact bounce are ignored.
*/

const int8_t ROTARY_TRANSITION_TABLE[16] PROGMEM =
{
     0, -1,  1,  0,
     1,  0,  0, -1,
    -1,  0,  0,  1,
     0,  1, -1,  0
};


// ============================================================
// GENERAL UTILITIES
// ============================================================

uint8_t clampGeometryValue(int value)
{
    if (value < 0)
        return 0;

    if (value > 63)
        return 63;

    return (uint8_t)value;
}


// ============================================================
// DEBUG HELPERS
// ============================================================

#if DEBUG_LOGGING

void debugPrintHexByte(uint8_t value)
{
    if (value < 0x10)
        Serial.print('0');

    Serial.print(value, HEX);
}


void debugPrintHexAddress(uint16_t value)
{
    Serial.print(F("0x"));

    if (value < 0x1000)
        Serial.print('0');

    if (value < 0x0100)
        Serial.print('0');

    if (value < 0x0010)
        Serial.print('0');

    Serial.print(value, HEX);
}

#endif


// ============================================================
// LCD HELPERS
// ============================================================

void clearRestOfLine(uint8_t charsAlreadyPrinted)
{
    while (charsAlreadyPrinted < 20)
    {
        lcd.print(' ');
        ++charsAlreadyPrinted;
    }
}


void lcdPrintLine(
    uint8_t row,
    const char* text)
{
    lcd.setCursor(0, row);

    uint8_t count = 0;

    while (*text && count < 20)
    {
        lcd.print(*text++);
        ++count;
    }

    clearRestOfLine(count);
}


void lcdPrintLineF(
    uint8_t row,
    const __FlashStringHelper* text)
{
    // Clear row first.
    lcd.setCursor(0, row);

    for (uint8_t i = 0; i < 20; ++i)
        lcd.print(' ');

    // Then print flash-backed text.
    lcd.setCursor(0, row);
    lcd.print(text);
}


// ============================================================
// GENERATED PROFILE SELECTION / LCD UI
// ============================================================

uint16_t alphabetGroupStart(uint8_t group)
{
    return pgm_read_word(&GENERATED_ALPHABET_JUMPS[group]);
}

uint16_t alphabetGroupEnd(uint8_t group)
{
    for (uint8_t next = group + 1; next < 27; ++next)
    {
        uint16_t start = alphabetGroupStart(next);
        if (start < GENERATED_GAME_COUNT) return start;
    }
    return GENERATED_GAME_COUNT;
}

void selectAlphabetGroup(int8_t movement)
{
    if (GENERATED_GAME_COUNT == 0) return;
    for (uint8_t checked = 0; checked < 27; ++checked)
    {
        selectedAlphabetGroup = movement > 0
            ? (selectedAlphabetGroup == 26 ? 0 : selectedAlphabetGroup + 1)
            : (selectedAlphabetGroup == 0 ? 26 : selectedAlphabetGroup - 1);
        uint16_t start = alphabetGroupStart(selectedAlphabetGroup);
        if (start < GENERATED_GAME_COUNT && start < alphabetGroupEnd(selectedAlphabetGroup))
        {
            selectedGameIndex = start;
            gameNameScroll = 0;
            renderUI();
            return;
        }
    }
}

void moveGame(int16_t movement)
{
    if (GENERATED_GAME_COUNT == 0) return;
    int32_t next = (int32_t)selectedGameIndex + movement;
    if (next < 0) next = 0;
    if (next >= GENERATED_GAME_COUNT) next = GENERATED_GAME_COUNT - 1;
    selectedGameIndex = (uint16_t)next;
    gameNameScroll = 0;
    for (uint8_t group = 0; group < 27; ++group)
        if (alphabetGroupStart(group) <= selectedGameIndex && selectedGameIndex < alphabetGroupEnd(group))
            selectedAlphabetGroup = group;
    renderUI();
}

void printGeometryRows(bool showSelection)
{
    char line[21];
    snprintf(line, sizeof(line), "%cHSH%02u %cVSL%02u %cVAM%02u",
        showSelection && selectedParameter == PARAM_HSH ? '*' : ' ', currentGeometry.hsh,
        showSelection && selectedParameter == PARAM_VSL ? '*' : ' ', currentGeometry.vsl,
        showSelection && selectedParameter == PARAM_VAM ? '*' : ' ', currentGeometry.vam);
    lcdPrintLine(2, line);
    snprintf(line, sizeof(line), "%cVSC%02u %cVSH%02u",
        showSelection && selectedParameter == PARAM_VSC ? '*' : ' ', currentGeometry.vsc,
        showSelection && selectedParameter == PARAM_VSH ? '*' : ' ', currentGeometry.vsh);
    lcdPrintLine(3, line);
}

void renderGameBrowser()
{
    if (GENERATED_GAME_COUNT == 0)
    {
        lcdPrintLineF(0, F("No assigned games")); lcdPrintLineF(1, F("Generate database"));
        lcdPrintLineF(2, F("from desktop app")); lcdPrintLineF(3, F("")); return;
    }
    char line[41];
    char group = selectedAlphabetGroup == 0 ? '#' : 'A' + selectedAlphabetGroup - 1;
    snprintf(line, sizeof(line), "Group %c  Game %u/%u", group, selectedGameIndex + 1, GENERATED_GAME_COUNT);
    lcdPrintLine(0, line);
    decodeGeneratedGameNameWindow(selectedGameIndex, gameNameScroll, line, 21); lcdPrintLine(1, line);
    snprintf(line, sizeof(line), "Profile %03u", generatedGameProfileId(selectedGameIndex)); lcdPrintLine(2, line);
    lcdPrintLineF(3, F("E1=ABC E2=GAME E3=>"));
}

void renderGeometryEditor()
{
    char line[41]; decodeGeneratedGameName(selectedGameIndex, line, sizeof(line)); lcdPrintLine(0, line);
    snprintf(line, sizeof(line), "P%03u Click=WRITE", selectedProfileId); lcdPrintLine(1, line);
    printGeometryRows(true);
}

void renderUI()
{
    if (menuLevel == MENU_GAME_BROWSER) renderGameBrowser(); else renderGeometryEditor();
}

// ============================================================
// GEOMETRY ENCODING
// ============================================================

uint8_t encodeHSH(
    uint8_t desiredHSH)
{
    int serviceMenuEquivalent =
        (int)desiredHSH -
        HSH_RELOAD_OFFSET;

    int raw =
        serviceMenuEquivalent - 38;

    return (uint8_t)raw;
}


uint8_t decodeHSH(
    uint8_t raw)
{
    int8_t signedRaw =
        (int8_t)raw;

    int serviceMenuEquivalent =
        (int)signedRaw + 38;

    int effectiveValue =
        serviceMenuEquivalent +
        HSH_RELOAD_OFFSET;

    return clampGeometryValue(
        effectiveValue);
}


uint8_t encodeVSL(uint8_t value)
{
    return value;
}


uint8_t decodeVSL(uint8_t raw)
{
    return clampGeometryValue(raw);
}


uint8_t encodeVAM(uint8_t value)
{
    return (uint8_t)(value - 4);
}


uint8_t decodeVAM(uint8_t raw)
{
    return clampGeometryValue(
        (int)raw + 4);
}


uint8_t encodeVSC(uint8_t value)
{
    return value;
}


uint8_t decodeVSC(uint8_t raw)
{
    return clampGeometryValue(raw);
}


uint8_t encodeVSH(uint8_t value)
{
    return (uint8_t)(value + 2);
}


uint8_t decodeVSH(uint8_t raw)
{
    return clampGeometryValue(
        (int)raw - 2);
}


Geometry decodeGeometry(
    const RawGeometry& raw)
{
    Geometry result;

    result.hsh = decodeHSH(raw.hsh);
    result.vsl = decodeVSL(raw.vsl);
    result.vam = decodeVAM(raw.vam);
    result.vsc = decodeVSC(raw.vsc);
    result.vsh = decodeVSH(raw.vsh);

    return result;
}


RawGeometry encodeGeometry(
    const Geometry& geometry,
    uint8_t checksum)
{
    RawGeometry result;

    result.hsh = encodeHSH(geometry.hsh);
    result.vsl = encodeVSL(geometry.vsl);
    result.vam = encodeVAM(geometry.vam);
    result.vsc = encodeVSC(geometry.vsc);
    result.vsh = encodeVSH(geometry.vsh);

    result.checksum = checksum;

    return result;
}


// ============================================================
// M24C16 SUPPORT
// ============================================================

uint8_t get24C16DeviceAddress(
    uint16_t memoryAddress)
{
    uint8_t block =
        (memoryAddress >> 8) &
        0x07;

    return 0x50 | block;
}


bool waitForEepromReady(
    uint8_t i2cAddress)
{
    unsigned long start =
        millis();

    while ((millis() - start) < 50)
    {
        Wire.beginTransmission(
            i2cAddress);

        if (Wire.endTransmission() == 0)
            return true;

        delay(1);
    }

    return false;
}


bool eepromWriteByte24C16(
    uint16_t memoryAddress,
    uint8_t data)
{
    uint8_t i2cAddress =
        get24C16DeviceAddress(
            memoryAddress);

    uint8_t wordAddress =
        memoryAddress & 0xFF;


    Wire.beginTransmission(
        i2cAddress);

    Wire.write(wordAddress);
    Wire.write(data);


    uint8_t result =
        Wire.endTransmission();


    if (result != 0)
        return false;


    return waitForEepromReady(
        i2cAddress);
}


bool eepromReadByte24C16(
    uint16_t memoryAddress,
    uint8_t& result)
{
    uint8_t i2cAddress =
        get24C16DeviceAddress(
            memoryAddress);

    uint8_t wordAddress =
        memoryAddress & 0xFF;


    Wire.beginTransmission(
        i2cAddress);

    Wire.write(wordAddress);


    uint8_t status =
        Wire.endTransmission(false);


    if (status != 0)
        return false;


    uint8_t count =
        Wire.requestFrom(
            i2cAddress,
            (uint8_t)1);


    if (count != 1 ||
        !Wire.available())
    {
        return false;
    }


    result = Wire.read();

    return true;
}


// ============================================================
// RAW GEOMETRY READ
// ============================================================

bool readRawGeometry(
    RawGeometry& raw)
{
    if (!eepromReadByte24C16(
            ADDR_HSH,
            raw.hsh))
        return false;

    if (!eepromReadByte24C16(
            ADDR_VSL,
            raw.vsl))
        return false;

    if (!eepromReadByte24C16(
            ADDR_VAM,
            raw.vam))
        return false;

    if (!eepromReadByte24C16(
            ADDR_VSC,
            raw.vsc))
        return false;

    if (!eepromReadByte24C16(
            ADDR_VSH,
            raw.vsh))
        return false;

    if (!eepromReadByte24C16(
            ADDR_CHECKSUM,
            raw.checksum))
        return false;

    return true;
}


// ============================================================
// DEBUG OUTPUT
// ============================================================

#if DEBUG_LOGGING

void debugPrintRawGeometry(
    const RawGeometry& raw)
{
    Serial.println();
    Serial.println(
        F("Raw NVRAM geometry:"));


    Serial.print(
        F("HSH  0x0103 = 0x"));

    debugPrintHexByte(raw.hsh);
    Serial.println();


    Serial.print(
        F("VSL  0x0109 = 0x"));

    debugPrintHexByte(raw.vsl);
    Serial.println();


    Serial.print(
        F("VAM  0x010A = 0x"));

    debugPrintHexByte(raw.vam);
    Serial.println();


    Serial.print(
        F("VSC  0x010B = 0x"));

    debugPrintHexByte(raw.vsc);
    Serial.println();


    Serial.print(
        F("VSH  0x010C = 0x"));

    debugPrintHexByte(raw.vsh);
    Serial.println();


    Serial.print(
        F("CHK  0x01AD = 0x"));

    debugPrintHexByte(raw.checksum);
    Serial.println();
}


void debugPrintGeometry(
    const Geometry& geometry)
{
    Serial.println(
        F("Effective geometry:"));


    Serial.print(F("HSH = "));
    Serial.println(geometry.hsh);

    Serial.print(F("VSL = "));
    Serial.println(geometry.vsl);

    Serial.print(F("VAM = "));
    Serial.println(geometry.vam);

    Serial.print(F("VSC = "));
    Serial.println(geometry.vsc);

    Serial.print(F("VSH = "));
    Serial.println(geometry.vsh);

    Serial.println();
}

#endif


// ============================================================
// CHECKSUM
// ============================================================

uint8_t calculateAdjustedChecksum(
    const RawGeometry& oldRaw,
    const RawGeometry& newRaw)
{
    uint8_t checksum =
        oldRaw.checksum;


    checksum =
        (uint8_t)(
            checksum +
            (uint8_t)(
                newRaw.hsh -
                oldRaw.hsh));


    checksum =
        (uint8_t)(
            checksum +
            (uint8_t)(
                newRaw.vsl -
                oldRaw.vsl));


    checksum =
        (uint8_t)(
            checksum +
            (uint8_t)(
                newRaw.vam -
                oldRaw.vam));


    checksum =
        (uint8_t)(
            checksum +
            (uint8_t)(
                newRaw.vsc -
                oldRaw.vsc));


    checksum =
        (uint8_t)(
            checksum +
            (uint8_t)(
                newRaw.vsh -
                oldRaw.vsh));


    return checksum;
}


// ============================================================
// WRITE / VERIFY HELPERS
// ============================================================

bool writeIfChanged(
    uint16_t address,
    uint8_t oldValue,
    uint8_t newValue)
{
    if (oldValue == newValue)
        return true;


#if DEBUG_LOGGING

    Serial.print(F("Write "));
    debugPrintHexAddress(address);

    Serial.print(F(": 0x"));
    debugPrintHexByte(oldValue);

    Serial.print(F(" -> 0x"));
    debugPrintHexByte(newValue);

    Serial.println();

#endif


    return eepromWriteByte24C16(
        address,
        newValue);
}


bool verifyByte(
    uint16_t address,
    uint8_t expected)
{
    uint8_t actual;


    if (!eepromReadByte24C16(
            address,
            actual))
    {
        return false;
    }


#if DEBUG_LOGGING

    Serial.print(F("Verify "));
    debugPrintHexAddress(address);

    Serial.print(F(" expected 0x"));
    debugPrintHexByte(expected);

    Serial.print(F(" got 0x"));
    debugPrintHexByte(actual);

    if (actual == expected)
        Serial.println(F(" OK"));
    else
        Serial.println(F(" FAIL"));

#endif


    return actual == expected;
}


// ============================================================
// WRITE CURRENT GEOMETRY
// ============================================================

bool writeCurrentGeometry()
{
    RawGeometry oldRaw;


    if (!readRawGeometry(oldRaw))
    {
#if DEBUG_LOGGING

        Serial.println(
            F("WRITE FAILED: NVRAM read error."));

#endif

        lcdPrintLineF(
            0,
            F("WRITE FAILED"));

        lcdPrintLineF(
            1,
            F("NVRAM not found"));

        lcdPrintLineF(
            2,
            F("Switch bus to Nano"));

        lcdPrintLineF(
            3,
            F(""));


        delay(1500);

        renderUI();

        return false;
    }


    RawGeometry newRaw =
        encodeGeometry(
            currentGeometry,
            oldRaw.checksum);


    newRaw.checksum =
        calculateAdjustedChecksum(
            oldRaw,
            newRaw);


#if DEBUG_LOGGING

    Serial.println();
    Serial.println(
        F("===================="));

    Serial.println(
        F("WRITE REQUEST"));

    Serial.println(
        F("===================="));


    debugPrintGeometry(
        currentGeometry);


    Serial.print(F("HSH raw: "));
    debugPrintHexByte(oldRaw.hsh);
    Serial.print(F(" -> "));
    debugPrintHexByte(newRaw.hsh);
    Serial.println();


    Serial.print(F("VSL raw: "));
    debugPrintHexByte(oldRaw.vsl);
    Serial.print(F(" -> "));
    debugPrintHexByte(newRaw.vsl);
    Serial.println();


    Serial.print(F("VAM raw: "));
    debugPrintHexByte(oldRaw.vam);
    Serial.print(F(" -> "));
    debugPrintHexByte(newRaw.vam);
    Serial.println();


    Serial.print(F("VSC raw: "));
    debugPrintHexByte(oldRaw.vsc);
    Serial.print(F(" -> "));
    debugPrintHexByte(newRaw.vsc);
    Serial.println();


    Serial.print(F("VSH raw: "));
    debugPrintHexByte(oldRaw.vsh);
    Serial.print(F(" -> "));
    debugPrintHexByte(newRaw.vsh);
    Serial.println();


    Serial.print(F("CHECK: "));
    debugPrintHexByte(oldRaw.checksum);
    Serial.print(F(" -> "));
    debugPrintHexByte(newRaw.checksum);
    Serial.println();

#endif


    lcdPrintLineF(
        0,
        F("WRITING NVRAM..."));

    lcdPrintLineF(1, F(""));
    lcdPrintLineF(2, F(""));
    lcdPrintLineF(3, F(""));


    if (!writeIfChanged(
            ADDR_HSH,
            oldRaw.hsh,
            newRaw.hsh))
        goto writeFailed;


    if (!writeIfChanged(
            ADDR_VSL,
            oldRaw.vsl,
            newRaw.vsl))
        goto writeFailed;


    if (!writeIfChanged(
            ADDR_VAM,
            oldRaw.vam,
            newRaw.vam))
        goto writeFailed;


    if (!writeIfChanged(
            ADDR_VSC,
            oldRaw.vsc,
            newRaw.vsc))
        goto writeFailed;


    if (!writeIfChanged(
            ADDR_VSH,
            oldRaw.vsh,
            newRaw.vsh))
        goto writeFailed;


    // Check byte last.
    if (!writeIfChanged(
            ADDR_CHECKSUM,
            oldRaw.checksum,
            newRaw.checksum))
        goto writeFailed;


    // Verify all six bytes.

    if (!verifyByte(
            ADDR_HSH,
            newRaw.hsh))
        goto verifyFailed;


    if (!verifyByte(
            ADDR_VSL,
            newRaw.vsl))
        goto verifyFailed;


    if (!verifyByte(
            ADDR_VAM,
            newRaw.vam))
        goto verifyFailed;


    if (!verifyByte(
            ADDR_VSC,
            newRaw.vsc))
        goto verifyFailed;


    if (!verifyByte(
            ADDR_VSH,
            newRaw.vsh))
        goto verifyFailed;


    if (!verifyByte(
            ADDR_CHECKSUM,
            newRaw.checksum))
        goto verifyFailed;


#if DEBUG_LOGGING

    Serial.println();
    Serial.println(
        F("WRITE VERIFIED OK"));
    Serial.println();

#endif


    lcdPrintLineF(
        0,
        F("WRITE VERIFIED OK"));

    lcdPrintLineF(
        1,
        F(""));

    lcdPrintLineF(
        2,
        F("Switch I2C -> TV"));

    lcdPrintLineF(
        3,
        F("Then reload AV"));


    delay(1500);

    renderUI();

    return true;


writeFailed:

#if DEBUG_LOGGING

    Serial.println(
        F("ERROR: EEPROM write failed."));

#endif

    lcdPrintLineF(
        0,
        F("WRITE FAILED"));

    lcdPrintLineF(
        1,
        F("I2C error"));

    lcdPrintLineF(
        2,
        F("Check bus switch"));

    lcdPrintLineF(
        3,
        F(""));


    delay(2000);

    renderUI();

    return false;


verifyFailed:

#if DEBUG_LOGGING

    Serial.println(
        F("ERROR: EEPROM verify failed."));

#endif

    lcdPrintLineF(
        0,
        F("VERIFY FAILED"));

    lcdPrintLineF(
        1,
        F("Do not trust write"));

    lcdPrintLineF(
        2,
        F("Check connection"));

    lcdPrintLineF(
        3,
        F(""));


    delay(2000);

    renderUI();

    return false;
}


// ============================================================
// MANUAL GEOMETRY ADJUSTMENT
// ============================================================

uint8_t* getSelectedParameterPointer()
{
    switch (selectedParameter)
    {
        case PARAM_HSH:
            return &currentGeometry.hsh;

        case PARAM_VSL:
            return &currentGeometry.vsl;

        case PARAM_VAM:
            return &currentGeometry.vam;

        case PARAM_VSC:
            return &currentGeometry.vsc;

        case PARAM_VSH:
            return &currentGeometry.vsh;

        default:
            return &currentGeometry.hsh;
    }
}


void adjustSelectedParameter(
    int delta)
{
    uint8_t* value =
        getSelectedParameterPointer();


    int newValue =
        (int)(*value) +
        delta;


    *value =
        clampGeometryValue(
            newValue);


#if DEBUG_LOGGING

    Serial.print(
        F("Manual adjustment: "));


    switch (selectedParameter)
    {
        case PARAM_HSH:
            Serial.print(F("HSH"));
            break;

        case PARAM_VSL:
            Serial.print(F("VSL"));
            break;

        case PARAM_VAM:
            Serial.print(F("VAM"));
            break;

        case PARAM_VSC:
            Serial.print(F("VSC"));
            break;

        case PARAM_VSH:
            Serial.print(F("VSH"));
            break;

        default:
            break;
    }


    Serial.print(F(" = "));
    Serial.println(*value);

#endif


    renderUI();
}


// ============================================================
// ROTARY SETUP
// ============================================================

uint8_t readRotaryAB(
    const RotaryData& rotary)
{
    uint8_t a =
        digitalRead(rotary.pinCLK)
            ? 1
            : 0;

    uint8_t b =
        digitalRead(rotary.pinDT)
            ? 1
            : 0;


    return (a << 1) | b;
}


void setupRotary(
    RotaryData& rotary,
    uint8_t pinCLK,
    uint8_t pinDT,
    uint8_t pinSW)
{
    rotary.pinCLK = pinCLK;
    rotary.pinDT  = pinDT;
    rotary.pinSW  = pinSW;


    pinMode(
        pinCLK,
        INPUT_PULLUP);

    pinMode(
        pinDT,
        INPUT_PULLUP);

    pinMode(
        pinSW,
        INPUT_PULLUP);


    rotary.previousAB =
        readRotaryAB(rotary);


    rotary.transitionAccumulator = 0;


    rotary.lastButtonReading =
        digitalRead(pinSW);


    rotary.stableButtonState =
        rotary.lastButtonReading;


    rotary.lastDebounceTime = 0;
    rotary.pressStartTime   = 0;
    rotary.longPressFired   = false;
}


// ============================================================
// ROBUST QUADRATURE ROTARY DECODING
// ============================================================

int8_t updateRotaryRotation(
    RotaryData& rotary)
{
    uint8_t currentAB =
        readRotaryAB(rotary);


    // Nothing changed.
    if (currentAB ==
        rotary.previousAB)
    {
        return 0;
    }


    uint8_t tableIndex =
        (rotary.previousAB << 2) |
        currentAB;


    int8_t transition =
        (int8_t)pgm_read_byte(
            &ROTARY_TRANSITION_TABLE[
                tableIndex]);


    rotary.previousAB =
        currentAB;


    /*
        Invalid/bouncing two-bit jumps produce zero and therefore
        don't change the accumulator.
    */

    rotary.transitionAccumulator +=
        transition;


    if (rotary.transitionAccumulator >=
        ROTARY_TRANSITIONS_PER_DETENT)
    {
        rotary.transitionAccumulator = 0;

        return 1;
    }


    if (rotary.transitionAccumulator <=
        -ROTARY_TRANSITIONS_PER_DETENT)
    {
        rotary.transitionAccumulator = 0;

        return -1;
    }


    return 0;
}


// ============================================================
// ROTARY BUTTON
// ============================================================

ButtonEvent updateRotaryButton(
    RotaryData& rotary)
{
    bool reading =
        digitalRead(
            rotary.pinSW);


    ButtonEvent event =
        BUTTON_NONE;


    if (reading !=
        rotary.lastButtonReading)
    {
        rotary.lastDebounceTime =
            millis();
    }


    if ((millis() -
         rotary.lastDebounceTime)
        > BUTTON_DEBOUNCE_MS)
    {
        if (reading !=
            rotary.stableButtonState)
        {
            rotary.stableButtonState =
                reading;


            // Button pressed.
            if (reading == LOW)
            {
                rotary.pressStartTime =
                    millis();

                rotary.longPressFired =
                    false;
            }

            // Button released.
            else
            {
                /*
                    Only generate click if this wasn't already
                    recognised as a long hold.
                */
                if (!rotary.longPressFired)
                {
                    event =
                        BUTTON_CLICK;
                }
            }
        }
    }


    // Detect hold while button remains pressed.
    if (rotary.stableButtonState == LOW &&
        !rotary.longPressFired)
    {
        if ((millis() -
             rotary.pressStartTime)
            >= BACK_HOLD_MS)
        {
            rotary.longPressFired =
                true;

            event =
                BUTTON_LONG_PRESS;
        }
    }


    rotary.lastButtonReading =
        reading;


    return event;
}


// ============================================================
// PROFILE NAVIGATION / BUTTON ACTIONS
// ============================================================

void handleClick()
{
    if (menuLevel == MENU_GAME_BROWSER)
    {
        uint8_t profileId = generatedGameProfileId(selectedGameIndex);
        if (profileId != 0 && loadGeneratedProfile(profileId, currentGeometry))
        {
            selectedProfileId = profileId;
            menuLevel = MENU_GEOMETRY;
            renderUI();
        }
    }
    else writeCurrentGeometry();
}

void handleLongPress()
{
    if (menuLevel == MENU_GEOMETRY)
    {
        menuLevel = MENU_GAME_BROWSER;
        loadGeneratedProfile(generatedGameProfileId(selectedGameIndex), currentGeometry);
        renderUI();
    }
}

// ============================================================
// ENCODER HANDLING
// ============================================================

void updateEncoders()
{
    // --------------------------------------------------------
    // Encoder 1
    // Generated profile browsing
    // --------------------------------------------------------

    int8_t movement1 =
        updateRotaryRotation(
            encoder1);


    if (movement1 != 0)
    {
        if (menuLevel == MENU_GAME_BROWSER)
            selectAlphabetGroup(movement1);
    }


    // --------------------------------------------------------
    // Encoder 2
    // Parameter selection
    // --------------------------------------------------------

    int8_t movement2 =
        updateRotaryRotation(
            encoder2);


    if (movement2 != 0 && menuLevel == MENU_GAME_BROWSER)
    {
        moveGame(movement2);
    }
    else if (movement2 != 0 &&
        menuLevel == MENU_GEOMETRY)
    {
        int next =
            (int)selectedParameter +
            movement2;


        if (next < 0)
            next =
                PARAM_COUNT - 1;


        if (next >= PARAM_COUNT)
            next = 0;


        selectedParameter =
            (GeometryParameter)next;


#if DEBUG_LOGGING

        Serial.print(
            F("Selected parameter: "));


        switch (selectedParameter)
        {
            case PARAM_HSH:
                Serial.println(F("HSH"));
                break;

            case PARAM_VSL:
                Serial.println(F("VSL"));
                break;

            case PARAM_VAM:
                Serial.println(F("VAM"));
                break;

            case PARAM_VSC:
                Serial.println(F("VSC"));
                break;

            case PARAM_VSH:
                Serial.println(F("VSH"));
                break;

            default:
                break;
        }

#endif


        renderUI();
    }


    // --------------------------------------------------------
    // Encoder 3
    // Geometry adjustment
    // --------------------------------------------------------

    int8_t movement3 =
        updateRotaryRotation(
            encoder3);


    if (movement3 != 0 && menuLevel == MENU_GAME_BROWSER)
    {
        uint16_t length = generatedGameNameLength(selectedGameIndex);
        int32_t next = (int32_t)gameNameScroll + movement3;
        uint16_t maximum = length > 20 ? length - 20 : 0;
        if (next < 0) next = 0;
        if (next > maximum) next = maximum;
        gameNameScroll = (uint16_t)next;
        renderUI();
    }
    else if (movement3 != 0 &&
        menuLevel == MENU_GEOMETRY)
    {
        adjustSelectedParameter(
            movement3);
    }


    // --------------------------------------------------------
    // Buttons
    //
    // All three buttons have identical meaning:
    //
    // click = enter editor OR write geometry
    // hold  = return to profile selector
    // --------------------------------------------------------

    ButtonEvent button1 =
        updateRotaryButton(
            encoder1);

    ButtonEvent button2 =
        updateRotaryButton(
            encoder2);

    ButtonEvent button3 =
        updateRotaryButton(
            encoder3);


    /*
        Process long holds first.

        If two buttons somehow generate events simultaneously,
        BACK has priority over WRITE.
    */

    if (button1 == BUTTON_LONG_PRESS ||
        button2 == BUTTON_LONG_PRESS ||
        button3 == BUTTON_LONG_PRESS)
    {
        handleLongPress();

        return;
    }


    if (button1 == BUTTON_CLICK ||
        button2 == BUTTON_CLICK ||
        button3 == BUTTON_CLICK)
    {
        handleClick();
    }
}


// ============================================================
// SETUP
// ============================================================

void setup()
{
#if DEBUG_LOGGING

    Serial.begin(
        SERIAL_BAUD_RATE);


    delay(100);


    Serial.println();

    Serial.println(
        F("CRT Geometry Controller"));

    Serial.println(
        F("======================="));

    Serial.println();

#endif


    lcd.begin(
        20,
        4);


    lcdPrintLineF(
        0,
        F("CRT Geometry Tool"));

    lcdPrintLineF(
        1,
        F("Starting..."));

    lcdPrintLineF(2, F(""));
    lcdPrintLineF(3, F(""));


    setupRotary(
        encoder1,
        ENC1_CLK,
        ENC1_DT,
        ENC1_SW);


    setupRotary(
        encoder2,
        ENC2_CLK,
        ENC2_DT,
        ENC2_SW);


    setupRotary(
        encoder3,
        ENC3_CLK,
        ENC3_DT,
        ENC3_SW);


    Wire.begin();


    if (GENERATED_GAME_COUNT != 0)
    {
        selectedGameIndex = 0;
        for (uint8_t group = 0; group < 27; ++group)
            if (alphabetGroupStart(group) == 0 && alphabetGroupEnd(group) > 0)
                selectedAlphabetGroup = group;
        selectedProfileId = generatedGameProfileId(0);
        loadGeneratedProfile(selectedProfileId, currentGeometry);
    }


    delay(300);


    renderUI();


#if DEBUG_LOGGING

    Serial.println(
        F("Controls:"));

    Serial.println(
        F(" E1 turn  = alphabet group"));

    Serial.println(
        F(" E2 turn  = geometry parameter"));

    Serial.println(
        F(" E3 turn  = geometry value"));

    Serial.println(
        F(" Any click = edit/write"));

    Serial.println(
        F(" Any hold  = game browser"));

    Serial.println();

#endif
}


// ============================================================
// MAIN LOOP
// ============================================================

void loop()
{
    updateEncoders();
}
