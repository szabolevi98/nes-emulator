namespace NesEmulator.Core.Ppu;

/// <summary>
/// The sixty-four colours the picture unit can produce.
///
/// The console never stored colours as red, green and blue. It generated a
/// composite television signal directly, so each index is really a phase and an
/// amplitude, and every published palette is somebody's decision about what that
/// signal looks like on a screen. This is the widely used reference table.
/// </summary>
public static class NesPalette
{
    /// <summary>Index to packed 0x00RRGGBB.</summary>
    public static readonly int[] Rgb =
    [
        0x545454, 0x001E74, 0x081090, 0x300088, 0x440064, 0x5C0030, 0x540400, 0x3C1800,
        0x202A00, 0x083A00, 0x004000, 0x003C00, 0x00323C, 0x000000, 0x000000, 0x000000,
        0x989698, 0x084CC4, 0x3032EC, 0x5C1EE4, 0x8814B0, 0xA01464, 0x982220, 0x783C00,
        0x545A00, 0x287200, 0x087C00, 0x007628, 0x006678, 0x000000, 0x000000, 0x000000,
        0xECEEEC, 0x4C9AEC, 0x787CEC, 0xB062EC, 0xE454EC, 0xEC58B4, 0xEC6A64, 0xD48820,
        0xA0AA00, 0x74C400, 0x4CD020, 0x38CC6C, 0x38B4CC, 0x3C3C3C, 0x000000, 0x000000,
        0xECEEEC, 0xA8CCEC, 0xBCBCEC, 0xD4B2EC, 0xECAEEC, 0xECAED4, 0xECB4B0, 0xE4C490,
        0xCCD278, 0xB4DE78, 0xA8E290, 0x98E2B4, 0xA0D6E4, 0xA0A2A0, 0x000000, 0x000000,
    ];

    /// <summary>
    /// The same colours under each of the eight emphasis settings, indexed as
    /// <c>emphasis &lt;&lt; 6 | colour</c>.
    ///
    /// The three high bits of the mask register do not brighten a channel so much
    /// as hold the other two back: the signal spends longer at the emphasised
    /// phase and the rest come out dimmer. Games use it for a screen-wide flash,
    /// for going under water, and for the moment a hit lands. With all three set
    /// the picture simply darkens.
    /// </summary>
    public static readonly int[] Emphasized = BuildEmphasized();

    private const double Attenuation = 0.746;

    private static int[] BuildEmphasized()
    {
        int[] table = new int[8 * 64];
        for (int emphasis = 0; emphasis < 8; emphasis++)
        {
            for (int colour = 0; colour < 64; colour++)
            {
                int packed = Rgb[colour];
                double red = (packed >> 16) & 0xFF;
                double green = (packed >> 8) & 0xFF;
                double blue = packed & 0xFF;

                // Each bit dims the two channels it does not emphasise.
                if ((emphasis & 1) != 0) { green *= Attenuation; blue *= Attenuation; }
                if ((emphasis & 2) != 0) { red *= Attenuation; blue *= Attenuation; }
                if ((emphasis & 4) != 0) { red *= Attenuation; green *= Attenuation; }

                table[(emphasis << 6) | colour] =
                    ((int)Math.Round(red) << 16) | ((int)Math.Round(green) << 8) | (int)Math.Round(blue);
            }
        }

        return table;
    }
}
