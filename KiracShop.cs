using System.Linq;

namespace AtlasMapLabels;

/// <summary>One step of Kirac's shop: once <see cref="Objectives"/> atlas objectives are done, the
/// shop can offer maps up to <see cref="ShopTier"/>.</summary>
public readonly record struct ShopBreakpoint(int Objectives, int ShopTier, int RerollCost)
{
    /// <summary>Area level of the highest map offered (T1 = level 68 ... T16 = level 83).</summary>
    public int MapLevel => 67 + ShopTier;
}

/// <summary>
/// How Kirac's shop picks the highest map tier it offers. It is not the highest map you have
/// completed: it is the number of atlas objectives (bonus-completed regular, non-unique maps, 100 in
/// all) measured against fixed breakpoints.
///
/// Sources: the table in the r/pathofexile post "How Kirac's shop actually works" and its cheat sheet,
/// cross-checked against poedb's Kirac Levels (map level 68-83 and reroll cost). Whether the 100 is
/// reached by the maps the cheat sheet lists (4/8/6/6/8/6/6/6/6/8/6/6/6/8/6 at T1-T15, 4 at T16) matches
/// the game's own atlas data for T1-T15 exactly; which four T16 maps count is not in the data.
/// </summary>
public static class KiracShop
{
    public const int MaxObjectives = 100;

    public static readonly ShopBreakpoint[] Breakpoints =
    {
        new(0, 1, 1000),
        new(1, 2, 1500),
        new(3, 3, 2000),
        new(6, 4, 2500),
        new(12, 5, 3000),
        new(16, 6, 3500),
        new(21, 7, 4000),
        new(26, 8, 4500),
        new(31, 9, 5000),
        new(36, 10, 5500),
        new(41, 11, 6000),
        new(46, 12, 6500),
        new(51, 13, 7000),
        new(56, 14, 7500),
        new(61, 15, 8000),
        new(100, 16, 10000),
    };

    /// <summary>The breakpoint the shop is currently on for this many objectives.</summary>
    public static ShopBreakpoint Current(int objectives) =>
        Breakpoints.Last(b => b.Objectives <= objectives);

    /// <summary>The next breakpoint to reach, or null once at the top.</summary>
    public static ShopBreakpoint? Next(int objectives)
    {
        foreach (var b in Breakpoints)
            if (b.Objectives > objectives)
                return b;
        return null;
    }
}
