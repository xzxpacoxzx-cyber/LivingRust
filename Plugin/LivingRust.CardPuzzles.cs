using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using LivingRust.Models;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Card-reader puzzle solving (2026-08-17, Lucas's own explicit spec) - a
/// survivor already carrying the matching-tier keycard(s) AND a real fuse
/// (fuse/fuse.highgrade - "either or, not 1 for a specific monument") can
/// now actually solve a monument's puzzle room instead of just avoiding it
/// (see CardReaderAvoidRadius, LivingRust.Looting.cs, the old stopgap).
/// Opportunistic only, per Lucas's own explicit choice - this never sends a
/// survivor hunting for a card/fuse it doesn't already have, it only
/// detours here if it's already carrying everything the route needs.
///
/// Built entirely on the existing ghost-route engine (GhostRouteWaypoint/
/// TryLoadTraceWaypoints/ProjectGhostRouteToMonument/StartGhostRoute,
/// LivingRust.Looting.cs) - a puzzle route IS a ghost route, just triggered
/// by a different condition (holding the right items) than the normal
/// per-monument loot folders (MonumentGhostRouteFolders), and reprojected
/// onto whichever real monument instance is actually nearby the same way.
///
/// Most monuments have exactly one reader needing one tier. Military Tunnel
/// (2026-08-18, Lucas's own real description) is a genuinely different
/// shape - ONE continuous route through three separate gated doors, each
/// needing its own specific keycard the survivor must already be carrying
/// (not a cascading reward like harbor_2's blue-card drop): green keycard
/// opens a room with a backup generator (fusebox+switch, same pattern as
/// every other monument) that powers the blue door; blue keycard opens
/// access to the red door; red door has its own switch (flip first), then
/// the red keycard. This means a single route can now need swiping
/// MULTIPLE different-tier readers, so the interaction logic below
/// determines which tier EACH specific reader needs from its own real
/// accessLevel field, rather than one fixed tier for the whole route.
///
/// The real interactions (fusebox/switch/reader) are confirmed real
/// Assembly-CSharp API via a live reflection/IL dump against the game's own
/// managed DLL, not guessed:
///   ElectricSwitch.IsOn()/SetSwitch(bool) - IsOn() is inherited
///   BaseEntity.IsOn() (real Flags.On check).
///   ItemBasedFlowRestrictor.HasPassthroughItem()/IsValidPassthroughItem() -
///   its own inventory field is PRIVATE (unlike Recycler's public one).
///   Type.GetField("inventory", NonPublic) confirmed FAILING at runtime on
///   this server build (2026-08-18, live-tested via /lr.debug.inspectfusebox
///   before/after a real manual insert) despite the field definitely
///   existing and definitely being populated at spawn (confirmed via a live
///   IL dump of ServerInit()/CreateInventory()) - plain direct field access
///   (fusebox.inventory) is used instead, which compiles fine against
///   Carbon's publicized reference assembly and isn't affected by whatever
///   is stripping the reflection metadata at runtime.
///   CardReader.GrantCard()/FailCard() - no-arg, no built-in validation
///   (the real ServerCardSwiped RPC does that itself before choosing one of
///   these) - safe to call GrantCard() directly here ONLY because the tier
///   check happens ourselves first (see TrySwipeCard's own doc comment).
///   CardReader.accessLevel (public Int32) - confirmed via IL that
///   ServerCardSwiped reads this AND the swiped Keycard's own accessLevel
///   property to validate a swipe - the real tier-matching mechanism. The
///   exact int<->tier mapping (KeycardTierByReaderAccessLevel below) is a
///   best-guess (0/1/2 = Green/Blue/Red, the standard ordering) NOT yet
///   empirically confirmed against a live reader - TrySwipeCard logs the
///   raw accessLevel every attempt specifically so the first live Military
///   Tunnel test can confirm or correct it.
/// Keycard uses are real Item.condition durability, confirmed via the
/// actual item definition JSON (Bundles/items/keycard_*.json):
/// keycard_green/keycard_blue max 4.0, keycard_red max 2.0, not repairable -
/// 1.0 lost per successful swipe here, matching that real economy.
/// </summary>
public partial class LivingRust
{
    private enum KeycardTier
    {
        Green,
        Blue,
        Red,
    }

    private static readonly Dictionary<KeycardTier, string> KeycardShortnames = new()
    {
        [KeycardTier.Green] = "keycard_green",
        [KeycardTier.Blue] = "keycard_blue",
        [KeycardTier.Red] = "keycard_red",
    };

    /// <summary>
    /// Real CardReader.accessLevel -> KeycardTier mapping, 1-indexed not
    /// 0-indexed (2026-08-18, corrected via live data: a scan of Sphere
    /// Tank/"Dome"'s reader - a known real green-only puzzle - reported
    /// accessLevel=1, not 0 as the original 0-indexed guess assumed).
    /// Green confirmed live; Blue/Red (2/3) are Lucas's own logical
    /// extension pending his own confirmation scan of a real blue/red
    /// reader - TrySwipeCard/inspectcardreader still log the raw int every
    /// time so those get a hard confirm too, not just an assumption.
    /// </summary>
    private static readonly Dictionary<int, KeycardTier> KeycardTierByReaderAccessLevel = new()
    {
        [1] = KeycardTier.Green,
        [2] = KeycardTier.Blue,
        [3] = KeycardTier.Red,
    };

    /// <summary>
    /// Same "does this survivor actually have a real, usable one" check
    /// TryStartCardPuzzleDetour's own keycard loop already does inline
    /// (condition <= 0 means a durability-depleted card, treated the same
    /// as not owning one) - pulled out here as its own helper (2026-09-15)
    /// so TryStartWithGearWeightedDestination (LivingRust.GearScore.cs) can
    /// reuse the exact same definition of "usable" for its own keycard-tier
    /// destination bias, without the two ever drifting apart.
    /// </summary>
    private static bool HasUsableKeycard(BasePlayer npc, KeycardTier tier)
    {
        Item keycard = npc.inventory.FindItemByItemName(KeycardShortnames[tier]);
        return keycard != null && keycard.condition > 0f;
    }

    /// <summary>
    /// Real shortnames confirmed via Bundles/items/fuse*.json - "electric
    /// fuse" and "heavy fuse" in Lucas's own words are fuse/fuse.highgrade
    /// in-game. Either one satisfies any puzzle tier (2026-08-17, Lucas's
    /// own explicit correction: "the tougher tiers don't need heavy_fuse,
    /// they just require either or, not 1 for a specific monument").
    /// </summary>
    private static readonly string[] CardPuzzleFuseShortnames = { "fuse", "fuse.highgrade" };

    /// <summary>
    /// Monument substring -> (authored puzzle-route folder, EVERY keycard
    /// tier this route needs somewhere along it - most monuments need just
    /// one, Military Tunnel needs all three). Same substring-match
    /// convention as MonumentGhostRouteFolders. Harbor (specifically
    /// harbor_2, not harbor_1/ferry_terminal_1 - all three are real
    /// distinct monuments sharing one "harbor" asset folder, confirmed via
    /// scanmonumentloot log output, same caveat as Harbor2_A's own
    /// registry entry) - green tier, 1 path recorded by Lucas 2026-08-17:
    /// fusebox -> switch -> reader -> loot room -> leave, confirmed via a
    /// live /lr.debug.scan at each of the 4 recorded dwell stops.
    /// </summary>
    private static readonly Dictionary<string, (string Folder, KeycardTier[] RequiredTiers, int RequiredFuseCount)> CardPuzzleRouteFolders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["harbor_2"] = ("Cardreader_A", new[] { KeycardTier.Green }, 1),

            // Traced 2026-08-18 - harbor_1 (a distinct real monument from
            // harbor_2/ferry_terminal_1, same "harbor" asset-folder caveat
            // as above), also green tier, 1 path recorded by Lucas. The
            // monument's own general loot route was deliberately skipped
            // (only 3-4 lootable containers - "juice isn't worth the
            // squeeze" for a dedicated Harbor1_A ghost route) but the
            // puzzle room's reward loot is separate from that ambient count
            // and worth solving regardless - registered on its own here.
            ["harbor_1"] = ("Cardreader_B", new[] { KeycardTier.Green }, 1),

            // Traced 2026-08-18 - Satellite Dish (real prefab confirmed via
            // AssetSceneManifest.json: monument/small/satellite_dish.prefab),
            // green tier, 1 path recorded by Lucas.
            ["satellite_dish"] = ("Cardreader_C", new[] { KeycardTier.Green }, 1),

            // Traced 2026-08-18 - Radtown (real prefab "radtown_1", the
            // plain roadside monument - distinct from "radtown_small",
            // which is Sewer Branch, a Medium-tier monument), green tier,
            // 1 path recorded by Lucas. Fuse requirement is unconditional
            // regardless of tier - Lucas's own confirmation, 2026-08-18.
            ["radtown_1"] = ("Cardreader_D", new[] { KeycardTier.Green }, 1),

            // Traced 2026-08-19 - Military Tunnel, ONE continuous route
            // through all three gated doors (see this file's own top doc
            // comment for the real green->generator->blue->red flow, Lucas's
            // own description). Needs all three tiers held simultaneously
            // to even commit (plus a fuse, same as every other puzzle) -
            // Lucas's own explicit spec: "it just requires all three
            // keycards along the way." Deliberately points at the SAME
            // MilitaryTunnel_A folder as the normal loot route (not a
            // dedicated Cardreader_X folder like every other monument) -
            // Lucas's own explicit correction, 2026-08-19: the existing 5
            // loot-route traces already walk past every fusebox/switch/
            // reader stop on their own, so the puzzle detour reuses them
            // rather than needing a separate recording.
            ["military_tunnel"] = ("MilitaryTunnel_A", new[] { KeycardTier.Green, KeycardTier.Blue, KeycardTier.Red }, 1),

            // Traced 2026-08-18 - Sphere Tank ("Dome", real prefab
            // monument/small/sphere_tank.prefab), green tier. Extracted
            // from a much longer continuous recording session that also
            // covered Radtown earlier in the same file - the Sphere Tank
            // segment sat further along (elapsed_s ~965-990) near its own
            // reader's real position, confirmed via a live
            // /lr.debug.inspectcardreader scan (accessLevel=1). Lucas's own
            // general pattern, 2026-08-18: every Tier 1 monument is green-
            // only (accessLevel 1); Tier 2 needs green+blue (1+2); Tier 3
            // varies per monument (some 1+3, some just 2, Military Tunnel
            // an outlier needing all three) - to be confirmed individually
            // as each gets traced.
            ["sphere_tank"] = ("Cardreader_F", new[] { KeycardTier.Green }, 1),

            // Traced 2026-08-19 - Ferry Terminal (real prefab
            // "ferry_terminal_1", distinct from harbor_1/harbor_2 despite
            // sharing the "harbor" asset folder - same caveat noted on the
            // harbor_2 entry above), green tier, 1 path recorded by Lucas.
            ["ferry_terminal"] = ("Cardreader_G", new[] { KeycardTier.Green }, 1),

            // Traced 2026-08-19 - Powerplant, Tier 2/Medium monument. Green
            // gates the generator/switch room, blue is the actual reader
            // (accessLevel=2, live-confirmed earlier via
            // /lr.debug.inspectcardreader) - matches the established Tier 2
            // pattern (green+blue), Lucas's own confirmation. First 3
            // recorded paths disregarded same day - they stopped right at
            // the door immediately after the swipe instead of continuing
            // through the puzzle room and out the far side, leaving the bot
            // with nowhere left to walk once the route "finished." 1 good
            // complete path recorded and confirmed by Lucas.
            ["powerplant"] = ("Cardreader_H", new[] { KeycardTier.Green, KeycardTier.Blue }, 1),

            // Traced 2026-08-19 - Sewer Branch (real prefab "radtown_small"
            // - reclassified from Medium to Low/Tier 1 same day, Lucas's
            // own explicit correction: its puzzle only needs a green
            // keycard + fuse, same as every other Tier 1 monument, unlike
            // its Medium-tier name-based default assumed). Green tier, 1
            // path recorded by Lucas - awaiting a real live test.
            ["radtown_small"] = ("Cardreader_I", new[] { KeycardTier.Green }, 1),

            // Traced 2026-08-19 - Arctic Research Base, Tier 2/Medium
            // monument. Blue keycard ONLY, no fuse at all - Lucas's own
            // explicit spec, the first monument that breaks the "every
            // puzzle needs a fuse" assumption every other entry here relies
            // on (see the RequiredFuseCount field, TryStartCardPuzzleDetour).
            // 1 path recorded by Lucas.
            ["arctic_research_base"] = ("Cardreader_J", new[] { KeycardTier.Blue }, 0),

            // Traced 2026-08-20 - Airfield, Tier 2/Medium monument. Needs
            // green + blue AND 2 fuses of any type (either fuse or
            // fuse.highgrade, mixed or matched - Lucas's own explicit spec,
            // "2 Fuses (of any type)") - the first monument needing more
            // than 1 fusebox filled, which is why RequiredFuseCount is an
            // int rather than the old bool. 1 path recorded by Lucas.
            ["airfield"] = ("Cardreader_K", new[] { KeycardTier.Green, KeycardTier.Blue }, 2),

            // Traced 2026-08-20 - Water Treatment Plant, Tier 2/Medium
            // monument. Blue keycard + 1 fuse only, no green - Lucas's own
            // explicit spec. Includes a real WheelSwitch-driven roller door
            // (see TryStartWheelTurn/TryResolveConnectedDoor's own doc
            // comments) - 1 full path recorded by Lucas, replacing the
            // earlier bare wheel-hold test trace.
            ["water_treatment_plant"] = ("Cardreader_L", new[] { KeycardTier.Blue }, 1),

            // Traced 2026-08-21 - Nuclear Missile Silo, Tier 3/High
            // monument. Red keycard only, no fuse - Lucas's own explicit
            // spec, confirmed via a live inspect-switch scan. Single way in
            // and out, so puzzle and loot are one combined trace rather
            // than a separate puzzle + loot pair - reuses
            // MonumentGhostRouteFolders' own "nuclear_missile_silo" entry
            // (NuclearMissileSilo_A) rather than a dedicated Cardreader_N
            // folder. Heavy elevator use throughout - Lucas flagged
            // elevators as needing dedicated interaction handling, not yet
            // implemented.
            ["nuclear_missile_silo"] = ("NuclearMissileSilo_A", new[] { KeycardTier.Red }, 0),

            // Scaffolded 2026-08-22 - Launch Site, Tier 3/High monument.
            // Green + Red keycards, 2 fuses - Lucas's own explicit spec.
            // Loot and puzzle paths are the same route (Lucas's own
            // framing, "basically identical, no requirement for loot
            // traces") - reuses MonumentGhostRouteFolders' own
            // "launch_site" entry (LaunchSite_A) rather than a dedicated
            // Cardreader_N folder, same combined-trace pattern as Nuclear
            // Missile Silo above. Folder currently empty - waiting on
            // Lucas's real recording pass.
            ["launch_site"] = ("LaunchSite_A", new[] { KeycardTier.Green, KeycardTier.Red }, 2),
        };

    /// <summary>
    /// How close the survivor needs to be to a real fusebox/switch/reader
    /// for TryHandleCardPuzzleInteractions to act on it - tight on purpose,
    /// these are small props (confirmed via the live scan: fusebox/switch
    /// bounding boxes are well under 1m), not a whole loot zone.
    /// </summary>
    private const float CardPuzzleInteractionRadius = 3f;

    /// <summary>
    /// Real WheelSwitch.RotateProgress() distance-to-rotator cutoff
    /// (confirmed via IL: a flat 2m Vector3Ex.Distance2D check, X/Z only)
    /// - narrower than CardPuzzleInteractionRadius (3m), which gates
    /// whether TryStartWheelTurn gets called at all. 1.8m, not the full 2m,
    /// as a small safety margin against floating-point noise sitting right
    /// at the real boundary - 2026-08-20, real live bug: a bot arriving
    /// between 2-3m from a wheel used to get detected and start turning,
    /// then immediately self-cancel on the very first RotateProgress tick.
    /// </summary>
    private const float WheelSwitchMaxRotateDistance2D = 1.8f;

    /// <summary>
    /// Real live-confirmed hold duration (Lucas's own direct observation,
    /// 2026-08-20, watching a real player hold this exact wheel) - a
    /// continuous, uninterrupted 8s hold guarantees the door fully opens.
    /// Used as the PRIMARY completion signal alongside (not instead of)
    /// door.openProgress reaching 1 - whichever happens first. Explicit
    /// time-based completion is more robust than trusting the door's own
    /// energy simulation alone, since that also has to account for its own
    /// real decay-while-not-held behaviour Lucas separately confirmed
    /// ("once the player or bot lets go... the door automatically starts
    /// coming down") - small real-world timing/tick jitter in that energy
    /// math could otherwise leave openProgress just short of 1 even after
    /// a genuinely full real hold.
    /// </summary>
    private const float WheelHoldCompletionSeconds = 8f;

    /// <summary>
    /// Real-world timestamp (UnityEngine.Time.realtimeSinceStartup) a
    /// wheel's CURRENT unbroken hold began - keyed by wheel NetworkableId,
    /// same scope as _activeWheelTurnTimers. Reset (removed) the instant a
    /// hold is interrupted, same as _activeWheelTurnTimers - "genuine hold
    /// time" (Lucas's own framing) means continuous, so a broken-then-
    /// resumed hold starts counting from zero again, not from wherever it
    /// left off.
    /// </summary>
    private readonly Dictionary<NetworkableId, float> _wheelHoldStartTimes = new();

    /// <summary>
    /// Which survivors currently have an active card-puzzle detour running -
    /// set the instant TryStartCardPuzzleDetour/TryTeleportToCardPuzzle
    /// commits, cleared the instant that detour ends (success or failure).
    /// Read by TryHandleCardPuzzleInteractions to decide WHETHER to touch a
    /// nearby fusebox/switch/reader at all - never during a normal loot
    /// route that just happens to pass within CardPuzzleInteractionRadius
    /// of one (flipping a switch or burning a fuse outside of an actual
    /// committed attempt would only waste them and falsely "solve" the
    /// entry gate for a real attempt later). A plain marker now, not a
    /// single fixed tier (2026-08-18) - a multi-door route like Military
    /// Tunnel needs to keep reacting to readers of DIFFERENT tiers for the
    /// whole route, not stop after the first successful swipe the way a
    /// single-tier puzzle correctly did before.
    /// </summary>
    private readonly HashSet<Guid> _activeCardPuzzleSurvivors = new();

    /// <summary>
    /// Which specific real CardReader entities a survivor has already
    /// swiped (or determined it can't) THIS puzzle attempt - prevents
    /// re-attempting the same reader every time the recorded route passes
    /// near it across several close-together waypoints (collapse distance
    /// is only 0.4m - GhostRouteWaypointCollapseDistance). Cleared the
    /// instant the puzzle detour ends, same lifecycle as
    /// _activeCardPuzzleSurvivors.
    /// </summary>
    private readonly Dictionary<Guid, HashSet<NetworkableId>> _handledReadersThisPuzzle = new();

    /// <summary>
    /// Which keycard tiers THIS survivor's active puzzle attempt actually
    /// requires (the same array TryGetCardPuzzleRoute resolved when the
    /// detour committed) - 2026-08-20, real live bug: Water Treatment
    /// Plant physically has a green CardReader and a blue CardReader
    /// sitting at the exact same spot (confirmed via a live /lr.debug.scan),
    /// but its registered puzzle only needs blue. TrySwipeCard used to
    /// determine which card to swipe purely from reader.accessLevel with
    /// zero awareness of what this specific route actually needs - a
    /// survivor carrying a green card (the common Tier 1 default most other
    /// monuments use) walking past the co-located green reader got it
    /// swiped too, burning real durability on a tier this puzzle never
    /// asked for. Same lifecycle as _handledReadersThisPuzzle.
    /// </summary>
    private readonly Dictionary<Guid, KeycardTier[]> _activePuzzleRequiredTiers = new();

    /// <summary>
    /// Which real WheelSwitch entities a survivor has already given a full
    /// WheelHoldCompletionSeconds hold to - 2026-08-20, real live bug:
    /// without this, "finished turning the wheel" only ever meant "stop
    /// holding for now," not "never touch this wheel again." The door
    /// decays back down within a second or two of being released (Lucas's
    /// own live-confirmed behaviour), so the very next waypoint scan found
    /// door.openProgress &lt; 1 again and immediately restarted the whole 8s
    /// hold - live log evidence: 'JumpyProwler39' held the same wheel for
    /// 8.0s, finished, restarted, finished, restarted... on an unbroken
    /// ~8-9s cycle with no sign of ever stopping. Once a wheel's in this
    /// set for this survivor, TryStartWheelTurn refuses to start it again
    /// regardless of the door's live state afterward, letting the ghost
    /// route move on for good.
    ///
    /// Pre-created (and cleared on completion) at TryStartCardPuzzleDetour/
    /// TryTeleportToCardPuzzle for a puzzle attempt, same lifecycle as
    /// _handledReadersThisPuzzle - but ALSO lazily created on first use
    /// inside TryStartWheelTurn itself (2026-08-20), since
    /// TryHandleFreeMonumentInteractions calls wheel-handling
    /// unconditionally on every ghost route, not just committed puzzle
    /// ones. Known limitation of the lazy path: a normal loot-route entry
    /// is never cleared (no puzzle-complete callback exists to clear it),
    /// so a given survivor only ever turns a given wheel once, ever, across
    /// its whole lifetime on a plain loot route - a later separate visit
    /// won't re-attempt it even if the door's since closed again. Accepted
    /// for now (Lucas's own call, 2026-08-20 - avoiding the infinite-loop
    /// bug mattered more tonight than handling repeat visits perfectly);
    /// revisit if repeat-visit behaviour turns out to matter in practice.
    /// </summary>
    private readonly Dictionary<Guid, HashSet<NetworkableId>> _handledWheelsThisPuzzle = new();

    /// <summary>
    /// Which real WheelSwitch entities currently have an active turn timer
    /// running (see TryStartWheelTurn) - keyed globally by the wheel's own
    /// NetworkableId, not per-survivor, since only one bot realistically
    /// works a given wheel at a time and the wheel's own real
    /// rotateProgress/rotatorPlayer state (not this dictionary) is the
    /// actual source of truth for "is this done." Purely prevents
    /// restarting a second overlapping tick loop for the same wheel if the
    /// recorded route's dwell keeps re-triggering the interaction scan
    /// across several close waypoints during the same real hold.
    /// </summary>
    private readonly Dictionary<NetworkableId, Timer> _activeWheelTurnTimers = new();

    /// <summary>
    /// Elevator/ElevatorLift entities (2026-08-21, Nuclear Missile Silo -
    /// its trace is heavy on elevator use throughout) already triggered
    /// this ghost route, per survivor - same lazy-create/never-clear-on-
    /// plain-routes contract as _handledWheelsThisPuzzle (see its own doc
    /// comment for why: cleared on card-puzzle commit/complete, lazily
    /// created and left set for the rest of a bot's lifetime on ordinary
    /// loot routes). Keyed by the SPECIFIC entity's own NetworkableId, so
    /// a call panel (Elevator/ElevatorStatic) and the cabin itself
    /// (ElevatorLift/ElevatorLiftStatic) that it eventually rides are
    /// tracked as two independent "already handled" entries even though
    /// they're the same shaft - both need their own single trigger per
    /// route (call once, then ride once), not a single combined flag.
    /// </summary>
    private readonly Dictionary<Guid, HashSet<NetworkableId>> _handledElevatorsThisRoute = new();

    /// <summary>
    /// Same substring-match lookup as TryGetGhostRouteForMonument, just
    /// against CardPuzzleRouteFolders instead.
    /// </summary>
    private static bool TryGetCardPuzzleRoute(string monumentName, out string folder, out KeycardTier[] requiredTiers, out int requiredFuseCount)
    {
        folder = null;
        requiredTiers = null;
        requiredFuseCount = 1;

        if (string.IsNullOrEmpty(monumentName))
        {
            return false;
        }

        foreach (KeyValuePair<string, (string Folder, KeycardTier[] RequiredTiers, int RequiredFuseCount)> entry in CardPuzzleRouteFolders)
        {
            if (monumentName.IndexOf(entry.Key, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            folder = entry.Value.Folder;
            requiredTiers = entry.Value.RequiredTiers;
            requiredFuseCount = entry.Value.RequiredFuseCount;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Same random-pick-from-folder pattern as TryGetGhostRouteForMonument -
    /// deliberately a separate folder tree (Cardreader_A etc.) from the
    /// per-monument loot routes, since this is a distinct opportunistic
    /// detour, not the main loot pass through the monument.
    /// </summary>
    private bool TryGetCardPuzzleRouteFile(string folder, out string traceFilePath)
    {
        traceFilePath = null;

        string folderPath = $"{TraceDirectory}/{folder}";

        if (!Directory.Exists(folderPath))
        {
            return false;
        }

        string[] candidates = Directory.GetFiles(folderPath, "*.csv");

        if (candidates.Length == 0)
        {
            return false;
        }

        string chosen = candidates[UnityEngine.Random.Range(0, candidates.Length)];
        traceFilePath = chosen.Replace('\\', '/');
        return true;
    }

    /// <summary>
    /// Real proximity search for AN ElectricSwitch along this puzzle
    /// route, used ONLY for the entry-gate check below - searches every
    /// recorded waypoint (not a hardcoded index) so this keeps working
    /// regardless of where in a future monument's recording the switch
    /// stop(s) fall. Deliberately returns the FIRST one found (a
    /// multi-switch route like Military Tunnel has more than one - the
    /// entry gate only needs ANY of them already on to conclude "this has
    /// been solved recently, skip it", not to check all of them
    /// individually). Same CardReaderLayerMask ("World", confirmed via a
    /// live scan) IsNearCardReader already uses - a switch/fusebox not
    /// living on that layer would just mean this silently never finds one,
    /// safely falling through to "start the route anyway" rather than a
    /// hard failure.
    /// </summary>
    private static bool TryFindCardPuzzleSwitch(List<GhostRouteWaypoint> waypoints, out ElectricSwitch found)
    {
        foreach (GhostRouteWaypoint waypoint in waypoints)
        {
            Collider[] hits = Physics.OverlapSphere(waypoint.Position, CardPuzzleInteractionRadius, CardReaderLayerMask, QueryTriggerInteraction.Collide);

            foreach (Collider hit in hits)
            {
                ElectricSwitch candidate = hit.GetComponentInParent<ElectricSwitch>();

                if (candidate != null && !candidate.IsDestroyed)
                {
                    found = candidate;
                    return true;
                }
            }
        }

        found = null;
        return false;
    }

    /// <summary>
    /// Test-only shortcut (2026-08-18, Lucas's own explicit request: "just
    /// want to test the bot using the keycard and toggling the switch...
    /// not super stressed about having the bot do all the things leading up
    /// to that point") - teleports straight to the puzzle route's own first
    /// waypoint instead of a real StartLongDistanceWalk approach, so testing
    /// the actual fuse/switch/card interactions isn't gated on the
    /// unrelated cross-map pathing reliability (bollards, stuck-recovery,
    /// etc.) TryStartCardPuzzleDetour's real approach walk depends on.
    /// Still runs the real entry gate (switch already on) and the real
    /// interaction logic (TryHandleCardPuzzleInteractions via StartGhostRoute)
    /// unchanged - only the WALK there is skipped, not any of the actual
    /// behaviour being tested.
    /// </summary>
    private bool TryTeleportToCardPuzzle(Survivor survivor, MonumentInfo monument, out string failureReason)
    {
        BasePlayer npc = survivor.Player;
        failureReason = null;

        if (npc == null || npc.IsDestroyed)
        {
            failureReason = "no live BasePlayer.";
            return false;
        }

        if (!TryGetCardPuzzleRoute(monument.name, out string folder, out KeycardTier[] requiredTiers, out int requiredFuseCount))
        {
            failureReason = $"'{monument.name}' has no registered card-puzzle route.";
            return false;
        }

        if (!TryGetCardPuzzleRouteFile(folder, out string traceFilePath)
            || !TryLoadTraceWaypoints(traceFilePath, out List<GhostRouteWaypoint> puzzleWaypoints, out MonumentInfo recordedAtMonument, monument.name)
            || puzzleWaypoints.Count == 0)
        {
            failureReason = $"couldn't load a route from '{folder}' - does it have any .csv traces yet?";
            return false;
        }

        List<GhostRouteWaypoint> worldWaypoints = recordedAtMonument != null
            ? ProjectGhostRouteToMonument(puzzleWaypoints, monument)
            : puzzleWaypoints;

        if (TryFindCardPuzzleSwitch(worldWaypoints, out ElectricSwitch existingSwitch) && existingSwitch.IsOn())
        {
            failureReason = "the real puzzle switch is already on - already solved recently. Flip it off yourself (or find an unsolved instance) to test a fresh attempt.";
            return false;
        }

        Guid characterId = survivor.Character.Id;
        Vector3 startPosition = worldWaypoints[0].Position;

        npc.transform.position = startPosition;
        npc.MovePosition(startPosition);
        npc.SendNetworkUpdateImmediate();

        _activeCardPuzzleSurvivors.Add(characterId);
        _handledReadersThisPuzzle[characterId] = new HashSet<NetworkableId>();
        _handledWheelsThisPuzzle[characterId] = new HashSet<NetworkableId>();
        _activePuzzleRequiredTiers[characterId] = requiredTiers;

        Action onPuzzleComplete = () =>
        {
            _activeCardPuzzleSurvivors.Remove(characterId);
            _handledReadersThisPuzzle.Remove(characterId);
            _handledWheelsThisPuzzle.Remove(characterId);
            _activePuzzleRequiredTiers.Remove(characterId);
            survivor.Character.CurrentTask = TaskType.None;
        };

        _pendingGhostRouteToResume[characterId] = (worldWaypoints, 0, onPuzzleComplete);
        StartGhostRouteLootScan(survivor, onPuzzleComplete);
        StartGhostRoute(survivor, worldWaypoints, 0, onPuzzleComplete);

        return true;
    }

    /// <summary>
    /// Opportunistic card-puzzle detour, checked from EscalateSearchToMonumentZone
    /// right before the normal per-monument ghost route (2026-08-17, Lucas's
    /// own explicit spec) - returns true and takes over entirely the instant
    /// it commits, exactly the same calling convention the existing ghost-
    /// route commit block already uses. Capped at one attempt per monument
    /// per task (CardPuzzleVisitedMonuments) so finishing/abandoning it and
    /// resuming ContinueLootTask nearby can't immediately re-trigger it in a
    /// loop - a fresh task is free to try again on a later visit.
    /// </summary>
    private static int GetPuzzleMinGearScore(MonumentTier tier)
    {
        return tier switch
        {
            MonumentTier.TierZero => 0,
            MonumentTier.TierOne => LowTierBandMin,
            MonumentTier.TierTwo => MediumTierBandMin,
            _ => HighTierBandMin,
        };
    }

    private bool TryStartCardPuzzleDetour(Survivor survivor, MonumentInfo monument, LootTaskState state)
    {
        BasePlayer npc = survivor.Player;

        if (npc == null || npc.IsDestroyed
            || state.CardPuzzleVisitedMonuments.Contains(monument.name)
            || !TryGetCardPuzzleRoute(monument.name, out string folder, out KeycardTier[] requiredTiers, out int requiredFuseCount))
        {
            return false;
        }

        // Gear-score gate lives HERE and only here (2026-09-21, Lucas's own
        // explicit spec): a survivor may walk any monument's loot path at
        // any gear score, but the card puzzle rooms are where scientists
        // roam, so the puzzle itself requires the gear band of its
        // monument's own tier.
        if (GetGearScore(npc) < GetPuzzleMinGearScore(GetMonumentTier(monument.name)))
        {
            return false;
        }

        // Opportunistic only - never sends a survivor hunting for a card or
        // fuse it doesn't already have. Requires EVERY tier this route
        // needs (Military Tunnel needs all three simultaneously - Lucas's
        // own explicit spec), not just one. condition <= 0 means a real
        // durability-depleted card sitting in inventory unremoved for some
        // reason - treat it the same as not having one.
        //
        // Fuse requirement is now a per-monument COUNT (RequiredFuseCount,
        // 2026-08-19/20) - Arctic Research Base was the first exception to
        // the old "every puzzle needs exactly 1 fuse" assumption (needs 0),
        // and Airfield is the first needing MORE than 1 (2, any mix of
        // fuse/fuse.highgrade - Lucas's own explicit spec). Summed by
        // .amount, not item count, since both fuse types are stackable
        // (fuse up to 10, fuse.highgrade up to 3) - two separate fuses
        // could be two Items OR one stack with amount=2.
        if (requiredFuseCount > 0)
        {
            int heldFuseCount = npc.inventory.containerMain.itemList
                .Concat(npc.inventory.containerBelt.itemList)
                .Where(item => Array.IndexOf(CardPuzzleFuseShortnames, item.info.shortname) >= 0)
                .Sum(item => item.amount);

            if (heldFuseCount < requiredFuseCount)
            {
                return false;
            }
        }

        foreach (KeycardTier tier in requiredTiers)
        {
            Item keycard = npc.inventory.FindItemByItemName(KeycardShortnames[tier]);

            if (keycard == null || keycard.condition <= 0f)
            {
                return false;
            }
        }

        if (!TryGetCardPuzzleRouteFile(folder, out string traceFilePath)
            || !TryLoadTraceWaypoints(traceFilePath, out List<GhostRouteWaypoint> puzzleWaypoints, out MonumentInfo recordedAtMonument, monument.name)
            || puzzleWaypoints.Count == 0)
        {
            return false;
        }

        List<GhostRouteWaypoint> worldWaypoints = recordedAtMonument != null
            ? ProjectGhostRouteToMonument(puzzleWaypoints, monument)
            : puzzleWaypoints;

        state.CardPuzzleVisitedMonuments.Add(monument.name);

        // Entry gate (2026-08-17, Lucas's own explicit request): "Bot does
        // a local state-check of the switch... If switch is already on END
        // GHOSTROUTE immediately... the monument puzzle has probably
        // already been looted and it is not worth the bots time or
        // resources doing the puzzle (keycards have limited uses)."
        if (TryFindCardPuzzleSwitch(worldWaypoints, out ElectricSwitch existingSwitch) && existingSwitch.IsOn())
        {
            VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' found '{monument.name}''s puzzle switch already on - already solved recently, skipping.");
            return false;
        }

        // Same fix, same reasoning as the main ghost-route commit block
        // (LivingRust.MonumentLootZones.cs, 2026-08-18) - this whole
        // function returns before ever reaching the zone-scan code that
        // normally sets these, so without this a card-puzzle detour left
        // the task's "committed monument" untouched, and the later re-check
        // ContinueLootTask relies on to catch a fuse/card picked up mid-
        // route could silently resolve a different monument (or none) once
        // the survivor ends up more than the generic 60m
        // MonumentLootZoneDetectionRadius from this one's own origin.
        state.CommittedMonumentName = monument.name;
        state.CommittedMonumentDeadline = UnityEngine.Time.realtimeSinceStartup + GetMonumentDwellSeconds(monument.name);

        Puts($"card-puzzle: '{survivor.Character.Alias}' is heading to solve '{monument.name}''s card puzzle via '{traceFilePath}'.");

        Guid characterId = survivor.Character.Id;
        _activeCardPuzzleSurvivors.Add(characterId);
        _handledReadersThisPuzzle[characterId] = new HashSet<NetworkableId>();
        _handledWheelsThisPuzzle[characterId] = new HashSet<NetworkableId>();
        _activePuzzleRequiredTiers[characterId] = requiredTiers;

        Action onPuzzleComplete = () =>
        {
            _activeCardPuzzleSurvivors.Remove(characterId);
            _handledReadersThisPuzzle.Remove(characterId);
            _handledWheelsThisPuzzle.Remove(characterId);
            _activePuzzleRequiredTiers.Remove(characterId);
            ContinueLootTask(survivor, state);
        };

        // Same pre-registration-before-the-approach-walk pattern the normal
        // ghost route commit block already uses (LivingRust.MonumentLootZones.cs) -
        // combat interrupting the approach itself, before StartGhostRoute
        // ever runs once, still has something real to resume afterward.
        _pendingGhostRouteToResume[characterId] = (worldWaypoints, 0, onPuzzleComplete);
        StartGhostRouteLootScan(survivor, onPuzzleComplete);

        StartLongDistanceWalk(
            survivor,
            worldWaypoints[0].Position,
            monument.name,
            onArrived: () => StartGhostRoute(survivor, worldWaypoints, 0, onPuzzleComplete),
            onFailed: () =>
            {
                VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' couldn't reach the puzzle route's start near '{monument.name}' - falling back to normal looting.");
                _pendingGhostRouteToResume.Remove(characterId);
                StopGhostRouteLootScan(characterId);
                _activeCardPuzzleSurvivors.Remove(characterId);
                _handledReadersThisPuzzle.Remove(characterId);
                _handledWheelsThisPuzzle.Remove(characterId);
                _activePuzzleRequiredTiers.Remove(characterId);
                ContinueLootTask(survivor, state);
            });

        return true;
    }

    /// <summary>
    /// Real fuse/switch/reader interactions, called from StartGhostRoute's
    /// own waypoint-arrival step (LivingRust.Looting.cs) right alongside
    /// LootWhateverIsHereNow - generic to every ghost route, but gated
    /// entirely on _activeCardPuzzleSurvivors having this survivor (see its
    /// own doc comment for why) so it's a genuine no-op for every normal
    /// loot route. Handles however many fuseboxes/switches/readers this
    /// specific route has - each type's own real state (HasPassthroughItem/
    /// IsOn/per-reader handled-set) is what makes repeat visits naturally
    /// idempotent, not a single "puzzle solved" flag.
    ///
    /// Deliberately does NOT cover WheelSwitch/PressButton - see
    /// TryHandleFreeMonumentInteractions' own doc comment for why those two
    /// are handled separately and unconditionally instead (2026-08-20,
    /// Lucas's own explicit spec).
    /// </summary>
    private void TryHandleCardPuzzleInteractions(Survivor survivor, BasePlayer npc)
    {
        Guid characterId = survivor.Character.Id;

        if (!_activeCardPuzzleSurvivors.Contains(characterId))
        {
            return;
        }

        Collider[] hits = Physics.OverlapSphere(npc.transform.position, CardPuzzleInteractionRadius, CardReaderLayerMask, QueryTriggerInteraction.Collide);

        // Real fusebox/switch/reader props each expose SEVERAL child
        // colliders (confirmed via a live scan - CardReader alone has
        // separate on/off/green/red/blue state-visual colliders plus its
        // own box collider, all resolving to the same entity via
        // GetComponentInParent) - the old per-collider loop below acted on
        // every one of them, which is exactly what flipped a switch twice
        // and burned an entire keycard's remaining charges in one single
        // visit instead of once (2026-08-18, both confirmed live).
        //
        // Switches are deduped by INSTANCE (HashSet<ElectricSwitch>), not
        // "at most one total" like fusebox/reader below - 2026-08-19 real
        // live bug: Powerplant has two genuinely separate switches under 1m
        // apart, both well within CardPuzzleInteractionRadius (3m) at once.
        // The old single-variable `??=` pattern kept whichever switch's
        // collider happened to appear FIRST in the hits array and silently
        // dropped the second one forever - not skipped-and-retried, since
        // by the time its colliders were reached in the loop the variable
        // was already non-null. Unless some other waypoint happened to be
        // within range of ONLY the second switch, it would never get
        // flipped at all. A HashSet naturally fixes both problems at once:
        // one switch's own multiple colliders still collapse to a single
        // entry (no double-flip), but two distinct switch entities each get
        // their own entry and both get handled in the same call.
        ItemBasedFlowRestrictor fusebox = null;
        CardReader reader = null;
        HashSet<ElectricSwitch> switches = new();

        // TimerSwitch (the "ACTIVATE" spring-loaded/timed prop, real
        // in-game prompt shows a rotating-arrow icon - confirmed via a live
        // IL dump of Assembly-CSharp.dll, 2026-08-19) is a COMPLETELY
        // separate class from ElectricSwitch (both inherit IOEntity
        // directly, neither derives from the other), so the ElectricSwitch
        // dedup set above silently never saw it at all - real live bug:
        // Powerplant wires two different switch types together (a plain
        // ElectricSwitch turn-on/off plus a TimerSwitch "activate" that
        // starts a real countdown - SwitchPressed() -> StartTimer() ->
        // auto EndTimer() after timerLength seconds if not used in time,
        // matching Lucas's own description of the mechanic). Same dedup
        // approach as switches - a HashSet collapses one entity's multiple
        // colliders while still catching genuinely separate ones.
        HashSet<TimerSwitch> timerSwitches = new();

        foreach (Collider hit in hits)
        {
            fusebox ??= hit.GetComponentInParent<ItemBasedFlowRestrictor>();
            reader ??= hit.GetComponentInParent<CardReader>();

            ElectricSwitch hitSwitch = hit.GetComponentInParent<ElectricSwitch>();

            if (hitSwitch != null)
            {
                switches.Add(hitSwitch);
            }

            TimerSwitch hitTimerSwitch = hit.GetComponentInParent<TimerSwitch>();

            if (hitTimerSwitch != null)
            {
                timerSwitches.Add(hitTimerSwitch);
            }
        }

        if (fusebox != null && !fusebox.IsDestroyed && !fusebox.HasPassthroughItem())
        {
            TryInsertFuse(survivor, npc, fusebox);
        }

        foreach (ElectricSwitch electricSwitch in switches)
        {
            if (!electricSwitch.IsDestroyed && !electricSwitch.IsOn())
            {
                electricSwitch.SetSwitch(true);
                VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' flipped a switch.");
            }
        }

        foreach (TimerSwitch timerSwitch in timerSwitches)
        {
            // Real entry point (confirmed via IL: SVSwitch, the actual RPC
            // a player's own client-side interaction calls, does nothing
            // but call this) - internally checks IsPowered() itself before
            // starting the countdown, so calling it before the upstream
            // circuit is actually live is a safe no-op, not a wasted press.
            if (!timerSwitch.IsDestroyed && !timerSwitch.IsOn())
            {
                timerSwitch.SwitchPressed();
                VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' pressed a timed activate switch.");
            }
        }

        if (reader != null && !reader.IsDestroyed
            && _handledReadersThisPuzzle.TryGetValue(characterId, out HashSet<NetworkableId> handledReaders)
            && handledReaders.Add(reader.net.ID))
        {
            // .Add() above both records this reader as handled AND tells us
            // whether it was already in the set - a multi-reader route
            // (Military Tunnel) needs each DIFFERENT reader attempted once,
            // but the same reader shouldn't be re-attempted every time the
            // route passes near it again across several close waypoints.
            TrySwipeCard(survivor, npc, reader);
        }
    }

    /// <summary>
    /// WheelSwitch and PressButton interactions, called from
    /// StartGhostRoute's own waypoint-arrival step UNCONDITIONALLY - on
    /// every ghost route, not just a puzzle-committed one, unlike
    /// TryHandleCardPuzzleInteractions above. 2026-08-20, Lucas's own
    /// explicit spec, after Water Treatment Plant's real layout turned out
    /// to need it: its WheelSwitch-driven roller door needs no keycard or
    /// fuse at all to open (that's purely the puzzle ROOM behind it, gated
    /// separately) - Lucas recorded the wheel-hold into all 4 general loot
    /// paths too, since it's the only way into the loot room regardless of
    /// whether a bot is even attempting the puzzle. PressButton is the same
    /// story but broader - Lucas's own framing: "concurrent throughout
    /// almost every monument with a card reader... if the player is inside
    /// the monument for too long the doors can close, and the buttons allow
    /// them to escape," so it needs to work on ordinary loot passes too, not
    /// just committed puzzle attempts.
    ///
    /// Deliberately excludes fusebox/ElectricSwitch/TimerSwitch/CardReader -
    /// those stay puzzle-gated (TryHandleCardPuzzleInteractions) because
    /// they consume real fuse/card durability, and a bot just passing near
    /// one on an ordinary loot run has no business spending either.
    /// WheelSwitch/PressButton cost nothing to interact with, so there's no
    /// waste risk running them unconditionally.
    ///
    /// Also handles Elevator/ElevatorLift (2026-08-21, Nuclear Missile
    /// Silo - see TryHandleElevatorInteractions' own doc comment for the
    /// real call/ride mechanics).
    ///
    /// Returns true if ANYTHING handled here is still actively pending
    /// (an in-progress wheel-turn whose door hasn't reached full
    /// openProgress yet, or an elevator that's still IsBusy() moving) -
    /// StartGhostRoute uses this to wait for the real completion state
    /// before advancing.
    /// </summary>
    private bool TryHandleFreeMonumentInteractions(Survivor survivor, BasePlayer npc)
    {
        Collider[] hits = Physics.OverlapSphere(npc.transform.position, CardPuzzleInteractionRadius, CardReaderLayerMask, QueryTriggerInteraction.Collide);

        HashSet<WheelSwitch> wheelSwitches = new();
        HashSet<PressButton> pressButtons = new();
        HashSet<Elevator> elevators = new();
        HashSet<ElevatorLift> elevatorLifts = new();

        foreach (Collider hit in hits)
        {
            WheelSwitch hitWheelSwitch = hit.GetComponentInParent<WheelSwitch>();

            if (hitWheelSwitch != null)
            {
                wheelSwitches.Add(hitWheelSwitch);
            }

            PressButton hitPressButton = hit.GetComponentInParent<PressButton>();

            if (hitPressButton != null)
            {
                pressButtons.Add(hitPressButton);
            }

            // Elevator is the per-floor call point, ElevatorLift is the
            // moving cabin itself - two distinct entity classes (neither
            // derives from the other), so both need their own lookup.
            Elevator hitElevator = hit.GetComponentInParent<Elevator>();

            if (hitElevator != null)
            {
                elevators.Add(hitElevator);
            }

            ElevatorLift hitElevatorLift = hit.GetComponentInParent<ElevatorLift>();

            if (hitElevatorLift != null)
            {
                elevatorLifts.Add(hitElevatorLift);
            }
        }

        foreach (PressButton pressButton in pressButtons)
        {
            // Real entry point (confirmed via IL: RPC_Press, the actual RPC
            // a player's own client-side interaction calls, does nothing
            // but call this) - momentary, not held: IsOn() naturally flips
            // back to false on its own once pressDuration elapses (real
            // field, per-instance), so no separate "already handled forever"
            // tracking is needed the way the wheel needed - if the route
            // passes back near it later and it's already reverted, pressing
            // again is exactly the real intended behaviour, not a bug.
            if (!pressButton.IsDestroyed && !pressButton.IsOn())
            {
                pressButton.Press();
                VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' pressed a button.");
            }
        }

        bool anyPending = false;

        foreach (WheelSwitch wheelSwitch in wheelSwitches)
        {
            if (!wheelSwitch.IsDestroyed)
            {
                ProgressDoor connectedDoor = TryResolveConnectedDoor(wheelSwitch);

                TryStartWheelTurn(survivor, npc, wheelSwitch, connectedDoor);

                // Only actually PENDING (worth freezing the route to wait
                // on) if a turn attempt is genuinely running right now -
                // 2026-08-20, real live bug: this used to just check
                // "door isn't open yet," true for every waypoint that
                // merely brings the wheel within CardPuzzleInteractionRadius
                // (3m) regardless of whether TryStartWheelTurn's own real
                // 2m range pre-check let it actually start. That froze the
                // route at the FIRST waypoint the wheel became visible from
                // - well before the recorded path would have carried the
                // bot the rest of the way in - instead of continuing to
                // walk through intermediate waypoints like a normal route.
                // _activeWheelTurnTimers only holds an entry once a turn
                // genuinely started, so this is the real "should we wait"
                // signal.
                if (connectedDoor != null && !connectedDoor.IsDestroyed && connectedDoor.openProgress < 1f
                    && _activeWheelTurnTimers.ContainsKey(wheelSwitch.net.ID))
                {
                    anyPending = true;
                }
            }
        }

        foreach (Elevator elevator in elevators)
        {
            if (!elevator.IsDestroyed && TryCallElevator(survivor, npc, elevator))
            {
                anyPending = true;
            }
        }

        foreach (ElevatorLift elevatorLift in elevatorLifts)
        {
            if (!elevatorLift.IsDestroyed && TryRideElevatorDown(survivor, npc, elevatorLift))
            {
                anyPending = true;
            }
        }

        return anyPending;
    }

    private void TryInsertFuse(Survivor survivor, BasePlayer npc, ItemBasedFlowRestrictor fusebox)
    {
        // 2026-08-18: Type.GetField("inventory", NonPublic|Instance) confirmed
        // failing at RUNTIME on this server build (FlowRestrictorInventoryField
        // resolves null every time, live-confirmed via /lr.debug.inspectfusebox
        // both before AND after a real fuse insert) - despite the field
        // definitely existing and definitely being populated at spawn
        // (confirmed via a live IL dump of ServerInit()/CreateInventory()).
        // Direct field access instead of reflection - Carbon compiles
        // plugins against a publicized reference assembly (private members
        // exposed for direct access at compile time), so this should work
        // even though the reflection-based approach couldn't find the same
        // field on the actually-loaded runtime type.
        ItemContainer fuseboxInventory = fusebox.inventory;

        if (fuseboxInventory == null)
        {
            Puts($"card-puzzle-diag: '{survivor.Character.Alias}' fusebox.inventory (direct field access) is still null.");
            return;
        }

        Item fuseItem = CardPuzzleFuseShortnames
            .Select(shortname => npc.inventory.FindItemByItemName(shortname))
            .FirstOrDefault(candidate => candidate != null);

        if (fuseItem == null)
        {
            Puts($"card-puzzle-diag: '{survivor.Character.Alias}' has no fuse/fuse.highgrade in inventory at all.");
            return;
        }

        if (!fusebox.IsValidPassthroughItem(fuseItem))
        {
            Puts($"card-puzzle-diag: '{survivor.Character.Alias}' holds '{fuseItem.info.shortname}' but IsValidPassthroughItem rejected it (validPassthroughItems mismatch?).");
            return;
        }

        Puts($"card-puzzle-diag: '{survivor.Character.Alias}' attempting MoveToContainer('{fuseItem.info.shortname}') into fusebox inventory (capacity {fuseboxInventory.capacity}, current count {fuseboxInventory.itemList.Count}).");

        if (fuseItem.MoveToContainer(fuseboxInventory))
        {
            VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' fed a '{fuseItem.info.shortname}' into a fusebox.");
        }
        else
        {
            Puts($"card-puzzle-diag: '{survivor.Character.Alias}' MoveToContainer returned false - fusebox rejected the fuse.");
        }
    }

    /// <summary>
    /// Resolves whichever real ProgressDoor a WheelSwitch's own IO output
    /// wiring actually connects to - 2026-08-20, real live bug: an earlier
    /// version paired wheel-to-door by proximity (whichever ProgressDoor
    /// happened to also be within CardPuzzleInteractionRadius), which a
    /// live scan proved unreliable - Water Treatment Plant has TWO separate
    /// sliding_blast_door entities both sitting ~3m from the same wheel.
    /// IOEntity.outputs/IOSlot.connectedTo/IORef.Get() are all public
    /// fields/methods (confirmed via reflection, no Harmony/private-field
    /// access needed) - this walks the wheel's own real output slots and
    /// returns the first one that resolves to an actual ProgressDoor,
    /// exactly the same resolution path WheelSwitch.RotateProgress() itself
    /// uses internally (confirmed via its own IL) to know which entity to
    /// push kinetic energy into.
    /// </summary>
    private static ProgressDoor TryResolveConnectedDoor(WheelSwitch wheel)
    {
        if (wheel.outputs == null)
        {
            return null;
        }

        foreach (IOEntity.IOSlot output in wheel.outputs)
        {
            IOEntity connected = output.connectedTo?.Get(false);

            if (connected is ProgressDoor door && !door.IsDestroyed)
            {
                return door;
            }
        }

        return null;
    }

    /// <summary>
    /// Real WheelSwitch prop (confirmed via IL 2026-08-20 - e.g. Water
    /// Treatment Plant's roller-door wheel) - unlike every other puzzle
    /// interaction here (instant per-visit: flip, insert, swipe), a real
    /// player has to physically hold the interact key for several real
    /// seconds while standing close (Lucas's own live-confirmed 8s for
    /// this specific wheel). Rather than hardcoding that 8s - which would
    /// silently be wrong for any other wheel tuned differently elsewhere -
    /// this drives the REAL WheelSwitch.RotateProgress() method repeatedly
    /// at the entity's own real progressTickRate cadence, naturally
    /// reproducing whatever real duration that specific wheel is tuned
    /// for. Confirmed via IL that RotateProgress() (not SetRotateProgress
    /// alone) is what pushes the real kinetic signal to the connected door
    /// each tick - calling only SetRotateProgress would update the visual
    /// value but never actually open anything.
    ///
    /// Completion is checked against door.openProgress, NOT
    /// wheel.rotateProgress - 2026-08-20, real live bug: the first version
    /// of this method used rotateProgress &gt;= 1f as "done," which read
    /// as plausible from the IL alone (IOInput's own IL has a
    /// Mathf.Clamp(1, 0, ...) call right after touching rotateProgress) but
    /// turned out to be dead wrong live - /lr.debug.inspectswitch reported
    /// rotateProgress=34.50 after one real 8.86s hold, meaning it's an
    /// uncapped local animation value with no real ceiling, not a 0-1
    /// completion signal. The check above bailed out immediately on every
    /// real attempt (34.5 &gt;= 1 is always true), which is exactly why the
    /// wheel "never activated at all." The REAL completion state lives on
    /// the connected ProgressDoor's own storedEnergy/energyForOpen fields
    /// (openProgress is their 0-1 ratio) - door is resolved via the wheel's
    /// own real IO output wiring (TryResolveConnectedDoor), not by
    /// proximity guessing - a real live scan found TWO separate
    /// sliding_blast_door entities both within CardPuzzleInteractionRadius
    /// of the same wheel (net.ID 2899 and 6121, ~3m apart), so "whichever
    /// ProgressDoor happens to be nearby" would have been a genuine
    /// coinflip about which door the wait logic actually tracked.
    ///
    /// Also confirmed live (Lucas, 2026-08-20): the door isn't a one-way
    /// ratchet - it actively decays back down once nobody's feeding it
    /// energy ("once the player or bot lets go... the door automatically
    /// starts coming down"), which is why the whole 8s has to be held
    /// continuously, not accumulated in bursts. The timer.Every loop below
    /// already does this correctly (calls RotateProgress every tick with
    /// no gaps once started), this doc note just explains WHY holding
    /// continuously matters instead of it looking like an arbitrary detail.
    ///
    /// rotatorPlayer/progressTickRate are both private fields, directly
    /// field-accessed rather than reflected - same pattern TryInsertFuse
    /// already established for fusebox.inventory (Carbon's publicized
    /// reference assembly allows direct compile-time access even though
    /// runtime reflection fails on this server build).
    ///
    /// Idempotent both within one call (won't double-start a timer already
    /// running for this exact wheel - _activeWheelTurnTimers) and across
    /// repeat visits. Stops itself the instant the wheel's own real
    /// rotatorPlayer no longer matches npc - that happens INSIDE
    /// RotateProgress via its own real CancelPlayerRotation() call if its
    /// distance check fails or the player dies/sleeps, which this external
    /// timer has no other way to detect since it's driven by Carbon's own
    /// timer system, not the entity's internal Unity Invoke chain
    /// CancelPlayerRotation() cancels.
    ///
    /// Once a full WheelHoldCompletionSeconds hold finishes, the wheel is
    /// marked permanently done for this survivor's puzzle attempt
    /// (_handledWheelsThisPuzzle) and never restarted again regardless of
    /// what the door does afterward - 2026-08-20, real live bug: without
    /// this, "finished" only meant "stopped holding for now," and since the
    /// door actively decays back down within a second or two of release
    /// (Lucas's own live confirmation), the very next waypoint scan always
    /// found openProgress &lt; 1 again and restarted the whole 8s hold -
    /// confirmed live as an unbroken hold/finish/restart loop with no exit.
    /// Lucas's own explicit spec: "it only needs to hold it for 8 seconds
    /// then give up... so it can continue the path" - the bot did its part,
    /// the route moves on regardless of whether the door stays open.
    /// </summary>
    private void TryStartWheelTurn(Survivor survivor, BasePlayer npc, WheelSwitch wheel, ProgressDoor door)
    {
        Guid characterId = survivor.Character.Id;
        bool alreadyHandled = _handledWheelsThisPuzzle.TryGetValue(characterId, out HashSet<NetworkableId> handledWheels)
            && handledWheels.Contains(wheel.net.ID);

        bool alreadyOpen = door != null && !door.IsDestroyed && door.openProgress >= 1f;

        if (alreadyHandled || alreadyOpen || _activeWheelTurnTimers.ContainsKey(wheel.net.ID))
        {
            return;
        }

        if (door == null)
        {
            // No ProgressDoor found within CardPuzzleInteractionRadius of
            // this wheel - without it there's no real way to tell when
            // turning is actually "done," so this deliberately refuses to
            // start rather than pumping energy forever with no stop
            // condition. Should only happen if a wheel is ever recorded
            // without its door in range - worth a real diag line if it
            // ever fires live.
            Puts($"card-puzzle-diag: '{survivor.Character.Alias}' found a wheel but no nearby ProgressDoor to pair it with - not starting (no way to detect completion).");
            return;
        }

        // Real distance-to-wheel BEFORE committing to start - 2026-08-20,
        // real live bug: two separate bots both got "started turning...
        // stopped turning early (too far)" within ~1 real second of
        // starting, meaning RotateProgress's own internal 2m distance-to-
        // rotator check (confirmed via IL: a flat 2m Vector3Ex.Distance2D
        // cutoff) failed on the very first tick. CardPuzzleInteractionRadius
        // (3m, what gates whether this method even gets CALLED) is wider
        // than that real 2m tolerance, so a bot arriving between 2-3m away
        // used to get detected and started here but was guaranteed to
        // immediately self-cancel - wasted attempt, and (before the
        // StartGhostRoute wait-loop retry fix) left the route stuck waiting
        // on a dead timer for up to WheelWaitMaxSeconds. Refusing to start
        // at all past WheelSwitchMaxRotateDistance2D (with a small safety
        // margin under the real 2m) means the caller's retry loop just
        // keeps trying again every poll instead of burning one real attempt
        // that can never succeed.
        float startDistance2D = Vector2.Distance(
            new Vector2(npc.transform.position.x, npc.transform.position.z),
            new Vector2(wheel.transform.position.x, wheel.transform.position.z));

        if (startDistance2D > WheelSwitchMaxRotateDistance2D)
        {
            VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' is {startDistance2D:F2}m from a wheel (real turn range is ~2m) - not starting yet.");
            return;
        }

        wheel.rotatorPlayer = npc;

        VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' started turning a wheel (tick every {wheel.progressTickRate:F2}s, door openProgress={door.openProgress:F2}, distance2D={startDistance2D:F2}m).");

        NetworkableId wheelId = wheel.net.ID;
        Timer turnTimer = null;
        _wheelHoldStartTimes[wheelId] = UnityEngine.Time.realtimeSinceStartup;

        turnTimer = timer.Every(Mathf.Max(wheel.progressTickRate, 0.05f), () =>
        {
            if (npc == null || npc.IsDestroyed || wheel == null || wheel.IsDestroyed || door.IsDestroyed)
            {
                turnTimer.Destroy();
                _activeWheelTurnTimers.Remove(wheelId);
                _wheelHoldStartTimes.Remove(wheelId);
                return;
            }

            wheel.RotateProgress();

            float heldSeconds = UnityEngine.Time.realtimeSinceStartup - _wheelHoldStartTimes[wheelId];

            if (door.openProgress >= 1f || heldSeconds >= WheelHoldCompletionSeconds)
            {
                VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' finished turning the wheel - door openProgress={door.openProgress:F2}, held {heldSeconds:F1}s - moving on regardless of what the door does next.");
                turnTimer.Destroy();
                _activeWheelTurnTimers.Remove(wheelId);
                _wheelHoldStartTimes.Remove(wheelId);

                // Lazily creates the set if missing (2026-08-20) rather
                // than only recording completion when one already exists -
                // a normal loot-route survivor never goes through
                // TryStartCardPuzzleDetour/TryTeleportToCardPuzzle (the
                // only places that used to pre-create this entry), so
                // without this, marking a wheel "done" would silently
                // no-op for any bot doing an ordinary loot pass - exactly
                // the infinite hold/finish/restart loop this dictionary
                // exists to prevent, just for the OTHER calling context.
                if (!_handledWheelsThisPuzzle.TryGetValue(characterId, out HashSet<NetworkableId> completedWheels))
                {
                    completedWheels = new HashSet<NetworkableId>();
                    _handledWheelsThisPuzzle[characterId] = completedWheels;
                }

                completedWheels.Add(wheelId);

                return;
            }

            if (wheel.rotatorPlayer != npc)
            {
                float nowDistance2D = Vector2.Distance(
                    new Vector2(npc.transform.position.x, npc.transform.position.z),
                    new Vector2(wheel.transform.position.x, wheel.transform.position.z));

                VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' stopped turning the wheel early (real RotateProgress cancelled it - too far, dead, or asleep - distance2D now {nowDistance2D:F2}m, held {heldSeconds:F1}s).");
                turnTimer.Destroy();
                _wheelHoldStartTimes.Remove(wheelId);
                _activeWheelTurnTimers.Remove(wheelId);
            }
        });

        _activeWheelTurnTimers[wheelId] = turnTimer;
    }

    /// <summary>
    /// Resolves the real "mover" Elevator entity for a given per-floor call
    /// point - 2026-08-21, confirmed via IL: ElevatorStatic.CallElevator
    /// redirects to its own ownerElevator field (a direct ElevatorStatic
    /// reference, not wrapped in EntityRef) whenever it's set, rather than
    /// calling RequestMoveLiftTo on itself - only the owner (the shaft's
    /// single coordinating instance, per Rust's own elevator design) is
    /// safe to issue real movement requests through. A plain Elevator (not
    /// an ElevatorStatic) has no ownerElevator field at all, so it IS its
    /// own mover.
    /// </summary>
    private static Elevator TryResolveElevatorMover(Elevator elevator)
    {
        if (elevator is ElevatorStatic elevatorStatic && elevatorStatic.ownerElevator != null)
        {
            return elevatorStatic.ownerElevator;
        }

        return elevator;
    }

    /// <summary>
    /// Real per-floor "call the elevator" entry point (2026-08-21, Nuclear
    /// Missile Silo - single way in/out, heavy elevator use throughout).
    /// Confirmed via IL: a real player's floor call panel and the cabin's
    /// own internal up/down buttons both funnel through the ONE real RPC
    /// entry point, ElevatorLift.Server_RaiseLowerFloor(RPCMessage) - it
    /// reads an int targetFloor + bool relative off the wire and forwards
    /// straight to Elevator.Server_RaiseLowerElevator(int, bool, out bool),
    /// which is itself public and non-RPC, so it's called directly here,
    /// skipping the RPC message entirely (same pattern already established
    /// for WheelSwitch.RotateProgress()/PressButton.Press()).
    ///
    /// Calling a floor's own Elevator/ElevatorStatic entity with
    /// relative:false and its own real Floor value is what "call it to my
    /// floor" actually means - the cabin (wherever it currently sits)
    /// travels to match. Guarded by _handledElevatorsThisRoute so repeat
    /// waypoints/poll ticks near the same call panel don't keep re-issuing
    /// the request while it's already travelling or already arrived -
    /// pending state itself is read fresh every call via mover.IsBusy()
    /// (real BaseEntity method), not cached.
    /// </summary>
    private bool TryCallElevator(Survivor survivor, BasePlayer npc, Elevator elevator)
    {
        Guid characterId = survivor.Character.Id;
        Elevator mover = TryResolveElevatorMover(elevator);

        if (!_handledElevatorsThisRoute.TryGetValue(characterId, out HashSet<NetworkableId> handledElevators))
        {
            handledElevators = new HashSet<NetworkableId>();
            _handledElevatorsThisRoute[characterId] = handledElevators;
        }

        if (handledElevators.Add(elevator.net.ID))
        {
            mover.Server_RaiseLowerElevator(elevator.Floor, false, out _);
            VerbosePuts($"elevator: '{survivor.Character.Alias}' called the elevator to floor {elevator.Floor}.");
        }

        return mover.IsBusy();
    }

    /// <summary>
    /// Real "inside the cabin, press the down button" entry point
    /// (2026-08-21, Nuclear Missile Silo). Once boarded, the bot's own
    /// route waypoint sits near the moving ElevatorLift cabin itself
    /// (a distinct entity class from the per-floor Elevator/ElevatorStatic
    /// call points - neither derives from the other) rather than a
    /// call-point entity, so this is a separate detection/handling path
    /// from TryCallElevator even though both ultimately drive the same
    /// real mover.
    ///
    /// relative:true with targetFloor -1 matches the cabin's own real
    /// internal Down button semantics (confirmed via IL: ElevatorLift
    /// exposes dedicated UpButtonPoint/DownButtonPoint transforms distinct
    /// from its GoTopButtonPoint/GoBottomButtonPoint express buttons, i.e.
    /// a real relative one-floor-at-a-time step, not an absolute target) -
    /// deliberately always "down," matching every real trace recorded so
    /// far (Lucas's own framing: "wait inside the elevator as it goes
    /// down"). Revisit if a future monument's trace needs the cabin to go
    /// up instead.
    ///
    /// ElevatorLift.ownerElevator is a real EntityRef&lt;Elevator&gt; field
    /// (confirmed via IL/reflection), directly field-accessed same as
    /// every other private-field case in this file.
    /// </summary>
    private bool TryRideElevatorDown(Survivor survivor, BasePlayer npc, ElevatorLift elevatorLift)
    {
        Elevator mover = elevatorLift.ownerElevator.Get(false);

        if (mover == null)
        {
            return false;
        }

        Guid characterId = survivor.Character.Id;

        if (!_handledElevatorsThisRoute.TryGetValue(characterId, out HashSet<NetworkableId> handledElevators))
        {
            handledElevators = new HashSet<NetworkableId>();
            _handledElevatorsThisRoute[characterId] = handledElevators;
        }

        if (handledElevators.Add(elevatorLift.net.ID))
        {
            mover.Server_RaiseLowerElevator(-1, true, out _);
            VerbosePuts($"elevator: '{survivor.Character.Alias}' pressed the down button inside the elevator.");
        }

        return mover.IsBusy();
    }

    [ChatCommand("lr.debug.inspectfusebox")]
    private void CmdDebugInspectFusebox(BasePlayer player, string command, string[] args)
    {
        RunDebugInspectFusebox(player);
    }

    [ConsoleCommand("lr.debug.inspectfusebox")]
    private void CmdDebugInspectFuseboxConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugInspectFusebox(player);
        }
    }

    /// <summary>
    /// Reports the REAL live state of whichever fusebox is nearest where
    /// the admin's looking - built 2026-08-18 specifically to settle
    /// whether the reflected private inventory field is even the right
    /// thing to be poking at, after a first CreateInventory() fallback fix
    /// didn't resolve a live "fuse never gets inserted" report. Run this
    /// once before and once after manually inserting a real fuse as a
    /// player (aim at the fusebox, use it) - if the inventory field's item
    /// count changes to match, the field/approach is correct and the bug is
    /// elsewhere in TryInsertFuse's own logic (see its card-puzzle-diag:
    /// log lines for the rest of the story); if it STAYS empty even after a
    /// confirmed real insert, real fuses aren't tracked via this
    /// ItemContainer at all and TryInsertFuse needs a completely different
    /// approach.
    /// </summary>
    private void RunDebugInspectFusebox(BasePlayer player)
    {
        Vector3 origin = player.eyes.position;
        Vector3 direction = player.eyes.HeadForward();

        RaycastHit[] hits = Physics.RaycastAll(origin, direction, 15f, ~0, QueryTriggerInteraction.Collide);
        ItemBasedFlowRestrictor fusebox = hits
            .OrderBy(h => h.distance)
            .Select(h => h.collider.GetComponentInParent<ItemBasedFlowRestrictor>())
            .FirstOrDefault(f => f != null);

        if (fusebox == null)
        {
            player.ChatMessage("[LivingRust] No fusebox found along your look direction (within 15m).");
            return;
        }

        bool hasPassthroughItem = fusebox.HasPassthroughItem();
        bool gotPassthroughItem = fusebox.GetPassthroughItem(out Item passthroughItem);

        // 2026-08-18: switched from reflection (Type.GetField, confirmed
        // failing at runtime on this server build - see TryInsertFuse's own
        // doc comment) to plain direct field access, which Carbon's
        // publicized compile-time reference assembly allows.
        ItemContainer directInventory = fusebox.inventory;

        string inventoryFieldReport = directInventory == null
            ? "NULL (direct field access)"
            : $"non-null, {directInventory.itemList.Count}/{directInventory.capacity} slot(s) used: [{string.Join(", ", directInventory.itemList.Select(i => i.info.shortname))}]";

        string report = $"fusebox-diag: at {fusebox.transform.position} - "
            + $"HasPassthroughItem={hasPassthroughItem}, "
            + $"GetPassthroughItem={gotPassthroughItem} (item={(passthroughItem != null ? passthroughItem.info.shortname : "null")}), "
            + $"direct inventory field={inventoryFieldReport}, "
            + $"validPassthroughItems=[{string.Join(", ", fusebox.validPassthroughItems?.Select(d => d.shortname) ?? Enumerable.Empty<string>())}], "
            + $"numSlots={fusebox.numSlots}, allowedContents={fusebox.allowedContents}, IsOn={fusebox.IsOn()}.";

        Puts(report);
        player.ChatMessage($"[LivingRust] {report}");
    }

    [ChatCommand("lr.debug.inspectcardreader")]
    private void CmdDebugInspectCardReader(BasePlayer player, string command, string[] args)
    {
        RunDebugInspectCardReader(player);
    }

    [ConsoleCommand("lr.debug.inspectcardreader")]
    private void CmdDebugInspectCardReaderConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugInspectCardReader(player);
        }
    }

    /// <summary>
    /// Reports a real CardReader's own accessLevel field plus its resolved
    /// KeycardTier per KeycardTierByReaderAccessLevel (2026-08-18) - built
    /// specifically to confirm/correct that still-unverified mapping. Run
    /// this aimed at a KNOWN-tier reader (e.g. any already-registered green
    /// puzzle's reader) to confirm the green value, then at Military
    /// Tunnel's blue/red readers to fill in the rest.
    /// </summary>
    private void RunDebugInspectCardReader(BasePlayer player)
    {
        Vector3 origin = player.eyes.position;
        Vector3 direction = player.eyes.HeadForward();

        RaycastHit[] hits = Physics.RaycastAll(origin, direction, 15f, ~0, QueryTriggerInteraction.Collide);
        CardReader reader = hits
            .OrderBy(h => h.distance)
            .Select(h => h.collider.GetComponentInParent<CardReader>())
            .FirstOrDefault(r => r != null);

        if (reader == null)
        {
            player.ChatMessage("[LivingRust] No card reader found along your look direction (within 15m).");
            return;
        }

        string resolvedTier = KeycardTierByReaderAccessLevel.TryGetValue(reader.accessLevel, out KeycardTier tier)
            ? tier.ToString()
            : "UNKNOWN - not in KeycardTierByReaderAccessLevel";

        string report = $"cardreader-diag: at {reader.transform.position} - accessLevel={reader.accessLevel}, resolved tier={resolvedTier}, IsOn={reader.IsOn()}.";

        Puts(report);
        player.ChatMessage($"[LivingRust] {report}");
    }

    [ChatCommand("lr.debug.inspectswitch")]
    private void CmdDebugInspectSwitch(BasePlayer player, string command, string[] args)
    {
        RunDebugInspectSwitch(player);
    }

    [ConsoleCommand("lr.debug.inspectswitch")]
    private void CmdDebugInspectSwitchConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugInspectSwitch(player);
        }
    }

    /// <summary>
    /// Reports every real ElectricSwitch within CardPuzzleInteractionRadius
    /// of the admin on the "World" layer (the same query
    /// TryHandleCardPuzzleInteractions itself uses), PLUS a second
    /// all-layers/all-colliders dump of anything nearby whose type name
    /// contains "Switch" - 2026-08-19, real live bug: a known-visible
    /// second "activate" switch within a meter of a confirmed-working one
    /// (net.ID=5675) never showed up in the World-layer-only scan across 3
    /// separate attempts, meaning it's either on a different physics layer
    /// than CardReaderLayerMask covers, or isn't an ElectricSwitch at all -
    /// this second pass is built specifically to tell those two apart with
    /// real data instead of guessing further.
    /// </summary>
    private void RunDebugInspectSwitch(BasePlayer player)
    {
        Collider[] hits = Physics.OverlapSphere(player.transform.position, CardPuzzleInteractionRadius, CardReaderLayerMask, QueryTriggerInteraction.Collide);

        HashSet<ElectricSwitch> switches = new();

        // TimerSwitch ("ACTIVATE" prop, real class confirmed via live IL
        // dump 2026-08-19 - see TryHandleCardPuzzleInteractions' own doc
        // comment) is unrelated to ElectricSwitch, so it needs its own
        // separate GetComponentInParent pass here too.
        HashSet<TimerSwitch> timerSwitches = new();

        // WheelSwitch ("TURN" prop, real class confirmed via live IL dump
        // 2026-08-20) - same story, unrelated to both classes above.
        HashSet<WheelSwitch> wheelSwitches = new();

        // ProgressDoor - the REAL completion signal for a wheel-driven
        // door (openProgress), not WheelSwitch.rotateProgress itself - see
        // TryStartWheelTurn's own doc comment for the live bug this fixed.
        HashSet<ProgressDoor> progressDoors = new();

        // PressButton (momentary door-release button, real class confirmed
        // via live IL dump 2026-08-20) - unrelated to every class above.
        HashSet<PressButton> pressButtons = new();

        // Elevator (per-floor call point) and ElevatorLift (the moving
        // cabin itself) - real classes confirmed via live IL dump
        // 2026-08-21, Nuclear Missile Silo. Neither derives from the
        // other.
        HashSet<Elevator> elevators = new();
        HashSet<ElevatorLift> elevatorLifts = new();

        foreach (Collider hit in hits)
        {
            ElectricSwitch hitSwitch = hit.GetComponentInParent<ElectricSwitch>();

            if (hitSwitch != null)
            {
                switches.Add(hitSwitch);
            }

            TimerSwitch hitTimerSwitch = hit.GetComponentInParent<TimerSwitch>();

            if (hitTimerSwitch != null)
            {
                timerSwitches.Add(hitTimerSwitch);
            }

            WheelSwitch hitWheelSwitch = hit.GetComponentInParent<WheelSwitch>();

            if (hitWheelSwitch != null)
            {
                wheelSwitches.Add(hitWheelSwitch);
            }

            ProgressDoor hitProgressDoor = hit.GetComponentInParent<ProgressDoor>();

            if (hitProgressDoor != null)
            {
                progressDoors.Add(hitProgressDoor);
            }

            PressButton hitPressButton = hit.GetComponentInParent<PressButton>();

            if (hitPressButton != null)
            {
                pressButtons.Add(hitPressButton);
            }

            Elevator hitElevator = hit.GetComponentInParent<Elevator>();

            if (hitElevator != null)
            {
                elevators.Add(hitElevator);
            }

            ElevatorLift hitElevatorLift = hit.GetComponentInParent<ElevatorLift>();

            if (hitElevatorLift != null)
            {
                elevatorLifts.Add(hitElevatorLift);
            }
        }

        if (switches.Count == 0 && timerSwitches.Count == 0 && wheelSwitches.Count == 0 && progressDoors.Count == 0
            && pressButtons.Count == 0 && elevators.Count == 0 && elevatorLifts.Count == 0)
        {
            player.ChatMessage($"[LivingRust] No ElectricSwitch/TimerSwitch/WheelSwitch/ProgressDoor/PressButton/Elevator/ElevatorLift found within {CardPuzzleInteractionRadius}m on the World layer (CardReaderLayerMask).");
        }

        foreach (ElectricSwitch electricSwitch in switches)
        {
            float distance = Vector3.Distance(player.transform.position, electricSwitch.transform.position);
            string report = $"switch-diag: '{electricSwitch.ShortPrefabName}' (net.ID={electricSwitch.net?.ID}) at {electricSwitch.transform.position}, {distance:F2}m away - IsOn={electricSwitch.IsOn()}, IsDestroyed={electricSwitch.IsDestroyed}, layer={LayerMask.LayerToName(electricSwitch.gameObject.layer)}.";

            Puts(report);
            player.ChatMessage($"[LivingRust] {report}");
        }

        foreach (TimerSwitch timerSwitch in timerSwitches)
        {
            float distance = Vector3.Distance(player.transform.position, timerSwitch.transform.position);
            string report = $"timerswitch-diag: '{timerSwitch.ShortPrefabName}' (net.ID={timerSwitch.net?.ID}) at {timerSwitch.transform.position}, {distance:F2}m away - IsOn={timerSwitch.IsOn()}, IsDestroyed={timerSwitch.IsDestroyed}, timerLength={timerSwitch.timerLength}, layer={LayerMask.LayerToName(timerSwitch.gameObject.layer)}.";

            Puts(report);
            player.ChatMessage($"[LivingRust] {report}");
        }

        foreach (WheelSwitch wheelSwitch in wheelSwitches)
        {
            float distance = Vector3.Distance(player.transform.position, wheelSwitch.transform.position);
            string report = $"wheelswitch-diag: '{wheelSwitch.ShortPrefabName}' (net.ID={wheelSwitch.net?.ID}) at {wheelSwitch.transform.position}, {distance:F2}m away - rotateProgress={wheelSwitch.rotateProgress:F2} (NOT the completion signal - see ProgressDoor below), rotatorPlayer={wheelSwitch.rotatorPlayer?.displayName ?? "null"}, progressTickRate={wheelSwitch.progressTickRate:F2}, requiresPowerToTurn={wheelSwitch.requiresPowerToTurn}, IsDestroyed={wheelSwitch.IsDestroyed}, activeTimer={_activeWheelTurnTimers.ContainsKey(wheelSwitch.net.ID)}.";

            Puts(report);
            player.ChatMessage($"[LivingRust] {report}");
        }

        foreach (ProgressDoor progressDoor in progressDoors)
        {
            float distance = Vector3.Distance(player.transform.position, progressDoor.transform.position);
            string report = $"progressdoor-diag: '{progressDoor.ShortPrefabName}' (net.ID={progressDoor.net?.ID}) at {progressDoor.transform.position}, {distance:F2}m away - openProgress={progressDoor.openProgress:F2} (THIS is the real completion signal), storedEnergy={progressDoor.storedEnergy:F2}, energyForOpen={progressDoor.energyForOpen:F2}, IsDestroyed={progressDoor.IsDestroyed}.";

            Puts(report);
            player.ChatMessage($"[LivingRust] {report}");
        }

        foreach (PressButton pressButton in pressButtons)
        {
            float distance = Vector3.Distance(player.transform.position, pressButton.transform.position);
            string report = $"pressbutton-diag: '{pressButton.ShortPrefabName}' (net.ID={pressButton.net?.ID}) at {pressButton.transform.position}, {distance:F2}m away - IsOn={pressButton.IsOn()}, pressDuration={pressButton.pressDuration:F2}, IsDestroyed={pressButton.IsDestroyed}.";

            Puts(report);
            player.ChatMessage($"[LivingRust] {report}");

            // Wiring dump (2026-08-21, Nuclear Missile Silo) - same
            // IOEntity.outputs/IOSlot.connectedTo/IORef.Get() walk
            // TryResolveConnectedDoor already uses for WheelSwitch, applied
            // to PressButton to find out what a real "elevator call"
            // button is actually wired to (an Elevator? the door? both?)
            // instead of guessing.
            if (pressButton.outputs != null)
            {
                foreach (IOEntity.IOSlot output in pressButton.outputs)
                {
                    IOEntity connected = output.connectedTo?.Get(false);
                    string wireReport = connected == null
                        ? $"pressbutton-wiring-diag: '{pressButton.ShortPrefabName}' output slot has no connection."
                        : $"pressbutton-wiring-diag: '{pressButton.ShortPrefabName}' output -> '{connected.ShortPrefabName}' (type={connected.GetType().Name}, net.ID={connected.net?.ID}).";

                    Puts(wireReport);
                    player.ChatMessage($"[LivingRust] {wireReport}");
                }
            }
        }

        foreach (Elevator elevator in elevators)
        {
            float distance = Vector3.Distance(player.transform.position, elevator.transform.position);
            Elevator mover = TryResolveElevatorMover(elevator);
            string report = $"elevator-diag: '{elevator.ShortPrefabName}' (net.ID={elevator.net?.ID}) at {elevator.transform.position}, {distance:F2}m away - Floor={elevator.Floor}, IsStatic={elevator is ElevatorStatic}, mover.net.ID={mover.net?.ID}, mover.IsBusy={mover.IsBusy()}, IsDestroyed={elevator.IsDestroyed}.";

            Puts(report);
            player.ChatMessage($"[LivingRust] {report}");
        }

        foreach (ElevatorLift elevatorLift in elevatorLifts)
        {
            float distance = Vector3.Distance(player.transform.position, elevatorLift.transform.position);
            Elevator mover = elevatorLift.ownerElevator.Get(false);
            string report = $"elevatorlift-diag: '{elevatorLift.ShortPrefabName}' (net.ID={elevatorLift.net?.ID}) at {elevatorLift.transform.position}, {distance:F2}m away - ownerElevator.net.ID={mover?.net?.ID.ToString() ?? "null"}, ownerElevator.Floor={(mover != null ? mover.Floor.ToString() : "n/a")}, ownerElevator.IsBusy={(mover != null ? mover.IsBusy().ToString() : "n/a")}, IsDestroyed={elevatorLift.IsDestroyed}.";

            Puts(report);
            player.ChatMessage($"[LivingRust] {report}");
        }

        // All-layers, all-colliders diagnostic pass - catches anything the
        // World-layer-only scan above would miss entirely, whether it's a
        // real ElectricSwitch on an unexpected layer or a genuinely
        // different entity class.
        Collider[] rawHits = Physics.OverlapSphere(player.transform.position, CardPuzzleInteractionRadius, ~0, QueryTriggerInteraction.Collide);
        HashSet<BaseEntity> reportedEntities = new();

        foreach (Collider hit in rawHits)
        {
            BaseEntity entity = hit.GetComponentInParent<BaseEntity>();

            if (entity == null || !reportedEntities.Add(entity))
            {
                continue;
            }

            string typeName = entity.GetType().Name;

            // "elevator"/"lift" added 2026-08-21 - Nuclear Missile Silo,
            // to catch a real call-panel Elevator entity that might sit on
            // a physics layer CardReaderLayerMask doesn't cover, same
            // reasoning as the original "switch" case this pass was built
            // for.
            bool looksRelevant = typeName.IndexOf("Switch", StringComparison.OrdinalIgnoreCase) >= 0
                || entity.ShortPrefabName.IndexOf("switch", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("Elevator", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("Lift", StringComparison.OrdinalIgnoreCase) >= 0
                || entity.ShortPrefabName.IndexOf("elevator", StringComparison.OrdinalIgnoreCase) >= 0
                || entity.ShortPrefabName.IndexOf("lift", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!looksRelevant)
            {
                continue;
            }

            float distance = Vector3.Distance(player.transform.position, entity.transform.position);
            string report = $"switch-diag-rawscan: '{entity.ShortPrefabName}' (type={typeName}, net.ID={entity.net?.ID}) at {entity.transform.position}, {distance:F2}m away, collider layer={LayerMask.LayerToName(hit.gameObject.layer)}, entity layer={LayerMask.LayerToName(entity.gameObject.layer)}.";

            Puts(report);
            player.ChatMessage($"[LivingRust] {report}");
        }
    }

    /// <summary>
    /// CardReader.GrantCard() has no built-in validation of its own (the
    /// real ServerCardSwiped RPC does that BEFORE choosing GrantCard() vs
    /// FailCard() - confirmed via IL, GrantCard() takes no arguments) - so
    /// the tier match has to be checked here first. reader.accessLevel
    /// (real public field, confirmed via IL that ServerCardSwiped compares
    /// it against the swiped Keycard's own accessLevel) resolves via
    /// KeycardTierByReaderAccessLevel (still unconfirmed, see this file's
    /// own top doc comment) to decide which keycard shortname to look for -
    /// an unresolvable accessLevel just means nothing gets swiped, logged
    /// for visibility rather than silently failing. Deducts 1.0 real
    /// Item.condition per swipe (keycards: max 4.0 for green/blue, 2.0 for
    /// red, not repairable - confirmed via Bundles/items/keycard_*.json),
    /// removing the card outright once truly spent, same as a real
    /// depleted keycard would be.
    /// </summary>
    private void TrySwipeCard(Survivor survivor, BasePlayer npc, CardReader reader)
    {
        if (!KeycardTierByReaderAccessLevel.TryGetValue(reader.accessLevel, out KeycardTier requiredTier))
        {
            Puts($"card-puzzle-diag: '{survivor.Character.Alias}' found a reader with accessLevel={reader.accessLevel}, not in KeycardTierByReaderAccessLevel - can't tell which card it needs.");
            return;
        }

        // Only swipe if this tier is actually one the puzzle route needs -
        // 2026-08-20, real live bug: Water Treatment Plant physically has a
        // green CardReader and a blue CardReader sitting at the exact same
        // spot, but its registered puzzle only needs blue. Without this
        // check, a survivor incidentally carrying a green card (the common
        // Tier 1 default most other monuments use) walking past the
        // co-located green reader got it swiped too, burning real
        // durability on a tier this specific route never asked for.
        if (_activePuzzleRequiredTiers.TryGetValue(survivor.Character.Id, out KeycardTier[] requiredTiers)
            && Array.IndexOf(requiredTiers, requiredTier) < 0)
        {
            VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' found a {requiredTier} reader (accessLevel={reader.accessLevel}) but this puzzle doesn't need that tier - not swiping.");
            return;
        }

        Item card = npc.inventory.FindItemByItemName(KeycardShortnames[requiredTier]);

        if (card == null || card.condition <= 0f)
        {
            VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' found a {requiredTier} reader (accessLevel={reader.accessLevel}) but isn't carrying a usable {KeycardShortnames[requiredTier]}.");
            return;
        }

        // 2026-08-21: briefly tried making the survivor visibly hold the
        // keycard as its active item for the swipe (MoveToContainer-to-
        // belt-then-UpdateActiveItem-then-ForceRefreshHeldEntity, same
        // pattern EquipBestMeleeTool/TryEquipBestArmedWeapon already use) -
        // reverted same day after a live test showed a real regression: a
        // bot that swiped a card shortly before its next fight came out
        // holding a visually invisible AK (server-side still genuinely the
        // AK - reload-diag/damage-diag both confirmed real rifle.ak
        // behavior - but nothing rendered on the client). This codebase's
        // own history already flags rapid held-item swaps as fragile for a
        // connectionless bot (see ForceRefreshHeldEntity's own doc comment:
        // "still reported live even with this in place" for a near-
        // identical case) - adding a THIRD swap right before combat's own
        // swap for a purely cosmetic benefit wasn't worth the risk,
        // especially since the reader's own accessGrantedEffect/
        // swipeEffect already plays automatically off GrantCard() (confirmed
        // via IL: GrantCard() takes no arguments at all) regardless of what
        // the survivor is holding. Card durability/tier logic below is
        // unchanged - only the visual equip step was removed.
        reader.GrantCard();

        card.condition -= 1f;

        bool cardConsumed = card.condition <= 0f;

        if (cardConsumed)
        {
            card.Remove(0f);
        }
        else
        {
            card.MarkDirty();
        }

        VerbosePuts($"card-puzzle: '{survivor.Character.Alias}' swiped a '{KeycardShortnames[requiredTier]}' at a reader (accessLevel={reader.accessLevel}, condition now {Mathf.Max(card.condition, 0f):F0}).");
    }
}
