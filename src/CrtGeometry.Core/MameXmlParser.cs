using System.Globalization;
using System.Text.Json;
using System.Xml;

namespace CrtGeometry.Core;

public sealed class MameXmlParser
{
    public MameSourceMetadata Parse(Stream stream, Action<MameMachine> onMachine,
        IProgress<MameParseProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            CloseInput = false
        });
        MameSourceMetadata metadata = new(null, null, null);
        var count = 0;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.LocalName == "mame")
                metadata = new(reader.GetAttribute("build"), reader.GetAttribute("debug"), reader.GetAttribute("mameconfig"));
            else if (reader.LocalName is "game" or "machine")
            {
                using var subtree = reader.ReadSubtree();
                var machine = ReadMachine(subtree);
                if (machine is null) continue;
                onMachine(machine);
                count++;
                if (count % 100 == 0) progress?.Report(new(count, machine.RomName));
            }
        }
        progress?.Report(new(count, null));
        return metadata;
    }

    private static MameMachine? ReadMachine(XmlReader reader)
    {
        reader.Read();
        var name = reader.GetAttribute("name");
        if (string.IsNullOrWhiteSpace(name)) return null;
        var machine = new MameMachine
        {
            RomName = name,
            CloneOf = reader.GetAttribute("cloneof"),
            Runnable = !IsYesNoFalse(reader.GetAttribute("runnable")),
            IsBios = IsYes(reader.GetAttribute("isbios")),
            IsDevice = IsYes(reader.GetAttribute("isdevice")),
            IsMechanical = IsYes(reader.GetAttribute("ismechanical"))
        };
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            switch (reader.LocalName)
            {
                case "description": machine.Description = reader.ReadElementContentAsString(); break;
                case "year": machine.Year = reader.ReadElementContentAsString(); break;
                case "manufacturer": machine.Manufacturer = reader.ReadElementContentAsString(); break;
                case "input": machine.CoinInputs = Int(reader.GetAttribute("coins")); break;
                case "display": machine.Displays.Add(ReadDisplay(reader)); break;
            }
        }
        return machine;
    }

    private static MameDisplay ReadDisplay(XmlReader reader)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (reader.HasAttributes)
        {
            while (reader.MoveToNextAttribute()) attributes[reader.LocalName] = reader.Value;
            reader.MoveToElement();
        }
        string? Get(string key) => attributes.GetValueOrDefault(key);
        return new MameDisplay
        {
            Type = Get("type"), Width = Int(Get("width")), Height = Int(Get("height")),
            Rotate = Int(Get("rotate")), Refresh = Double(Get("refresh")), PixelClock = Long(Get("pixclock")),
            HTotal = Int(Get("htotal")), HBEnd = Int(Get("hbend")), HBStart = Int(Get("hbstart")),
            VTotal = Int(Get("vtotal")), VBEnd = Int(Get("vbend")), VBStart = Int(Get("vbstart")),
            RawAttributesJson = JsonSerializer.Serialize(attributes)
        };
    }

    private static bool IsYes(string? value) => value?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true;
    private static bool IsYesNoFalse(string? value) => value?.Equals("no", StringComparison.OrdinalIgnoreCase) == true;
    private static int? Int(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static long? Long(string? value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static double? Double(string? value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
