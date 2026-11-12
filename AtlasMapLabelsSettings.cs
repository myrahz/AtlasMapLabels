using System.Collections.Generic;
using Newtonsoft.Json;
using SharpDX;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;

namespace AtlasMapLabels;

public class AtlasMapLabelsSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(true);

    [Menu("Show Map Name")]
    public ToggleNode ShowName { get; set; } = new(true);

    [Menu("Show Tier", "Shows the map's base tier (T1-T16). Unique and special maps have no tier and show none.")]
    public ToggleNode ShowTier { get; set; } = new(true);

    [Menu("Only Uncompleted Maps", "Hide labels (and highlights) on maps you have already completed.")]
    public ToggleNode OnlyUncompleted { get; set; } = new(true);

    [Menu("Only Label Highlighted Maps", "Only the maps matching the highlight list get a label.")]
    public ToggleNode OnlyHighlighted { get; set; } = new(false);

    [Menu("Highlight Maps", "Comma-separated map names to highlight, e.g. 'Cemetery, Strand, Vaal Pyramid'. Case-insensitive; a name matches if the map name contains it.")]
    public TextNode HighlightMaps { get; set; } = new("");

    [Menu("Highlight Color")]
    public ColorNode HighlightColor { get; set; } = new(new Color(255, 215, 0, 255));

    [Menu("Label Color")]
    public ColorNode LabelColor { get; set; } = new(new Color(255, 255, 255, 255));

    [Menu("Completed Label Color", "Used when completed maps are shown (Only Uncompleted is off).")]
    public ColorNode CompletedColor { get; set; } = new(new Color(140, 140, 140, 255));

    [Menu("Path Color", "Color of the route to each pathed map, from the first map you still have to run.")]
    public ColorNode PathColor { get; set; } = new(new Color(0, 220, 255, 255));

    [Menu("Path Start Color", "Frame color of the first map on the route that you haven't completed yet (where the path starts). The rest of the route uses Path Color.")]
    public ColorNode PathStartColor { get; set; } = new(new Color(0, 255, 100, 255));

    [Menu("Path Line Thickness")]
    public RangeNode<int> PathThickness { get; set; } = new(3, 1, 10);

    // The three id lists have non-empty defaults. Json.NET would otherwise add a saved list on top of
    // the default one (duplicating entries on every load), so a saved list replaces the default.

    /// <summary>Node Ids ticked "Highlight" in the map list. Saved with the settings.</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> HighlightedIds { get; set; } = new()
    {
        "InfiniteHungerBoss", "BlackStarBoss", "MapWorldsCitySquare", "MapWorldsGrotto", "MapWorldsArsenal",
    };

    /// <summary>Node Ids whose "Show on Atlas" is unticked in the map list. Saved with the settings.</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> HiddenIds { get; set; } = new()
    {
        "BlightedMap", "LegionDomain", "CatarinaBoss", "BenevolenceBoss",
        "ElderWatchstoneSlotNode", "CleansingFireWatchstoneSlotNode", "TangledWatchstoneSlotNode", "MavenWatchstoneSlotNode",
        "AtziriBoss", "IncursionTemple", "BreachlordBoss", "KingInTheMistsBoss", "OshabiBoss",
    };

    /// <summary>Node Ids ticked "Path" in the map list window. Saved with the settings.</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> PathedIds { get; set; } = new()
    {
        "InfiniteHungerBoss", "BlackStarBoss", "MapWorldsPrimordialPool", "MapWorldsGrotto", "MapWorldsArsenal",
    };

    [Menu("Show Kirac Shop Panel", "Two lines above the Atlas's own objective counter: the highest map tier Kirac's shop offers, and the next tier with how many more objectives it needs.")]
    public ToggleNode ShowShopPanel { get; set; } = new(true);

    [Menu("Also Show On Kirac's Shop Window", "Draw the same two lines above Kirac's shop window when it is open, with or without the Atlas open. Kirac's shop is recognised by the Orb of Unmaking in its list, or a reroll cost matching your objective count.")]
    public ToggleNode ShowOnKiracShop { get; set; } = new(true);

    [Menu("Shop Window Padding (px)", "Inset of the message from the top-left corner of Kirac's shop window, inside the window (applied to both the left and top edges).")]
    public RangeNode<int> ShopWindowPad { get; set; } = new(4, 0, 40);

    [Menu("Shop Panel X (% of screen)", "Only used if the Atlas objective counter element can't be found.")]
    public RangeNode<int> ShopPanelX { get; set; } = new(34, 0, 95);

    [Menu("Shop Panel Y (% of screen)", "Only used if the Atlas objective counter element can't be found.")]
    public RangeNode<int> ShopPanelY { get; set; } = new(1, 0, 95);

    [Menu("Mark Maps That Count Toward Shop", "Adds '+1' to the label of uncompleted maps that would give an atlas objective. T1-T15 regular maps all count. For T16 only some count and the data doesn't say which, so those show '+1?' until you complete one.")]
    public ToggleNode MarkShopMaps { get; set; } = new(false);

    [Menu("Kirac Refresh Alert", "In a hideout, opens a window when your atlas objectives reach a Kirac shop breakpoint (shop refreshed) or are one away from it. 'Acknowledge' silences that breakpoint for good; closing the window only hides it until the next area.")]
    public ToggleNode RefreshAlert { get; set; } = new(true);

    /// <summary>Breakpoint (objective count) whose 'shop refreshed' alert was acknowledged.</summary>
    public int AckRefreshBreakpoint { get; set; } = 0;

    /// <summary>Breakpoint whose 'one away' warning was acknowledged.</summary>
    public int AckSoonBreakpoint { get; set; } = 0;

    [Menu("Don't Draw Over Right Panel", "While a right-hand side panel (Inventory etc.) is open, skip every label, highlight, path line and the shop panel that would land on it.")]
    public ToggleNode AvoidRightPanel { get; set; } = new(true);

    [Menu("Top Text Margin (px)", "No label or panel text is drawn above this many pixels from the top of the screen. 0 = off. Highlights and path lines still draw there.")]
    public RangeNode<int> TopTextMargin { get; set; } = new(0, 0, 400);

    [Menu("No Text Over Game UI", "Skip label and panel text that would overlap the flask panel, life orb, mana orb or skill bar. Highlights and path lines still draw.")]
    public ToggleNode AvoidGameUi { get; set; } = new(true);

    [Menu("Game UI Padding (px)", "Extra space kept clear around the flask panel, orbs and skill bar.")]
    public RangeNode<int> GameUiPadding { get; set; } = new(2, 0, 60);

    [Menu("Show Tier Summary", "A list centred below the Atlas objective counter (beside the tree selector buttons if the counter can't be found): one row per tier, lowest first, with how many uncompleted maps are left in it.")]
    public ToggleNode ShowTierSummary { get; set; } = new(true);

    [Menu("Lowest Tiers (N)", "How many of the lowest tiers that still have uncompleted maps are listed in the summary and marked on the Atlas. 0 turns both off.")]
    public RangeNode<int> LowestTierCount { get; set; } = new(3, 0, 16);

    [Menu("Mark Lowest Tiers' Maps On Atlas", "Draws the label of every uncompleted map in the N lowest tiers in the lowest-tier colour (red), even if map names are off, so you can see the easiest maps to do next. A highlighted map keeps its highlight colour.")]
    public ToggleNode MarkLowestTiers { get; set; } = new(true);

    [Menu("Lowest Tier Color")]
    public ColorNode LowestTierColor { get; set; } = new(new Color(255, 60, 60, 255));

    [Menu("Hide Unique Maps Once Bonus Is Done", "Unique maps give a completion bonus only up to a cap. When you have completed that many unique maps, every unique map is hidden (labels, highlights, markers and the map list). Until then they show as normal.")]
    public ToggleNode HideUniquesWhenBonusDone { get; set; } = new(true);

    [Menu("Unique Completion Cap", "How many completed unique maps the bonus needs (10).")]
    public RangeNode<int> UniqueBonusCap { get; set; } = new(10, 1, 30);

    [Menu("Show Pinnacle Bosses", "Shaper, Elder, Cortex, Maven, Sirus, Atziri, Eater of Worlds, Searing Exarch, Infinite Hunger (Seething Chyme), Black Star (Polaric Void), Fear, Benevolence, Ignorance. Applies to labels, highlights, markers and the map list.")]
    public ToggleNode ShowPinnacleBosses { get; set; } = new(true);

    [Menu("Show League Bosses", "Breachlord, Oshabi, Catarina, King in the Mists, Faridun, Simulacrum, Ultimatum, Incursion Temple, Abyss boss.")]
    public ToggleNode ShowLeagueBosses { get; set; } = new(true);

    [Menu("Show Other Special Nodes", "Reliquary Vault, Valdo, Legion Domain, Blighted map, and any other node without a map name.")]
    public ToggleNode ShowOtherSpecial { get; set; } = new(true);

    [Menu("Show Watchstone Slots")]
    public ToggleNode ShowWatchstoneSlots { get; set; } = new(true);

    [Menu("Show Random Content Nodes", "Betrayal safehouses, Vaal traders, Logbook, Harvest grove and the like. They only appear on the Atlas when active.")]
    public ToggleNode ShowRandomContent { get; set; } = new(true);

    [Menu("Random Content Alert", "While the Atlas is open, opens a window when a Random Content node is shown on it (needs 'Show Random Content Nodes' on and the node not hidden in the map list). 'Acknowledge' silences it until that node disappears and shows up again.")]
    public ToggleNode RandomContentAlert { get; set; } = new(true);

    [Menu("Custom Node Names", "Rename boss / content nodes whose name isn't in the game data. Format: NodeId=Name, NodeId=Name (e.g. EaterOfWorldsBoss=Absence of Symmetry and Harmony). The node Ids are listed by the Maps list's tooltip.")]
    public TextNode CustomNodeNames { get; set; } = new("");

    [Menu("Label Y Offset", "Pixels below the node's centre; the node icon radius is added automatically.")]
    public RangeNode<int> LabelOffset { get; set; } = new(2, -40, 60);
}
