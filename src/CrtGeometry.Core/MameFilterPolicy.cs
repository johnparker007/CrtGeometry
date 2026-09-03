namespace CrtGeometry.Core;

public sealed class MameFilterPolicy
{
    public MameExclusionReason Evaluate(MameMachine machine)
    {
        var reasons = MameExclusionReason.None;
        if (machine.IsBios) reasons |= MameExclusionReason.Bios;
        if (machine.IsDevice) reasons |= MameExclusionReason.Device;
        if (machine.IsMechanical) reasons |= MameExclusionReason.Mechanical;
        if (!machine.Runnable) reasons |= MameExclusionReason.NotRunnable;
        if (machine.Displays.Count == 0) reasons |= MameExclusionReason.NoDisplay;
        else if (machine.Displays.All(display =>
                     display.Type is not null && !display.Type.Equals("raster", StringComparison.OrdinalIgnoreCase)))
            reasons |= MameExclusionReason.NonRaster;
        // Older listxml often omits coins entirely. Only reject an explicit zero.
        if (machine.CoinInputs == 0) reasons |= MameExclusionReason.NoCoinInput;
        return reasons;
    }
}
