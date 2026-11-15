using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ImGuiNET;
using SharpDX;
using Color = SharpDX.Color;
using Vector2 = System.Numerics.Vector2;

namespace AtlasMapLabels;

/// <summary>
/// Labels every map node on the Atlas panel with its name and tier, and can hide completed maps or
/// highlight chosen ones.
///
/// How the data lines up (verified live): the node list comes from the game files
/// (<c>Files.AtlasNodes</c>: Area name, BaseTier, PosX/PosY, Id). The panel's node container
/// (<c>Atlas.Children[0]</c>) holds one small Element per node but in no usable order, and the elements
/// carry no text. They do sit at a fixed linear scale of the file position
/// (element.Position = file pos * 5.689 - 21.2), so each node is matched to its nearest element.
/// Completion is <c>ServerData.CompletedNodes</c>, matched by node Id.
/// </summary>
public class AtlasMapLabels : BaseSettingsPlugin<AtlasMapLabelsSettings>
{
    // Linear map from AtlasNode file position to the node element's local Position.
    private const float PosScale = 5.689f;
    private const float PosOffset = -21.2f;

    // Larger node icons (special maps) have their top-left a few units off the file position.
    private const float MatchTolerance = 24f;

    // Layers (backgrounds, connection art) are screen-sized; real node icons are well below this.
    private const float MaxNodeElementWidth = 100f;

    private sealed class Link
    {
        public AtlasNode Node;
        public int ChildIndex;
        public float ExpectedX, ExpectedY, Tolerance;
    }

    private readonly List<Link> _links = new();
    private readonly Dictionary<string, Link> _linkById = new();

    // Map list window state.
    private string _listFilter = string.Empty;
    private int _listShow; // 0 all, 1 completed, 2 uncompleted
    private List<AtlasNode> _allMaps = new();

    // Shortest paths from the completed maps to each pathed map, as ordered node lists (start first).
    private List<List<AtlasNode>> _paths = new();
    private string _pathKey = string.Empty;
    private int _linkedChildCount = -1;
    private DateTime _lastRelink = DateTime.MinValue;

    private string _highlightSource = null;
    private string[] _highlightNames = Array.Empty<string>();

    private HashSet<string> _completedIds = new();
    private int _completedCount = -1;

    // Kirac shop: the game's own atlas-objective list (what the shop tier is measured against).
    private HashSet<string> _bonusIds = new();
    private int _bonusCount;

    // Hop distance from the nearest completed map, and the BFS tree that gives the shortest paths.
    private Dictionary<string, int> _dist = new();
    private Dictionary<string, AtlasNode> _prev = new();
    private string _graphKey = string.Empty;

    private bool _closedThisArea;
    private int _listSort; // 0 name, 1 nearest first

    public override bool Initialise() => true;

    public override void AreaChange(AreaInstance area)
    {
        _closedThisArea = false;
        // Entering a map (anything that isn't a hideout or town) means a Random Content node may have been
        // run and respawned while the Atlas was closed, so acknowledgements are dropped and it alerts again.
        if (area?.Area?.IsHideout != true && area?.Area?.IsTown != true) _ackedRandom.Clear();
    }

    public override void Render()
    {
        if (!Settings.Enable) return;

        RefreshCompleted();
        DrawRefreshAlert();

        var ui = GameController.IngameState?.IngameUi;
        DrawShopWindowMessage(ui);

        var atlas = ui?.Atlas;
        if (atlas == null || !atlas.IsVisible) return;

        var container = atlas.Children.Count > 0 ? atlas.Children[0] : null;
        if (container == null || !container.IsVisible) return;

        var children = container.Children;
        UpdateBlockedArea(ui);
        EnsureLinks(children);
        RefreshHighlightNames();

        var showName = Settings.ShowName.Value;
        var showTier = Settings.ShowTier.Value;

        DrawRandomContentAlert(children);

        EnsureMapList();
        RefreshTierSummary();
        RefreshPaths();
        DrawPaths(children);
        // When Kirac's shop window is open it carries the message itself; don't draw it twice.
        if (Settings.ShowShopPanel && _kiracWindow == null && !ObjectivesMaxed) DrawShopPanel(children);
        if (Settings.ShowTierSummary && !ObjectivesMaxed) DrawTierSummary(atlas, children);

        var markLowest = Settings.MarkLowestTiers && _lowestTiers.Count > 0;
        if (!showName && !showTier && !markLowest && _highlightNames.Length == 0 && Settings.HighlightedIds.Count == 0) return;

        foreach (var link in _links)
        {
            if (link.ChildIndex >= children.Count) continue;
            var element = children[link.ChildIndex];
            if (element == null || !element.IsVisible) continue;

            var node = link.Node;
            if (!GroupEnabled(node)) continue;
            // "Show on Atlas" unticked in the map list: nothing is drawn for this node, even if it is
            // also ticked Highlight. (A pathed route still draws through it.)
            if (Settings.HiddenIds.Contains(node.Id)) continue;
            var completed = IsDone(node);
            if (completed && Settings.OnlyUncompleted) continue;

            var name = DisplayName(node);
            var highlighted = IsHighlighted(name) || Settings.HighlightedIds.Contains(node.Id);
            if (Settings.OnlyHighlighted && !highlighted) continue;

            var rect = element.GetClientRect();
            var center = new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);

            // Anything that would land on the open right-hand panel is skipped whole (node box,
            // highlight and label together), so a node is never half drawn.
            if (Blocked(new RectangleF(rect.X - 6, rect.Y - 6, rect.Width + 12, rect.Height + 12))) continue;

            // One of the N lowest tiers that still have uncompleted maps: the label is forced on and drawn
            // in the lowest-tier colour (a highlight colour still wins) so the easiest next maps stand out.
            var lowest = markLowest && IsInLowestTiers(node);
            var text = BuildLabel(name, node.BaseTier, showName || highlighted || lowest, showTier || lowest);
            if (Settings.MarkShopMaps && !completed)
            {
                var mark = ShopMark(node);
                if (mark.Length > 0) text = text.Length == 0 ? mark : text + " " + mark;
            }

            var pos = default(Vector2);
            var size = default(Vector2);
            if (text.Length > 0)
            {
                size = Graphics.MeasureText(text);
                pos = new Vector2(center.X - size.X / 2f, center.Y + rect.Height / 2f + Settings.LabelOffset.Value);
                var labelBox = new RectangleF(pos.X - 2, pos.Y - 1, size.X + 4, size.Y + 2);
                if (Blocked(labelBox)) continue;
                // Text over the top strip or the game UI is dropped, but the node's highlight still draws.
                if (TextBlocked(labelBox)) text = string.Empty;
            }

            if (highlighted) DrawHighlight(rect);
            if (text.Length == 0) continue;

            var color = highlighted ? Settings.HighlightColor.Value
                : lowest ? Settings.LowestTierColor.Value
                : completed ? Settings.CompletedColor.Value
                : Settings.LabelColor.Value;
            Graphics.DrawBox(new RectangleF(pos.X - 2, pos.Y - 1, size.X + 4, size.Y + 2), new Color(0, 0, 0, 170));
            Graphics.DrawText(text, pos, color);
        }
    }

    // Uncompleted maps per tier. "Uncompleted map" = a named, non-unique, tiered map that is shown (its
    // group is on, it isn't hidden) and that can give an atlas objective (not a map known not to count).
    // Only the N lowest tiers that still have any are summarised and marked.
    private List<(int Tier, int Count)> _tierSummary = new();
    private HashSet<int> _lowestTiers = new();
    private string _summaryKey = string.Empty;
    private DateTime _summaryAt = DateTime.MinValue;

    private bool IsUncompletedTieredMap(AtlasNode n) =>
        n.BaseTier > 0 && !n.IsUniqueMap && !string.IsNullOrEmpty(n.Area?.Name) && IsListed(n)
        && GroupEnabled(n) && !Settings.HiddenIds.Contains(n.Id)
        && CountsTowardShop(n) != ShopCount.No && !IsDone(n);

    private bool IsInLowestTiers(AtlasNode n) => IsUncompletedTieredMap(n) && _lowestTiers.Contains(n.BaseTier);

    /// <summary>Done for the purpose of the 100 objectives: an objective node is done once it is on the
    /// game's BonusCompletedNodes list; anything else falls back to CompletedNodes.</summary>
    private bool IsDone(AtlasNode n) =>
        CountsTowardShop(n) == ShopCount.No ? _completedIds.Contains(n.Id) : _bonusIds.Contains(n.Id);

    private void RefreshTierSummary()
    {
        // Cheap inputs make the key; the time check also catches in-place list edits (hide/show ticks).
        var key = $"{_completedCount}|{_bonusCount}|{_allMaps.Count}|{Settings.LowestTierCount.Value}|{Settings.HideUniquesWhenBonusDone.Value}|{Settings.HiddenIds.Count}";
        if (key == _summaryKey && (DateTime.UtcNow - _summaryAt).TotalSeconds < 1) return;
        _summaryKey = key;
        _summaryAt = DateTime.UtcNow;

        var n = ObjectivesMaxed ? 0 : Settings.LowestTierCount.Value;
        _tierSummary = _allMaps
            .Where(IsUncompletedTieredMap)
            .GroupBy(m => m.BaseTier)
            .OrderBy(g => g.Key)
            .Take(n)
            .Select(g => (g.Key, g.Count()))
            .ToList();
        _lowestTiers = _tierSummary.Select(t => t.Tier).ToHashSet();
    }

    /// <summary>A small list to the right of the Atlas's tree selector buttons: one row per tier, lowest
    /// first, with how many maps of that tier are left.</summary>
    private void DrawTierSummary(ExileCore.PoEMemory.Elements.AtlasPanel atlas, IList<Element> children)
    {
        var tint = Settings.MarkLowestTiers ? Settings.LowestTierColor.Value : Color.White;
        var lines = new List<(string Text, Color Color)> { ("Uncompleted", new Color(200, 200, 200, 255)) };
        if (_tierSummary.Count == 0) lines.Add(("none left", Color.White));
        foreach (var (tier, count) in _tierSummary)
            lines.Add(($"T{tier}: {count}", tint));

        // Anchor: centred just below the Atlas's own objective counter (the Kirac shop lines sit above it).
        // If that element can't be found, fall back to the right of the tree selector buttons.
        var progress = FindProgressElement(children);
        bool centred;
        float x, y;
        if (progress != null && progress.IsVisible)
        {
            var r = progress.GetClientRect();
            centred = true;
            x = r.X + r.Width / 2f;
            y = r.Bottom + 2;
        }
        else
        {
            var buttons = atlas.TreeSelectorButtons;
            if (buttons == null || buttons.Count == 0) return;
            var right = float.MinValue;
            var top = float.MaxValue;
            foreach (var b in buttons)
            {
                if (b == null || !b.IsVisible) continue;
                var r = b.GetClientRect();
                right = Math.Max(right, r.Right);
                top = Math.Min(top, r.Top);
            }
            if (right == float.MinValue) return;
            centred = false;
            x = right + 8;
            y = top;
        }

        x += Settings.TierSummaryOffsetX.Value;
        y += Settings.TierSummaryOffsetY.Value;

        foreach (var (text, color) in lines)
        {
            var size = Graphics.MeasureText(text);
            var px = centred ? x - size.X / 2f : x;
            var box = new RectangleF(px - 3, y - 1, size.X + 6, size.Y + 2);
            if (!Blocked(box) && !TextBlocked(box))
            {
                Graphics.DrawBox(box, new Color(0, 0, 0, 190));
                Graphics.DrawText(text, new Vector2(px, y), color);
            }
            y += size.Y + 3;
        }
    }

    // Screen area taken by the right-hand side panel (Inventory etc.) while one is open. Verified live:
    // OpenRightPanel.IsVisible follows whichever right panel is open, and its rect is the panel's
    // full-height column (x 1255-1920 on 1920 wide).
    private RectangleF _blocked;
    private bool _hasBlocked;

    // Areas where text (not highlights or lines) must not be drawn: the top margin strip and the
    // bottom game UI (IngameUi.GameUI.FlaskPanel / LifeOrb / ManaOrb and IngameUi.SkillBar).
    private readonly List<RectangleF> _textBlocked = new();
    private float _topMargin;

    private void UpdateTextBlockedAreas(IngameUIElements ui)
    {
        _textBlocked.Clear();
        _topMargin = Settings.TopTextMargin.Value;
        if (!Settings.AvoidGameUi) return;

        var pad = Settings.GameUiPadding.Value;
        var game = ui?.GameUI;
        foreach (var e in new Element[] { game?.FlaskPanel, game?.LifeOrb, game?.ManaOrb, ui?.SkillBar })
        {
            if (e == null || !e.IsVisible) continue;
            var r = e.GetClientRect();
            if (r.Width <= 0 || r.Height <= 0) continue;
            _textBlocked.Add(new RectangleF(r.X - pad, r.Y - pad, r.Width + pad * 2, r.Height + pad * 2));
        }
    }

    private bool TextBlocked(RectangleF r)
    {
        if (r.Top < _topMargin) return true;
        foreach (var b in _textBlocked)
            if (r.Left < b.Right && r.Right > b.Left && r.Top < b.Bottom && r.Bottom > b.Top)
                return true;
        return false;
    }

    private void UpdateBlockedArea(IngameUIElements ui)
    {
        UpdateTextBlockedAreas(ui);
        _hasBlocked = false;
        if (!Settings.AvoidRightPanel) return;
        var panel = ui?.OpenRightPanel;
        if (panel == null || !panel.IsVisible) return;
        _blocked = panel.GetClientRect();
        _hasBlocked = _blocked.Width > 0 && _blocked.Height > 0;
    }

    private bool Blocked(RectangleF r) =>
        _hasBlocked
        && r.Left < _blocked.Right && r.Right > _blocked.Left
        && r.Top < _blocked.Bottom && r.Bottom > _blocked.Top;

    /// <summary>True if the segment passes through the blocked area (Liang-Barsky clip).</summary>
    private bool SegmentBlocked(Vector2 a, Vector2 b)
    {
        if (!_hasBlocked) return false;
        float t0 = 0f, t1 = 1f;
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        bool Clip(float p, float q)
        {
            if (p == 0f) return q >= 0f;
            var t = q / p;
            if (p < 0f) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
            return true;
        }
        return Clip(-dx, a.X - _blocked.Left) && Clip(dx, _blocked.Right - a.X)
            && Clip(-dy, a.Y - _blocked.Top) && Clip(dy, _blocked.Bottom - a.Y);
    }

    private static string BuildLabel(string name, int tier, bool showName, bool showTier)
    {
        var tierText = showTier && tier > 0 ? "T" + tier : string.Empty;
        var nameText = showName ? name : string.Empty;
        if (nameText.Length == 0) return tierText;
        if (tierText.Length == 0) return nameText;
        return nameText + " " + tierText;
    }

    private void DrawHighlight(RectangleF rect)
    {
        var c = Settings.HighlightColor.Value;
        var pad = 6f;
        var box = new RectangleF(rect.X - pad, rect.Y - pad, rect.Width + pad * 2, rect.Height + pad * 2);
        Graphics.DrawBox(box, new Color(c.R, c.G, c.B, (byte)60));
        Graphics.DrawFrame(box, c, 2);
    }

    private bool IsHighlighted(string mapName)
    {
        if (_highlightNames.Length == 0 || mapName.Length == 0) return false;
        foreach (var n in _highlightNames)
            if (mapName.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void RefreshHighlightNames()
    {
        var source = Settings.HighlightMaps.Value ?? string.Empty;
        if (ReferenceEquals(source, _highlightSource) || source == _highlightSource) return;
        _highlightSource = source;
        _highlightNames = source
            .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToArray();
    }

    private void RefreshCompleted()
    {
        var sd = GameController.IngameState?.ServerData;
        var done = sd?.CompletedNodes;
        var bonus = sd?.BonusCompletedNodes;
        if (done == null || bonus == null) return;
        // Completion only ever grows within a session, so the counts are a sufficient change signal.
        if (done.Count == _completedCount && bonus.Count == _bonusCount) return;
        _completedCount = done.Count;
        _bonusCount = bonus.Count;
        _completedIds = done.Where(n => n != null).Select(n => n.Id).ToHashSet();
        _bonusIds = bonus.Where(n => n != null).Select(n => n.Id).ToHashSet();
        // Unique maps are in CompletedNodes but never in the objective (bonus) list; the unique
        // completion bonus is counted from these (verified: 3 completed uniques -> 77 completed vs 68 bonus).
        _uniqueCompleted = done.Count(n => n != null && n.IsUniqueMap && !string.IsNullOrEmpty(n.Area?.Name));
    }

    private int _uniqueCompleted;

    /// <summary>True once the unique completion bonus is maxed (the setting's cap), so unique maps have
    /// nothing left to give.</summary>
    private bool UniqueBonusDone => _uniqueCompleted >= Settings.UniqueBonusCap.Value;

    private enum ShopCount { No, Yes, Unknown }

    /// <summary>Would completing this map add an atlas objective? Regular T1-T15 maps always do (the
    /// cheat sheet's 96 maps match the game's T1-T15 nodes exactly). Of the 17 T16 nodes only 4 count and
    /// the data doesn't say which, so those are unknown until completed: it is an objective if the game
    /// put it on the bonus list, and not if it is completed but absent from it.</summary>
    private ShopCount CountsTowardShop(AtlasNode node)
    {
        if (node.IsUniqueMap || node.Area == null || string.IsNullOrEmpty(node.Area.Name))
            return ShopCount.No;
        if (node.BaseTier <= 0) return ShopCount.No;
        if (node.BaseTier < 16) return ShopCount.Yes;
        if (_bonusIds.Contains(node.Id)) return ShopCount.Yes;
        // Not in the data directly, but the area level separates them: the 4 T16 maps that count
        // (Park, Arsenal, Primordial Pool, Grotto) are level 71-80; the other T16 nodes (Vaal Temple,
        // Sanctuary, Citadel, Fortress, Abomination, Ziggurat, Hydra, Chimera, Minotaur, Phoenix) are 83-84.
        return node.Area.AreaLevel >= T16NonObjectiveLevel ? ShopCount.No : ShopCount.Yes;
    }

    private const int T16NonObjectiveLevel = 83;

    private string ShopMark(AtlasNode node) => CountsTowardShop(node) switch
    {
        ShopCount.Yes => "+1",
        ShopCount.Unknown => "+1?",
        _ => string.Empty,
    };

    /// <summary>Match every named, placed node to its nearest unclaimed node element. Redone when the
    /// panel's child count changes or, at most every 2 s, when an element no longer sits where expected.</summary>
    private void EnsureLinks(IList<Element> children)
    {
        var stale = _links.Count == 0 || children.Count != _linkedChildCount;
        if (!stale && (DateTime.UtcNow - _lastRelink).TotalSeconds > 2)
        {
            foreach (var l in _links)
            {
                if (l.ChildIndex >= children.Count) { stale = true; break; }
                var p = children[l.ChildIndex].Position;
                if (Math.Abs(p.X - l.ExpectedX) > l.Tolerance || Math.Abs(p.Y - l.ExpectedY) > l.Tolerance)
                {
                    stale = true;
                    break;
                }
            }
            _lastRelink = DateTime.UtcNow;
        }
        if (!stale) return;

        _links.Clear();
        _linkById.Clear();
        _linkedChildCount = children.Count;
        _lastRelink = DateTime.UtcNow;

        var nodes = GameController.Game?.Files?.AtlasNodes?.EntriesList;
        if (nodes == null) return;

        var candidates = new List<(int Index, float X, float Y, float Tol)>();
        for (var i = 0; i < children.Count; i++)
        {
            var e = children[i];
            if (e == null || e.GetClientRect().Width > MaxNodeElementWidth) continue;
            var p = e.Position;
            // Big icons (watchstone slots, pinnacle bosses) have their top-left further from the file position.
            candidates.Add((i, p.X, p.Y, e.GetClientRect().Width >= 35 ? 50f : MatchTolerance));
        }

        var pairs = new List<(float Dist, Link Link)>();
        foreach (var node in nodes)
        {
            if (!IsListed(node)) continue;
            var ex = node.PosX * PosScale + PosOffset;
            var ey = node.PosY * PosScale + PosOffset;
            foreach (var c in candidates)
            {
                var dx = c.X - ex;
                var dy = c.Y - ey;
                var d = MathF.Sqrt(dx * dx + dy * dy);
                if (d <= c.Tol)
                    pairs.Add((d, new Link { Node = node, ChildIndex = c.Index, ExpectedX = ex, ExpectedY = ey, Tolerance = c.Tol }));
            }
        }

        var claimedNodes = new HashSet<string>();
        var claimedChildren = new HashSet<int>();
        foreach (var (_, link) in pairs.OrderBy(p => p.Dist))
        {
            if (claimedChildren.Contains(link.ChildIndex) || !claimedNodes.Add(link.Node.Id)) continue;
            claimedChildren.Add(link.ChildIndex);
            _links.Add(link);
            _linkById[link.Node.Id] = link;
        }

    }

    // Nodes with no Area (boss encounters, league content) are named in the client UI, not in the
    // data files; these are the names that can't be derived. Anything else falls back to the
    // WorldArea with the same Id, then to the Id split into words.
    private static readonly Dictionary<string, string> NodeNames = new()
    {
        // The hideouts pair these (Seething Hideout / Polaric Hideout), which fixes which arena is which.
        ["InfiniteHungerBoss"] = "Seething Chyme",
        ["BlackStarBoss"] = "Polaric Void",
        ["ShaperBoss"] = "The Shaper's Realm",
        ["FaridunBoss"] = "The Black Barya",
    };

    private readonly Dictionary<string, string> _nameCache = new();
    private string _customNamesSource = string.Empty;
    private Dictionary<string, string> _customNames = new();

    private string DisplayName(AtlasNode node)
    {
        var area = node.Area?.Name;
        if (!string.IsNullOrEmpty(area)) return area;
        RefreshCustomNames();
        if (_customNames.TryGetValue(node.Id, out var custom)) return custom;
        if (_nameCache.TryGetValue(node.Id, out var cached)) return cached;

        string name;
        if (NodeNames.TryGetValue(node.Id, out var known)) name = known;
        else
        {
            var world = GameController.Files?.WorldAreas?.EntriesList?.FirstOrDefault(a => a.Id == node.Id)?.Name;
            name = !string.IsNullOrEmpty(world)
                ? world
                : System.Text.RegularExpressions.Regex.Replace(
                    System.Text.RegularExpressions.Regex.Replace(node.Id, "Node$", ""),
                    "(?<=[a-z0-9])(?=[A-Z])", " ");
        }
        return _nameCache[node.Id] = name;
    }

    private void RefreshCustomNames()
    {
        var source = Settings.CustomNodeNames.Value ?? string.Empty;
        if (source == _customNamesSource) return;
        _customNamesSource = source;
        _customNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in source.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && eq < part.Length - 1)
                _customNames[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
    }

    /// <summary>Placed nodes that are a map or an encounter; the watchstone slots are not.</summary>
    // Unplaced nodes sit at (0,0) or a fraction of a pixel from it (e.g. Theatre of Lies).
    private static bool IsListed(AtlasNode n) =>
        n.PosX > 1 && n.PosY > 1;

    private enum NodeGroup { Map, Pinnacle, League, Other, Watchstone, Random }

    private static readonly HashSet<string> PinnacleIds = new()
    {
        "ShaperBoss", "ElderBoss", "UberCortexBoss", "MavenBoss", "SirusBoss", "AtziriBoss",
        "EaterOfWorldsBoss", "SearingExarchBoss", "InfiniteHungerBoss", "BlackStarBoss",
        "FearBoss", "BenevolenceBoss", "IgnoranceBoss",
    };

    private static readonly HashSet<string> LeagueIds = new()
    {
        "BreachlordBoss", "OshabiBoss", "CatarinaBoss", "KingInTheMistsBoss", "FaridunBoss",
        "Simulacrum", "UltimatumTrial", "IncursionTemple", "AbyssLeagueBoss3",
    };

    private static NodeGroup GroupOf(AtlasNode n)
    {
        if (n.Id.EndsWith("WatchstoneSlotNode", StringComparison.Ordinal)) return NodeGroup.Watchstone;
        if (n.Id.StartsWith("RandomContent", StringComparison.Ordinal)) return NodeGroup.Random;
        if (PinnacleIds.Contains(n.Id)) return NodeGroup.Pinnacle;
        if (LeagueIds.Contains(n.Id)) return NodeGroup.League;
        return string.IsNullOrEmpty(n.Area?.Name) ? NodeGroup.Other : NodeGroup.Map;
    }

    private bool GroupEnabled(AtlasNode n)
    {
        // Unique maps only matter while the unique completion bonus still has room.
        if (Settings.HideUniquesWhenBonusDone && n.IsUniqueMap && UniqueBonusDone
            && !string.IsNullOrEmpty(n.Area?.Name))
            return false;
        return GroupEnabledByType(n);
    }

    private bool GroupEnabledByType(AtlasNode n) => GroupOf(n) switch
    {
        NodeGroup.Pinnacle => Settings.ShowPinnacleBosses,
        NodeGroup.League => Settings.ShowLeagueBosses,
        NodeGroup.Other => Settings.ShowOtherSpecial,
        NodeGroup.Watchstone => Settings.ShowWatchstoneSlots,
        NodeGroup.Random => Settings.ShowRandomContent,
        _ => true,
    };

    // Watchstone slots are drawn and connected on the atlas, but they are sockets, not places to run.
    private static bool IsWalkable(AtlasNode n) => !n.Id.EndsWith("WatchstoneSlotNode", StringComparison.Ordinal);

    /// <summary>Every named, placed map from the game files, sorted by name. Independent of the Atlas
    /// being open, so the settings list works at any time.</summary>
    private void EnsureMapList()
    {
        if (_allMaps.Count > 0) return;
        var nodes = GameController.Game?.Files?.AtlasNodes?.EntriesList;
        if (nodes == null) return;
        _allMaps = nodes
            .Where(IsListed)
            .OrderBy(DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public override void DrawSettings()
    {
        base.DrawSettings();
        RefreshCompleted();
        EnsureMapList();
        EnsureGraph();
        ImGui.Separator();
        DrawShopSummary();
        ImGui.Separator();
        ImGui.TextUnformatted("Maps");
        DrawMapList();
    }

    /// <summary>Breadth-first search outward from every completed map over all listed nodes. The first
    /// time it reaches a node is the fewest-hops route from any completed map; <c>_dist</c> holds the hop
    /// count (completed maps are 0) and <c>_prev</c> the way back.</summary>
    private void EnsureGraph()
    {
        var key = _completedCount + "|" + _allMaps.Count;
        if (key == _graphKey) return;
        _graphKey = key;
        _dist = new Dictionary<string, int>();
        _prev = new Dictionary<string, AtlasNode>();
        if (_allMaps.Count == 0) return;

        var walkable = _allMaps.Where(IsWalkable).ToDictionary(n => n.Id);
        var queue = new Queue<AtlasNode>();
        foreach (var id in _completedIds)
            if (walkable.TryGetValue(id, out var start))
            {
                _dist[id] = 0;
                queue.Enqueue(start);
            }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in current.Connections)
            {
                if (!walkable.ContainsKey(next.Id) || _dist.ContainsKey(next.Id)) continue;
                _dist[next.Id] = _dist[current.Id] + 1;
                _prev[next.Id] = current;
                queue.Enqueue(walkable[next.Id]);
            }
        }
    }

    private void RefreshPaths()
    {
        EnsureGraph();
        var ids = Settings.PathedIds;
        var key = _graphKey + "|" + string.Join(",", ids);
        if (key == _pathKey) return;
        _pathKey = key;
        _paths = new List<List<AtlasNode>>();
        if (ids.Count == 0 || _allMaps.Count == 0) return;

        var byId = _allMaps.ToDictionary(n => n.Id);
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var target) || _completedIds.Contains(id) || !_prev.ContainsKey(id)) continue;
            var path = new List<AtlasNode> { target };
            while (_prev.TryGetValue(path[^1].Id, out var step)) path.Add(step);
            path.Reverse();
            _paths.Add(path);
        }
    }

    private void DrawPaths(IList<Element> children)
    {
        if (_paths.Count == 0) return;
        var color = Settings.PathColor.Value;
        var thickness = Settings.PathThickness.Value;

        var startColor = Settings.PathStartColor.Value;

        foreach (var path in _paths)
        {
            // The path begins at a completed map (the search's starting point), but what is drawn begins
            // at the first map you still have to run: that one is framed in the start colour and nothing
            // is drawn on the completed map or on the link back to it.
            var first = path.FindIndex(n => !_completedIds.Contains(n.Id));
            if (first < 0) continue;

            for (var i = first; i < path.Count; i++)
            {
                if (!TryGetCenter(path[i], children, out var c, out var rect)) continue;
                var box = new RectangleF(rect.X - 4, rect.Y - 4, rect.Width + 8, rect.Height + 8);
                if (!Blocked(box))
                    Graphics.DrawFrame(box, i == first ? startColor : color, i == path.Count - 1 ? thickness + 1 : 2);
                if (i > first && TryGetCenter(path[i - 1], children, out var prev, out _) && !SegmentBlocked(prev, c))
                    Graphics.DrawLine(prev, c, thickness, color);
            }
        }
    }

    private bool TryGetCenter(AtlasNode node, IList<Element> children, out Vector2 center, out RectangleF rect)
    {
        center = default;
        rect = default;
        if (!_linkById.TryGetValue(node.Id, out var link) || link.ChildIndex >= children.Count) return false;
        var e = children[link.ChildIndex];
        if (e == null) return false;
        rect = e.GetClientRect();
        center = new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);
        return true;
    }

    private static readonly System.Text.RegularExpressions.Regex TierQuery =
        new(@"^[tT]?(\d{1,2})$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The list filter searches both columns: the map name (contains, any case) and the tier.
    /// A query that is a number, optionally with a leading T ("5", "T5"), matches that exact tier, so
    /// "1" finds T1 and not T10-T16.</summary>
    private bool MatchesFilter(string name, int tier)
    {
        var query = _listFilter.Trim();
        if (query.Length == 0) return true;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        var m = TierQuery.Match(query);
        return m.Success && tier > 0 && int.Parse(m.Groups[1].Value) == tier;
    }

    private void DrawMapList()
    {
        ImGui.SetNextItemWidth(160);
        ImGui.InputText("Filter (name or tier)", ref _listFilter, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        ImGui.Combo("##show", ref _listShow, "All\0Completed\0Uncompleted\0Uncompleted that count\0Hidden on Atlas\0");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        ImGui.Combo("##sort", ref _listSort, "By name\0Nearest first\0");

        if (ImGui.Button("Clear highlights")) Settings.HighlightedIds.Clear();
        ImGui.SameLine();
        if (ImGui.Button("Clear paths")) Settings.PathedIds.Clear();
        ImGui.SameLine();
        if (ImGui.Button("Show all on Atlas")) Settings.HiddenIds.Clear();
        ImGui.TextDisabled("Highlight: mark on the Atlas.  Path: route from the nearest completed map.  Show on Atlas: untick to draw nothing for that node (hides its label, highlight and marker).");

        var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                         | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable;
        if (ImGui.BeginTable("##maps", 6, tableFlags, new Vector2(0, 420)))
        {
            ImGui.TableSetupScrollFreeze(0, 1); // keep the header row visible while scrolling
            ImGui.TableSetupColumn("Highlight");
            ImGui.TableSetupColumn("Path");
            ImGui.TableSetupColumn("Show on Atlas");
            ImGui.TableSetupColumn("Map", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Tier");
            ImGui.TableSetupColumn("Info");
            ImGui.TableHeadersRow();

            IEnumerable<AtlasNode> rows = _allMaps;
            if (_listSort == 1)
                rows = rows.OrderBy(n => _dist.TryGetValue(n.Id, out var d) ? d : int.MaxValue)
                    .ThenBy(DisplayName, StringComparer.OrdinalIgnoreCase);

            foreach (var node in rows)
            {
                if (!GroupEnabled(node)) continue;
                var name = DisplayName(node);
                var counts = CountsTowardShop(node);
                var completed = IsDone(node);
                var hidden = Settings.HiddenIds.Contains(node.Id);
                if (_listShow == 1 && !completed) continue;
                if (_listShow == 2 && completed) continue;
                if (_listShow == 3 && (completed || counts == ShopCount.No)) continue;
                if (_listShow == 4 && !hidden) continue;
                if (!MatchesFilter(name, node.BaseTier)) continue;

                ImGui.PushID(node.Id);
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                var hi = Settings.HighlightedIds.Contains(node.Id);
                if (ImGui.Checkbox("##hi", ref hi)) Toggle(Settings.HighlightedIds, node.Id, hi);

                ImGui.TableNextColumn();
                var path = Settings.PathedIds.Contains(node.Id);
                if (ImGui.Checkbox("##path", ref path)) Toggle(Settings.PathedIds, node.Id, path);

                ImGui.TableNextColumn();
                var show = !hidden;
                if (ImGui.Checkbox("##show", ref show)) Toggle(Settings.HiddenIds, node.Id, !show);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(name);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(node.BaseTier > 0 ? "T" + node.BaseTier : string.Empty);

                ImGui.TableNextColumn();
                var extra = string.Empty;
                if (completed) extra = "done";
                else
                {
                    if (counts != ShopCount.No) extra += counts == ShopCount.Yes ? "+1  " : "+1?  ";
                    if (_dist.TryGetValue(node.Id, out var hops)) extra += hops + (hops == 1 ? " hop" : " hops");
                }
                ImGui.TextUnformatted(extra.TrimEnd());
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
    }

    private string ShopLine()
    {
        var current = KiracShop.Current(_bonusCount);
        var next = KiracShop.Next(_bonusCount);
        var tail = next is { } n
            ? $"next: T{n.ShopTier} at {n.Objectives} ({n.Objectives - _bonusCount} more)"
            : "max tier";
        return $"Objectives {_bonusCount}/{KiracShop.MaxObjectives}  |  Kirac offers up to T{current.ShopTier} (level {current.MapLevel})  |  {tail}";
    }

    private void DrawShopSummary()
    {
        ImGui.TextUnformatted("Kirac's shop");
        ImGui.TextUnformatted(ShopLine());
        ImGui.ProgressBar(Math.Min(1f, _bonusCount / (float)KiracShop.MaxObjectives), new Vector2(-1, 0), $"{_bonusCount}/{KiracShop.MaxObjectives}");
        var cap = Settings.UniqueBonusCap.Value;
        ImGui.TextUnformatted($"Unique maps completed: {_uniqueCompleted}/{cap} ({(UniqueBonusDone ? "unique bonus done" : "still needed")})");

        if (!ImGui.CollapsingHeader("Shop breakpoints")) return;
        var current = KiracShop.Current(_bonusCount);
        if (ImGui.BeginTable("##shoptable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Objectives");
            ImGui.TableSetupColumn("Shop tier");
            ImGui.TableSetupColumn("Map level");
            ImGui.TableSetupColumn("Reroll cost");
            ImGui.TableHeadersRow();
            foreach (var b in KiracShop.Breakpoints)
            {
                ImGui.TableNextRow();
                if (b.Objectives == current.Objectives)
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(new System.Numerics.Vector4(0.2f, 0.6f, 0.2f, 0.5f)));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(b.Objectives.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted("T" + b.ShopTier);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(b.MapLevel.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted(b.RerollCost.ToString());
            }
            ImGui.EndTable();
        }
    }

    // The Atlas shows "<objectives>/100" in a small element inside the node container
    // (Atlas.Children[0]); its index in that list shifts, so it is found by its text and re-found
    // when the cached one stops matching.
    private static readonly System.Text.RegularExpressions.Regex ProgressText =
        new(@"^\d+\s*/\s*\d+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private int _progressIndex = -1;

    private Element FindProgressElement(IList<Element> children)
    {
        bool Matches(int i) =>
            i >= 0 && i < children.Count && children[i] != null
            && children[i].Text is { Length: > 0 } t && ProgressText.IsMatch(t.Trim());

        if (!Matches(_progressIndex))
        {
            _progressIndex = -1;
            for (var i = 0; i < children.Count; i++)
                if (children[i]?.GetClientRect().Width <= MaxNodeElementWidth && Matches(i))
                {
                    _progressIndex = i;
                    break;
                }
        }
        return _progressIndex >= 0 ? children[_progressIndex] : null;
    }

    // Kirac's shop has no marker of its own. What identifies it (confirmed on the live shop): it is a
    // purchase window whose first row is always an Orb of Unmaking (atlas passive refund). Vendor rows
    // are a flat list (every item reports slot 0,0), so "first" is just the first visible row.
    private const string OrbOfUnmakingPath = "Metadata/Items/Currency/CurrencyAtlasPassiveRefund";

    // Second signal, also confirmed live: the reroll button holds a cost that equals the shop's reroll
    // cost for the current objective count (68 objectives -> "8,000"). The cost sits in a tooltip that
    // is hidden until hover, but its text is readable, so visibility is not required.
    private static readonly System.Text.RegularExpressions.Regex RerollCostText =
        new(@"^\d{1,3}(,\d{3})+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private Element _kiracWindow;
    private Element _kiracCandidate;
    private bool _kiracCandidateIsKirac;
    private DateTime _kiracCheckedAt = DateTime.MinValue;

    private Element FindKiracShopWindow(IngameUIElements ui)
    {
        var window = new Element[] { ui?.PurchaseWindowHideout, ui?.PurchaseWindow }
            .FirstOrDefault(w => w != null && w.IsVisible);
        if (window == null)
        {
            _kiracCandidate = null;
            return null;
        }

        // Reading item paths is memory work and the shop's contents don't change while it is open, so
        // the verdict (yes or no) is kept for 250 ms.
        if (ReferenceEquals(window, _kiracCandidate) && (DateTime.UtcNow - _kiracCheckedAt).TotalMilliseconds < 250)
            return _kiracCandidateIsKirac ? window : null;

        _kiracCandidate = window;
        _kiracCheckedAt = DateTime.UtcNow;
        _kiracCandidateIsKirac = HasOrbOfUnmaking(window) || HasMatchingRerollCost(window);
        return _kiracCandidateIsKirac ? window : null;
    }

    private static bool HasOrbOfUnmaking(Element window)
    {
        try
        {
            var items = (window as ExileCore.PoEMemory.Elements.PurchaseWindow)?.TabContainer?.VisibleStash?.VisibleInventoryItems;
            if (items == null) return false;
            foreach (var item in items)
                if (item?.Item?.Path == OrbOfUnmakingPath)
                    return true;
        }
        catch
        {
            // Mid-transition reads can fail; treat as "not found" this tick.
        }
        return false;
    }

    private bool HasMatchingRerollCost(Element window)
    {
        var expected = KiracShop.Current(_bonusCount).RerollCost;
        var found = false;

        void Walk(Element e, int depth)
        {
            if (found || e == null || depth > 4) return;
            var text = e.Text?.Trim();
            if (!string.IsNullOrEmpty(text) && text.Length < 9 && RerollCostText.IsMatch(text)
                && int.TryParse(text.Replace(",", ""), out var cost) && cost == expected)
            {
                found = true;
                return;
            }
            var i = 0;
            foreach (var c in e.Children)
            {
                if (i++ >= 14) break;
                Walk(c, depth + 1);
            }
        }

        try { Walk(window, 0); }
        catch { /* see above */ }
        return found;
    }

    private void DrawShopWindowMessage(IngameUIElements ui)
    {
        _kiracWindow = Settings.ShowShopPanel && Settings.ShowOnKiracShop ? FindKiracShopWindow(ui) : null;
        if (_kiracWindow == null || ObjectivesMaxed) return;

        UpdateBlockedArea(ui);
        DrawShopLinesAbove(_kiracWindow.GetClientRect(), leftAligned: true);
    }

    /// <summary>All atlas objectives done: the shop is at its top tier, so the shop message and the
    /// uncompleted-tier list have nothing left to say.</summary>
    private bool ObjectivesMaxed => _bonusCount >= KiracShop.MaxObjectives;

    private (string Line1, string Line2) ShopLines()
    {
        var current = KiracShop.Current(_bonusCount);
        var next = KiracShop.Next(_bonusCount);
        var line2 = next is { } n
            ? $"next: T{n.ShopTier} at {n.Objectives} ({n.Objectives - _bonusCount} more)"
            : "next: max tier";
        return ($"Kirac offers up to T{current.ShopTier} (level {current.MapLevel})", line2);
    }

    /// <summary>Two lines centred above the Atlas's own objective counter. If that element can't be
    /// found, the same two lines are drawn at the configured screen position instead.</summary>
    private void DrawShopPanel(IList<Element> children)
    {
        var progress = FindProgressElement(children);
        if (progress != null && progress.IsVisible)
        {
            DrawShopLinesAbove(progress.GetClientRect());
            return;
        }

        var (line1, line2) = ShopLines();
        var size1 = Graphics.MeasureText(line1);
        var size2 = Graphics.MeasureText(line2);
        var win = GameController.Window.GetWindowRectangle();
        var x = win.Width * Settings.ShopPanelX.Value / 100f;
        var y = win.Height * Settings.ShopPanelY.Value / 100f;
        DrawShopLine(line1, new Vector2(x, y), size1);
        DrawShopLine(line2, new Vector2(x, y + size1.Y + 4), size2);
    }

    /// <summary>Both lines, centred over <paramref name="anchor"/>, the block ending 2 px above its top edge.</summary>
    private void DrawShopLinesAbove(RectangleF anchor, bool leftAligned = false)
    {
        var (line1, line2) = ShopLines();
        var size1 = Graphics.MeasureText(line1);
        var size2 = Graphics.MeasureText(line2);
        var pad = leftAligned ? Settings.ShopWindowPad.Value : 2;
        var y = anchor.Top - pad - (size1.Y + 4 + size2.Y);

        if (leftAligned)
        {
            // Inside the window: top-left corner of its rect, inset by the pad on both axes.
            var x = anchor.Left + pad;
            var top = anchor.Top + pad;
            DrawShopLine(line1, new Vector2(x, top), size1);
            DrawShopLine(line2, new Vector2(x, top + size1.Y + 4), size2);
            return;
        }

        var cx = anchor.X + anchor.Width / 2f;
        DrawShopLine(line1, new Vector2(cx - size1.X / 2f, y), size1);
        DrawShopLine(line2, new Vector2(cx - size2.X / 2f, y + size1.Y + 4), size2);
    }

    private void DrawShopLine(string text, Vector2 pos, Vector2 size)
    {
        var box = new RectangleF(pos.X - 4, pos.Y - 1, size.X + 8, size.Y + 2);
        if (Blocked(box) || TextBlocked(box)) return;
        Graphics.DrawBox(box, new Color(0, 0, 0, 190));
        Graphics.DrawText(text, pos, Color.White);
    }

    /// <summary>Same shape as BetrayalHelperRevamped's hideout intel warning: only in a hideout, an ImGui
    /// window that stays until dismissed. 'Acknowledge' is remembered per breakpoint (saved with the
    /// settings); the X / 'Later' only hide it until the next area.</summary>
    private void DrawRefreshAlert()
    {
        if (!Settings.RefreshAlert || _closedThisArea || _completedCount < 0) return;
        if (GameController.Area?.CurrentArea?.Area?.IsHideout != true) return;

        var messages = new List<string>();
        var current = KiracShop.Current(_bonusCount);
        if (current.Objectives > 0 && Settings.AckRefreshBreakpoint < current.Objectives)
            messages.Add($"Kirac's shop has refreshed: it now offers maps up to T{current.ShopTier} (level {current.MapLevel}). Buy maps!");

        var next = KiracShop.Next(_bonusCount);
        var soon = next is { } n && n.Objectives - _bonusCount == 1 && Settings.AckSoonBreakpoint < n.Objectives;
        if (soon) messages.Add($"1 more atlas objective and Kirac's shop refreshes up to T{next.Value.ShopTier} (level {next.Value.MapLevel}).");

        if (messages.Count == 0) return;

        ImGui.SetNextWindowPos(new Vector2(800, 500), ImGuiCond.Once);
        ImGui.SetNextWindowSize(new Vector2(560, 130), ImGuiCond.Once);
        var open = true;
        if (ImGui.Begin("Kirac Shop Alert", ref open, ImGuiWindowFlags.NoCollapse))
        {
            foreach (var m in messages) ImGui.TextWrapped(m);
            ImGui.Spacing();
            if (ImGui.Button("Acknowledge"))
            {
                if (current.Objectives > 0) Settings.AckRefreshBreakpoint = current.Objectives;
                if (soon) Settings.AckSoonBreakpoint = next.Value.Objectives;
                _closedThisArea = true;
            }
            ImGui.SameLine();
            if (ImGui.Button("Later")) _closedThisArea = true;
        }
        ImGui.End();
        if (!open) _closedThisArea = true;
    }

    // Random Content nodes currently shown on the Atlas, and the ones the user already acknowledged.
    // An acknowledgement is forgotten once the node leaves the Atlas, so its next appearance alerts again.
    private readonly HashSet<string> _ackedRandom = new();

    private void DrawRandomContentAlert(IList<Element> children)
    {
        if (!Settings.RandomContentAlert || !Settings.ShowRandomContent) return;

        var shown = new List<AtlasNode>();
        foreach (var link in _links)
        {
            if (link.ChildIndex >= children.Count) continue;
            var element = children[link.ChildIndex];
            if (element == null || !element.IsVisible) continue;
            var node = link.Node;
            if (GroupOf(node) != NodeGroup.Random) continue;
            if (Settings.HiddenIds.Contains(node.Id)) continue;
            shown.Add(node);
        }

        _ackedRandom.RemoveWhere(id => !shown.Any(n => n.Id == id));
        var pending = shown.Where(n => !_ackedRandom.Contains(n.Id)).ToList();
        if (pending.Count == 0) return;

        ImGui.SetNextWindowPos(new Vector2(800, 300), ImGuiCond.Once);
        ImGui.SetNextWindowSize(new Vector2(460, 130), ImGuiCond.Once);
        var open = true;
        if (ImGui.Begin("Random Content Alert", ref open, ImGuiWindowFlags.NoCollapse))
        {
            foreach (var n in pending) ImGui.TextWrapped($"Random Content on the Atlas: {DisplayName(n)}");
            ImGui.Spacing();
            if (ImGui.Button("Acknowledge"))
                foreach (var n in pending) _ackedRandom.Add(n.Id);
        }
        ImGui.End();
        // Closing with the X counts as acknowledging, otherwise it would reopen next frame.
        if (!open)
            foreach (var n in pending) _ackedRandom.Add(n.Id);
    }

    private static void Toggle(List<string> list, string id, bool on)
    {
        if (on) { if (!list.Contains(id)) list.Add(id); }
        else list.Remove(id);
    }
}
