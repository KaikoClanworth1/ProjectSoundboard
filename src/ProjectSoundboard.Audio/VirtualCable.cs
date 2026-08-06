using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.Audio;

/// <summary>
/// A virtual audio cable: the playback endpoint we feed, paired with the recording endpoint
/// a voice app listens to. The two are separate Windows devices with confusingly inverted
/// names — VB-CABLE's playback device is called "CABLE Input" — so pairing them up front
/// means the UI can always tell the user the exact string to pick in Discord.
/// </summary>
public sealed class VirtualCableInfo
{
    /// <summary>The playback endpoint Project Soundboard sends audio to.</summary>
    public required AudioDeviceInfo Output { get; init; }

    /// <summary>The recording endpoint Discord, VRChat and OBS should be pointed at.</summary>
    public AudioDeviceInfo? Microphone { get; init; }

    /// <summary>Driver family, e.g. "VB-CABLE" or "VoiceMeeter".</summary>
    public required string Product { get; init; }

    /// <summary>True when we found both halves and the routing can actually work.</summary>
    public bool IsComplete => Microphone is not null;
}

/// <summary>
/// Detection and naming for virtual cables.
///
/// Project Soundboard does not ship its own audio driver, so the names Windows shows are
/// whichever driver the user installed. Inside our own UI we present the cable under our
/// own labels, but anywhere the user has to retype the name into another application we
/// always show the real device name — an alias there would be actively harmful.
/// </summary>
public static class VirtualCable
{
    /// <summary>What we call the playback endpoint inside Project Soundboard's own UI.</summary>
    public const string OutputAlias = "Project Soundboard Output";

    /// <summary>What we call the recording endpoint inside Project Soundboard's own UI.</summary>
    public const string MicrophoneAlias = "Project Soundboard Microphone";

    /// <summary>Known driver families, most specific first.</summary>
    private static readonly (string Hint, string Product)[] Products =
    {
        ("VoiceMeeter", "VoiceMeeter"),
        ("VB-Audio", "VB-CABLE"),
        ("VB-Cable", "VB-CABLE"),
        ("CABLE", "VB-CABLE"),
        ("Virtual Audio Cable", "Virtual Audio Cable"),
        ("Virtual Audio", "virtual audio device")
    };

    /// <summary>
    /// Find the cable currently in use, preferring <paramref name="preferredOutputId"/>
    /// (the device the user configured) over whatever happens to be installed.
    /// </summary>
    public static VirtualCableInfo? Detect(AudioDeviceService devices, string? preferredOutputId = null)
    {
        var outputs = devices.GetDevices(DeviceKind.Output);
        var inputs = devices.GetDevices(DeviceKind.Input);

        var output = outputs.FirstOrDefault(d => d.Id == preferredOutputId && d.IsVirtualCable)
                     ?? outputs.FirstOrDefault(d => d.IsVirtualCable);

        if (output is null) return null;

        var info = new VirtualCableInfo
        {
            Output = output,
            Microphone = FindCompanion(output, inputs),
            Product = ProductFor(output.Name)
        };

        Log.Debug($"Virtual cable: '{output.Name}' → '{info.Microphone?.Name ?? "no companion found"}'.");
        return info;
    }

    public static string ProductFor(string deviceName)
    {
        foreach (var (hint, product) in Products)
        {
            if (deviceName.Contains(hint, StringComparison.OrdinalIgnoreCase)) return product;
        }
        return "virtual audio cable";
    }

    /// <summary>
    /// Match a playback endpoint to its recording twin. Cable drivers name the two halves
    /// symmetrically — "CABLE Input"/"CABLE Output", "CABLE In 16ch"/"CABLE Out 16ch" — so
    /// swapping the direction word and comparing gives a reliable pairing, including on
    /// machines with several cables installed side by side.
    /// </summary>
    public static AudioDeviceInfo? FindCompanion(
        AudioDeviceInfo output, IReadOnlyList<AudioDeviceInfo> captureDevices)
    {
        var candidates = captureDevices.Where(d => d.IsVirtualCable).ToList();
        if (candidates.Count == 0) return null;

        // Exactly one cable on the machine: no ambiguity to resolve.
        if (candidates.Count == 1) return candidates[0];

        var (outputLabel, outputSuffix) = Split(output.Name);
        var expected = SwapDirection(outputLabel);

        foreach (var candidate in candidates)
        {
            var (label, suffix) = Split(candidate.Name);

            // The parenthesised driver name must agree, otherwise we would happily pair
            // VB-CABLE's playback half with VoiceMeeter's recording half.
            if (!string.Equals(suffix, outputSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(label, expected, StringComparison.OrdinalIgnoreCase)) return candidate;
        }

        // Same driver but an unfamiliar naming scheme — better than nothing.
        return candidates.FirstOrDefault(c =>
            string.Equals(Split(c.Name).Suffix, outputSuffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Split "CABLE In 16ch (VB-Audio Virtual Cable)" into label and driver suffix.</summary>
    private static (string Label, string Suffix) Split(string name)
    {
        var open = name.LastIndexOf('(');
        if (open <= 0 || !name.TrimEnd().EndsWith(')')) return (name.Trim(), string.Empty);

        var label = name[..open].Trim();
        var suffix = name[(open + 1)..].TrimEnd().TrimEnd(')').Trim();
        return (label, suffix);
    }

    /// <summary>Turn the playback side's label into what its recording twin should be called.</summary>
    private static string SwapDirection(string label)
    {
        var words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < words.Length; i++)
        {
            if (string.Equals(words[i], "Input", StringComparison.OrdinalIgnoreCase)) words[i] = "Output";
            else if (string.Equals(words[i], "In", StringComparison.OrdinalIgnoreCase)) words[i] = "Out";
        }

        return string.Join(' ', words);
    }
}
