namespace CrtGeometry.Tests;

public sealed class FirmwareSketchTests
{
    // Git may check the sketch out with CRLF on Windows. Normalize it before
    // making the deliberately line-oriented structural assertions below.
    private static readonly string Sketch = File.ReadAllText(FindRepositoryFile(
        "firmware", "CrtGeometryController", "CrtGeometryController.ino"))
        .ReplaceLineEndings("\n");

    [Fact]
    public void UsesTwoEncodersAndKeepsGameMovementInsideSelectedGroup()
    {
        Assert.DoesNotContain("ENC3_", Sketch);
        Assert.DoesNotContain("encoder3", Sketch);
        Assert.Contains("uint16_t start = alphabetGroupStart(selectedAlphabetGroup);", Sketch);
        Assert.Contains("if (next < start) next = end - 1;", Sketch);
        Assert.Contains("if (next >= end) next = start;", Sketch);
    }

    [Fact]
    public void BrowserClickLoadsGeneratedProfileAndWritesInOneAction()
    {
        Assert.Contains("uint8_t profileId = generatedGameProfileId(selectedGameIndex);", Sketch);
        Assert.Contains("!loadGeneratedProfile(profileId, currentGeometry)", Sketch);
        Assert.Contains("bool success = writeCurrentGeometry();", Sketch);
        Assert.Contains("if (button1 == BUTTON_CLICK || button2 == BUTTON_CLICK)", Sketch);
    }

    [Fact]
    public void ManualModeLoadsAndRestoresImmutableGeneratedGeometry()
    {
        Assert.Contains("menuLevel = MENU_GEOMETRY;", Sketch);
        Assert.Contains("loadGeneratedProfile(profileId, currentGeometry)", Sketch);
        Assert.Contains("Exit manual geometry; generated profile restored", Sketch);
    }

    [Fact]
    public void BrowserRendersOneTitleAcrossTwoRowsWithoutProfileIdOrScrolling()
    {
        Assert.Contains("decodeGeneratedGameNameLine(selectedGameIndex, 0, line); lcdPrintLine(1, line);", Sketch);
        Assert.Contains("decodeGeneratedGameNameLine(selectedGameIndex, 1, line); lcdPrintLine(2, line);", Sketch);
        Assert.DoesNotContain("gameNameScroll", Sketch);
        Assert.DoesNotContain("Profile %03u", Sketch);
    }

    [Fact]
    public void BacklightTimersAndWakeOnlyInputHaveDistinctSuccessAndFailureBehavior()
    {
        Assert.Contains("BACKLIGHT_IDLE_TIMEOUT_MS  = 30000", Sketch);
        Assert.Contains("BACKLIGHT_APPLY_TIMEOUT_MS = 5000", Sketch);
        Assert.Contains("if (!backlightEnabled)", Sketch);
        Assert.Contains("noteUserActivity();\n        renderUI();\n        return;", Sketch);
        Assert.Contains("if (success)\n        {", Sketch);
        Assert.Contains("Failure deliberately retains the ordinary 30-second activity timeout", Sketch);
        Assert.DoesNotContain("delay(5000)", Sketch);
        Assert.DoesNotContain("delay(30000)", Sketch);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not find firmware sketch from test output directory.");
    }
}
