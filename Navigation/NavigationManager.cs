using System;
using System.Collections.Generic;
using Facepunch;
using LivingRust.Core;
using Rust.Ai.Gen2;
using UnityEngine;
using UnityEngine.AI;

namespace LivingRust.Navigation
{
    /// <summary>
    /// Real navigation logic for survivors: finding nearby water and
    /// stepping toward a destination one tick at a time. No navmesh yet -
    /// straight-line movement is the "walk" step of the crawl-walk-run plan.
    /// </summary>
    public enum StepResult
    {
        Clear,
        SteppedUp,
        Blocked
    }

    public class NavigationManager
    {
        private const float WaterDetectionMargin = 0.25f;

        /// <summary>
        /// The general-purpose "solid world object" mask Rust's own code
        /// uses repeatedly for obstacle/ground checks (helicopter and
        /// vehicle AI, line-of-sight, etc), plus Default (scatter props,
        /// loot crates/barrels), Tree (all TreeEntity, including dead/
        /// fallen trees), Vehicle World/Vehicle Detailed (cars,
        /// helicopters, minicopters, boats, battering rams), and Ragdoll
        /// (player corpses) - all confirmed via /lr.debug.look/.nearby
        /// rather than guessed. Ragdoll was missing entirely until a user
        /// report that corpses (a real despawn's expected byproduct - see
        /// DespawnAllBots) blocked their own movement but not a bot's: a
        /// real player's physics collides with everything, but neither
        /// this mask nor the separate player-layer check
        /// (IsBlockedByOtherPlayer) covered layer 9, so bots walked
        /// straight through solid corpses undetected. Deliberately
        /// excludes "Prevent Building" - that's a vehicle's no-build
        /// trigger zone, not its physical body, and a real player can
        /// walk straight through it.
        /// </summary>
        private static readonly int ObstacleLayerMask = LayerMask.GetMask("Terrain", "World", "Construction", "Default", "Tree", "Vehicle World", "Vehicle Detailed", "Ragdoll");

        /// <summary>
        /// Obstacles at or below this height above the ground get stepped
        /// up onto rather than blocking movement - covers things like a
        /// fallen log, a low rock, or a junkpile crate. Taller than this is
        /// treated as an actual wall and blocks movement instead. Bumped
        /// from 1.0m as an experiment to let bots climb over more junkpile-
        /// height clutter directly rather than always detouring around it -
        /// still well short of vehicle/wall heights, so those should keep
        /// blocking as before.
        /// </summary>
        private const float MaxStepUpHeight = 1.3f;

        /// <summary>
        /// Symmetric counterpart to MaxStepUpHeight for stepping DOWN -
        /// without this, TryGetNextStep only ever rejected steps that were
        /// too tall to climb, never ones that were too far to drop, so a
        /// bot walking toward a destination past the edge of an elevated
        /// platform (e.g. a monument tower) would smoothly glide all the
        /// way down to whatever surface the probe found below - confirmed
        /// via a captured position trace showing a controlled ~11m descent
        /// at the MaxClimbSpeed-clamped rate, not a real physics fall, and
        /// visibly "inhuman" in-game as a result.
        /// </summary>
        private const float MaxStepDownHeight = 1.3f;

        /// <summary>
        /// How far above the point actually being walked toward (target,
        /// not just the immediate flattened step) a single step is allowed
        /// to climb before it's rejected outright, even if MaxStepUpHeight
        /// alone would've allowed it. Closes a real "staircase-climbing"
        /// exploit (2026-08-16, confirmed live: 'JitteryWolf' ended up
        /// standing on top of a store countertop while trying to reach a
        /// perfectly ordinary ground-floor loot target) - MaxStepUpHeight
        /// only ever checks the PER-TICK delta relative to current
        /// position, so a long enough series of individually-legal small
        /// steps (each one a real, valid "step up onto this ledge") can
        /// compound into standing somewhere no real person would climb to
        /// while just trying to walk somewhere flat, the same mechanical
        /// shape as the real player exploit of climbing stacked crates
        /// onto rooftops. Checking against target.y instead of a fixed
        /// "since real ground" baseline is what keeps this from breaking
        /// legitimate multi-corner routes up real stairs/ramps: a genuine
        /// staircase path's own corners are themselves progressively
        /// higher, so climbing toward an already-elevated target still
        /// passes cleanly - only climbing far ABOVE a target that's still
        /// at ground level gets rejected. Generous enough (2.5m) to allow
        /// stepping up onto a genuine low platform/curb right at the
        /// destination itself, tight enough to stop a multi-metre climb
        /// onto furniture a real player would just walk around.
        /// </summary>
        private const float MaxClimbAboveTargetHeight = 2.5f;

        /// <summary>
        /// How close to the FAR target this climb-above-target check even
        /// applies (2026-09-01, Lucas's own live video: 'HazyPoacher3414'
        /// crawling at ~1m/s down a perfectly ordinary grassy hillside,
        /// visibly walking not stuck). Root cause: target here is the
        /// FINAL destination the whole walk is headed toward, not a nearby
        /// waypoint - for any walk where start and destination sit at
        /// meaningfully different elevations (any hillside route, common
        /// on real terrain), EVERY early step's local ground is naturally
        /// still far above the destination's fixed y simply because the
        /// walk hasn't gotten there yet, which the original unconditional
        /// check couldn't tell apart from a genuine wrong-staircase climb.
        /// The bug this check actually exists to catch (JitteryWolf ending
        /// up on a store countertop reaching "a perfectly ordinary ground-
        /// floor loot target," per this const's own doc comment above) was
        /// already a close-range scenario - gating on real proximity to the
        /// target preserves that fix while no longer rejecting the first
        /// 90%+ of any ordinary sloped walk. Comfortably wider than
        /// MaxClimbAboveTargetHeight's own reach so the check still has
        /// several real steps to catch a genuine wrong-turn climb before
        /// arrival, not just the literal last step.
        /// </summary>
        private const float ClimbAboveTargetCheckRadius = 15f;

        /// <summary>
        /// Reference height for detecting a genuine wall/overhang blocking
        /// the path outright - distinct from MaxStepUpHeight, which is
        /// about how big a step the feet can take. Using MaxStepUpHeight
        /// for both caused obstacles close to that height (a cardboard
        /// box, a chest-height crate) to have their top edge clip the
        /// headroom ray and get blocked before ever measuring whether
        /// they were actually short enough to step onto. Roughly a
        /// standing player's height.
        /// </summary>
        private const float HeadClearance = 1.7f;

        /// <summary>
        /// Fallback headroom check height, used when the standing-height
        /// HeadClearance ray is blocked - real players can duck under a low
        /// beam/overhang instead of being stopped by it outright. Matches
        /// BasePlayer.DuckedHeight (1.1) exactly, confirmed via reflection
        /// over the decompiled Assembly-CSharp.dll rather than the previous
        /// 1.0 guess (eyeballed from a screenshot on a different beam). This
        /// probes slightly higher than the old value, not lower - it makes
        /// the check match a real crouched player's actual capsule top
        /// instead of approximating it, but doesn't by itself add margin at
        /// a tight gap (the Powerline tower's PlankBlocker duck is only
        /// ~1.2m clear at the point scanned, barely more than this height -
        /// if the bot is still stuck after this fix, the real gap is
        /// genuinely that tight and needs a different answer, not a bigger
        /// magic number).
        /// </summary>
        private const float DuckClearance = 1.1f;

        /// <summary>
        /// Chest/torso height above a candidate landing surface for
        /// BodyOverlapCheckRadius's volumetric safety check below - roughly
        /// where a standing player's body would actually be, distinct from
        /// HeadClearance (used for the path-of-travel headroom ray, not a
        /// check of the landing spot itself).
        /// </summary>
        private const float BodyOverlapCheckHeight = 0.9f;

        /// <summary>
        /// Roughly a standing player's own body radius - used with
        /// Physics.CheckSphere at the candidate landing position as a final
        /// volumetric safety net, not just the discrete raycasts above.
        /// Junkpile-style compound meshes (a haphazard stack of tires,
        /// boxes, barrels) are irregular and gap-riddled enough that the
        /// sparse point-raycasts (a 5-point ground probe, a single headroom
        /// ray) can find a technically-clear path while still missing a
        /// solid part of the mesh the survivor's actual body volume would
        /// overlap once standing there - those checks were designed against
        /// more regular shapes (monument walls, ramps) and don't fully hold
        /// up against genuinely chaotic prop clutter. Confirmed via live
        /// reports of bots visually "phasing through"/ending up embedded
        /// inside junkpile geometry despite the existing checks reporting a
        /// clear step.
        /// </summary>
        private const float BodyOverlapCheckRadius = 0.35f;

        /// <summary>
        /// Volumetric solid-geometry overlap check at a candidate body
        /// position - the exact same safety net TryGetNextStep already
        /// applies to every hand-built step, exposed publicly so native
        /// movement (which never runs TryGetNextStep at all - it trusts
        /// Unity's navmesh-driven position directly, with no equivalent
        /// check of its own) can use it too before actually applying a
        /// position update. Built after a live report of a bot visibly
        /// clipping through a rock pile while native movement was driving
        /// it - a real scientist2, also native-driven, never did the same
        /// at the identical spot (confirmed via a live /lr.debug.tracenpc
        /// capture), meaning the baked navmesh itself is fine there; what
        /// was actually missing was our own safety net, not native
        /// pathing accuracy.
        /// </summary>
        public bool IsBodyOverlapping(Vector3 position) => IsBodyOverlapping(position, out _);

        /// <summary>
        /// Same real check as the parameterless overload, but also reports
        /// WHICH collider actually blocked it (2026-08-21) - every other
        /// TryGetNextStep block reason already names its collider
        /// (get_gameObject().name, e.g. the surfaceName-based messages
        /// nearby), but this one specifically couldn't, since the original
        /// bool-only OverlapSphere loop discarded the hit the instant it
        /// found one. That gap made this exact class of false positive
        /// (a compound tunnel/building shell mesh - Military Tunnel's
        /// `tunnel.single.straight.36`, Abandoned Supermarket's own
        /// doorway shell, both already fixed via NonSteppableColliderNames)
        /// invisible in the log for any NEW monument hitting the same
        /// pattern - a bot stuck oscillating at Nuclear Missile Silo's
        /// tunnel only ever logged the generic "body would overlap solid
        /// geometry," with no way to tell whether it's a genuine obstacle
        /// or the same kind of over-eager compound-mesh collider already
        /// excluded elsewhere.
        /// </summary>
        public bool IsBodyOverlapping(Vector3 position, out string blockingColliderName)
        {
            blockingColliderName = null;

            Vector3 bodyCheckPoint = new Vector3(position.x, position.y + BodyOverlapCheckHeight, position.z);

            // OverlapSphere + skip NonSteppableColliderNames (2026-08-16),
            // not a plain CheckSphere - this was the one remaining place
            // the whole-building compound shell (confirmed the exact root
            // cause behind the doorway step-block AND the ground-probe
            // false positive earlier the same session) could still block
            // movement unfiltered: a live /lr.debug.scan right at the
            // doorway threshold, AFTER the blocking barricade had already
            // been destroyed, still reported Blocked with reason "body
            // would overlap solid geometry" - CheckSphere has no way to
            // report (or exclude) which collider it actually hit, so this
            // exact false positive was invisible to every other fix.
            Collider[] hits = Physics.OverlapSphere(bodyCheckPoint, BodyOverlapCheckRadius, ObstacleLayerMask, QueryTriggerInteraction.Ignore);

            foreach (Collider hit in hits)
            {
                if (IsNonSteppableCollider(hit.gameObject.name))
                {
                    continue;
                }

                // See KnownTerrainIntrusionPockets' own doc comment - a
                // real 'Terrain' hit can't be excluded by name globally
                // like the entries above, so this is gated on position
                // (only within the one small, confirmed-bad pocket)
                // instead.
                if (hit.gameObject.name == "Terrain" && IsKnownTerrainIntrusionPocket(position))
                {
                    continue;
                }

                blockingColliderName = hit.gameObject.name;
                return true;
            }

            return false;
        }

        /// <summary>
        /// How far above the (coarse, smoothed) heightmap-estimated ground
        /// height the surface probe starts searching from. Needs to clear
        /// the tallest realistic obstacle plus margin - some terrain
        /// features (rock outcrops sculpted into the actual collision
        /// mesh) rise higher above the smoothed heightmap value than
        /// MaxStepUpHeight alone accounts for. If the probe start point
        /// itself ends up inside solid geometry, Unity's raycast can't
        /// detect that collider at all (a ray can't hit what it starts
        /// inside of), so this needs real headroom rather than just
        /// MaxStepUpHeight + a small margin.
        /// </summary>
        private const float ProbeStartHeight = 4f;

        /// <summary>
        /// How far down the surface probe searches from ProbeStartHeight.
        /// Must reach below ground level so a probe that starts above a
        /// tall obstacle can still find the ground on the far side of it.
        /// </summary>
        private const float ProbeSearchDistance = 6f;

        /// <summary>
        /// Small cross-pattern of horizontal offsets used to probe for an
        /// obstacle's surface height with several plain raycasts instead
        /// of one SphereCast.
        /// </summary>
        private static readonly Vector3[] ProbeOffsets =
        {
            Vector3.zero,
            new Vector3(0.2f, 0f, 0f),
            new Vector3(-0.2f, 0f, 0f),
            new Vector3(0f, 0f, 0.2f),
            new Vector3(0f, 0f, -0.2f)
        };

        /// <summary>
        /// Maximum angle (from straight up) a probe hit's surface normal
        /// can have before it's rejected as "not a walkable surface" -
        /// without this, a downward probe over a monument's diagonal
        /// structural bracing can hit the beam's own slanted face instead
        /// of the real platform below it, misreporting a route as blocked
        /// by a "step too high" that doesn't actually exist. The
        /// original ~51-degree estimate this was calibrated from (backed
        /// into from the MaxClimbSpeed-clamped step-up rate observed
        /// climbing a real ramp) turned out to be an artifact of the clamp
        /// itself, not the ramp's true slope - any ramp steeper than that
        /// produces the identical clamped-rate signature, so it couldn't
        /// actually bound how steep a legitimate ramp might be. Confirmed
        /// via a follow-up trace that 60 degrees alone still let some
        /// structural surfaces through; see NonSteppableColliderNames for
        /// the more reliable fix for this monument's specific case.
        /// </summary>
        private const float MaxWalkableSurfaceAngle = 60f;

        /// <summary>
        /// Collider names that should never be treated as solid, no matter
        /// what a probe ray hits - confirmed via repeated debug scans that
        /// 'powerline_a (1)' (the Powerline monument's tower frame - a
        /// single compound MeshCollider spanning the entire tower) is
        /// present at essentially every probe point near the tower yet
        /// never represents anywhere a character should actually stand or
        /// a real wall a player couldn't walk past. The real walkable
        /// surface, and the real obstructions, are consistently ALSO
        /// present as separate, correctly-named colliders (platform/ramp
        /// meshes, PlankBlocker, etc.), so excluding this one by name
        /// removes false "step too high" and false headroom/duck blocks
        /// alike without risking legitimate movement elsewhere. Originally
        /// only applied to the ground-surface probe below; the PlankBlocker
        /// duck gap near the tower's peak turned out to also be clipped by
        /// this same compound mesh at headroom/duck height, so
        /// TryGetNextStep's headroom and duck raycasts now go through
        /// FirstSteppableHit too instead of a raw Physics.Raycast.
        /// </summary>
        private static readonly HashSet<string> NonSteppableColliderNames = new()
        {
            "powerline_a (1)",

            // Abandoned Supermarket's own single compound building-shell
            // MeshCollider (27m x 7.4m x 26.7m bounds - the whole
            // structure, confirmed via /lr.debug.scan). Identical false-
            // positive shape to powerline_a (1) above: a live scan right at
            // a doorway threshold (-334.89, 31.44, -576.80) hit this
            // collider at height 33.56 - the interior ceiling/roof, not the
            // real floor sitting around Y=31.4-31.5 just inside the door -
            // and TryGetNextStep read that as a bogus 2.12m "step too high"
            // wall exactly at the doorway. The real floor is a separate,
            // correctly-named collider, so excluding this shell mesh here
            // doesn't risk losing any genuine collision.
            "supermarket_a",

            // Military Tunnels' own compound tunnel-segment MeshCollider
            // (2026-08-16, real live bug: SlyScav stuck repeatedly
            // reporting "step too high onto 'tunnel.single.straight.36'
            // (3.0m+ > 1.30m max)" trying to reach real loot spotted by the
            // ghost-route's own loot-detour scan - same false-positive
            // shape as powerline_a (1)/supermarket_a above: a single
            // compound mesh spanning the whole tunnel segment's interior
            // (walls, ceiling, floor all one collider), so a ground probe
            // near the segment can land on the ceiling/wall portion instead
            // of the real floor. The real floor is a separate, correctly-
            // named collider (walkway_panel etc, confirmed via the same
            // /lr.debug.scan), so excluding this shell here doesn't risk
            // losing any genuine collision.
            "tunnel.single.straight.36",

            // Harbor's own portacabin building shell, right by harbor_2's
            // dock/bollard area near the Cardreader_A puzzle room
            // (2026-08-18, real live bug: repeated across FOUR separate
            // survivors - CrazyGoblin718/FastNomad/SavageGoblin1426/
            // JitteryScrapper - every one hit the identical false block
            // "step too high onto 'portacabin_building_300_900_b_blue'
            // (2.95m > 1.30m max)" trying to reach real loot near the
            // reader). Same false-positive shape as powerline_a (1)/
            // supermarket_a/tunnel.single.straight.36 above - a single
            // compound building-shell mesh, so a ground probe near it can
            // land on an interior wall/ceiling portion instead of the real
            // dock floor. This is also what was dragging survivors into the
            // long wiggle/emergency-teleport/last-resort-phase stuck-
            // recovery chain near the puzzle room, visible as "gets close
            // to the reader, then reverses and walks away" - that's the
            // stuck-recovery's own wiggle, not a deliberate route decision.
            "portacabin_building_300_900_b_blue",

            // Nuclear Missile Silo's door hinge prop, right at the tunnel
            // spot combat kept oscillating at (2026-08-21, real live
            // debug-scan evidence: TryGetNextStep verdict=Blocked,
            // blockReason="body would overlap solid geometry ('hinge')"
            // standing exactly on the stuck spot). Different SHAPE of
            // false positive from the compound-mesh entries above (a real
            // door's small hinge/pivot collider, not a whole-structure
            // shell), but the same underlying story - a real player walks
            // straight past a door hinge without it ever being a body-
            // blocking obstacle, so treating it as solid here is wrong
            // regardless of its physical size. Whether this alone fully
            // resolves the oscillation is NOT yet confirmed live - the
            // SAME live incident also logged this exact waypoint as
            // blocked by 'Terrain' repeatedly, a second, unrelated-looking
            // cause not yet root-caused (worth re-testing after this
            // change before assuming it's fully fixed).
            "hinge",
        };

        /// <summary>
        /// Real live bug (2026-08-29, Lucas's own report: a bot couldn't
        /// get through its own just-built doorway, oscillating in place
        /// at the threshold). Confirmed via debug-scan - the EXACT same
        /// false-positive shape every entry in NonSteppableColliderNames
        /// above already documents (powerline_a (1)/supermarket_a/etc): a
        /// straight-down ground-probe ray from above a doorway will
        /// always hit the frame's own solid header/lintel geometry first
        /// (a real doorway needs a horizontal approach through the gap
        /// underneath, not a vertical probe onto it), misreading the
        /// header's height (~31.1) as "the ground" and the real floor
        /// just inside (~28.9) as a bogus 2.3m "step too high" wall.
        /// Substring match rather than another exact HashSet entry - a
        /// real wall.doorway's own collider gameobject name is grade-
        /// specific (confirmed via decompile: ConstructionSkin swaps in a
        /// different mesh per grade, e.g. "...wall.doorway.twig.prefab"
        /// at Twigs vs a different literal string once upgraded to Wood/
        /// Stone/Metal/TopTier), so a single exact-string entry would
        /// silently stop working the moment Lucas's own upgrade system
        /// (already shipped this session) touches the piece - "wall.doorway"
        /// itself is the one stable substring common to every real grade
        /// variant.
        /// </summary>
        private static bool IsNonSteppableCollider(string colliderName)
        {
            return colliderName != null
                && (NonSteppableColliderNames.Contains(colliderName)
                    || colliderName.IndexOf("wall.doorway", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// One confirmed real-terrain intrusion pocket, monument-relative
        /// (same local-offset technique as the plugin's own
        /// MonumentAvoidZones, duplicated here since NavigationManager
        /// doesn't share that private dictionary) - NOT a collider-name
        /// exclusion like NonSteppableColliderNames above, because this
        /// false positive is genuinely the real 'Terrain' heightmap
        /// collider itself, not a mislabeled/oversized prop. Unlike a
        /// building shell or door hinge, "Terrain" can't be excluded by
        /// name globally - it's the same collider every single legitimate
        /// ground-following check on the whole map relies on. Confirmed
        /// live and repeatedly (2026-08-21/22, Nuclear Missile Silo's
        /// first tunnel stretch): the visual tunnel floor here sits above
        /// the real terrain heightmap, which was never fully carved out
        /// underneath it - a real player just walks across the visible
        /// floor and never notices, but this project's own volumetric
        /// body-overlap safety net (IsBodyOverlapping) reaches down into
        /// that buried intrusion and reports it as solid, at the exact
        /// spot a bot needs to pass through to reach the elevator/card
        /// room. Scoped to a small radius around the ONE known-bad local
        /// offset (matches the avoid-zone seeded in LivingRust.Main.cs at
        /// the same world position) rather than ignoring Terrain hits
        /// monument-wide, so this stays a narrow, precisely-targeted fix
        /// instead of a blanket "ignore terrain here" that could hide a
        /// real future terrain problem elsewhere in the same monument.
        /// </summary>
        private static readonly (string MonumentSubstring, Vector3 LocalOffset, float Radius)[] KnownTerrainIntrusionPockets =
        {
            ("nuclear_missile_silo", new Vector3(-9.25f, 35.58f, -0.29f), 3f),
        };

        private static bool IsKnownTerrainIntrusionPocket(Vector3 worldPosition)
        {
            foreach ((string monumentSubstring, Vector3 localOffset, float radius) in KnownTerrainIntrusionPockets)
            {
                foreach (MonumentInfo monument in TerrainMeta.Path.Monuments)
                {
                    if (monument.name.IndexOf(monumentSubstring, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    if (Vector3.Distance(monument.transform.InverseTransformPoint(worldPosition), localOffset) <= radius)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public NavigationManager()
        {
            LivingRust.Core.Logger.Info("NavigationManager initialized.");
        }

        /// <summary>
        /// Finds the nearest monument/POI whose display name or object
        /// name contains nameQuery (case-insensitive), and returns a
        /// walkable point on its bounds edge closest to origin - using the
        /// bounds edge rather than the monument's raw transform position,
        /// since for sprawling monuments (airfield, harbor) the center
        /// point often isn't a sensible or reachable destination.
        /// </summary>
        public bool TryFindNearestMonument(Vector3 origin, string nameQuery, out Vector3 destination, out string matchedName)
        {
            MonumentInfo closest = null;
            float closestDistanceSqr = float.MaxValue;

            foreach (MonumentInfo monument in TerrainMeta.Path.Monuments)
            {
                string displayName = monument.displayPhrase.IsValid() ? monument.displayPhrase.english : null;

                bool matches = (displayName != null && displayName.IndexOf(nameQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                    || monument.name.IndexOf(nameQuery, StringComparison.OrdinalIgnoreCase) >= 0;

                if (!matches)
                {
                    continue;
                }

                float distanceSqr = (monument.transform.position - origin).sqrMagnitude;

                if (distanceSqr < closestDistanceSqr)
                {
                    closestDistanceSqr = distanceSqr;
                    closest = monument;
                }
            }

            if (closest == null)
            {
                destination = default;
                matchedName = null;
                return false;
            }

            destination = closest.ClosestPointOnBounds(origin);
            destination.y = TerrainMeta.HeightMap.GetHeight(destination);
            matchedName = closest.displayPhrase.IsValid() ? closest.displayPhrase.english : closest.name;

            return true;
        }

        /// <summary>
        /// Lists every monument matching nameQuery with its exact world
        /// position, ordered nearest-to-origin first - useful when a name
        /// matches multiple instances (e.g. several "Substation"s on one
        /// map) and it's unclear which one a nearest-match search would
        /// actually pick.
        /// </summary>
        public List<(string Name, Vector3 Position, float Distance)> FindAllMonumentMatches(Vector3 origin, string nameQuery)
        {
            var matches = new List<(string, Vector3, float)>();

            foreach (MonumentInfo monument in TerrainMeta.Path.Monuments)
            {
                string displayName = monument.displayPhrase.IsValid() ? monument.displayPhrase.english : null;

                bool matches2 = (displayName != null && displayName.IndexOf(nameQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                    || monument.name.IndexOf(nameQuery, StringComparison.OrdinalIgnoreCase) >= 0;

                if (!matches2)
                {
                    continue;
                }

                Vector3 position = monument.transform.position;
                matches.Add((displayName ?? monument.name, position, Vector3.Distance(origin, position)));
            }

            matches.Sort((a, b) => a.Item3.CompareTo(b.Item3));

            return matches;
        }

        /// <summary>
        /// Lists every monument/POI's display (or fallback object) name,
        /// for discovering what's actually available to path to.
        /// </summary>
        public List<string> GetMonumentNames()
        {
            var names = new List<string>();

            foreach (MonumentInfo monument in TerrainMeta.Path.Monuments)
            {
                names.Add(monument.displayPhrase.IsValid() ? monument.displayPhrase.english : monument.name);
            }

            return names;
        }

        /// <summary>
        /// Searches outward from origin in expanding rings for the nearest
        /// point flagged as water by the terrain/water heightmaps.
        /// </summary>
        public bool TryFindNearestWater(
            Vector3 origin,
            out Vector3 waterPoint,
            float maxRadius = 250f,
            float ringStep = 5f,
            float angleStepDegrees = 15f)
        {
            for (float radius = ringStep; radius <= maxRadius; radius += ringStep)
            {
                for (float angle = 0f; angle < 360f; angle += angleStepDegrees)
                {
                    float radians = angle * Mathf.Deg2Rad;

                    Vector3 candidate = origin + new Vector3(Mathf.Cos(radians), 0f, Mathf.Sin(radians)) * radius;
                    candidate.y = TerrainMeta.HeightMap.GetHeight(candidate);

                    if (IsWater(candidate))
                    {
                        waterPoint = candidate;
                        return true;
                    }
                }
            }

            waterPoint = default;
            return false;
        }

        /// <summary>
        /// Computes a route across Rust's own baked NavMesh (the same one
        /// vanilla NPCs/animals path across) - this handles the big
        /// strategic routing decision (going around a building or a large
        /// rock formation), since anything solid/large enough gets
        /// excluded from the walkable surface at bake time. Local obstacle
        /// handling (TryGetNextStep) still runs for each straight-line
        /// segment between waypoints, handling smaller/dynamic obstacles
        /// the coarse path doesn't itself route around.
        /// </summary>
        public bool TryCalculatePath(Vector3 origin, Vector3 destination, RustNavMeshPath path, out string failureReason)
        {
            failureReason = null;

            // Renamed from RustNavMesh to RustNavMeshHelpers by the Rust
            // update that shipped with the 2026-08-06 wipe - same signature,
            // confirmed via reflection over the updated Assembly-CSharp.dll
            // rather than guessed.
            bool computed = RustNavMeshHelpers.CalculatePath(origin, destination, RustNavMeshHelpers.AllAreas, path);

            if (computed && path.corners.Count > 0 && path.status != NavMeshPathStatus.PathInvalid)
            {
                return true;
            }

            // A point can look perfectly walkable to our own raycasts (solid
            // ground under it) while still being off Rust's separately-baked
            // NavMesh entirely - CalculatePath only ever routes across that
            // baked surface, not whatever a collider check finds. Sampling
            // both ends tells us which side (if either) is actually the
            // problem instead of guessing again.
            failureReason = $"CalculatePath returned {computed}, {path.corners.Count} corners, status {path.status} - origin {DescribeNavMeshProximity(origin)}, destination {DescribeNavMeshProximity(destination)}";

            return false;
        }

        private static string DescribeNavMeshProximity(Vector3 point)
        {
            if (NavMesh.SamplePosition(point, out NavMeshHit hit, 5f, NavMesh.AllAreas))
            {
                return $"{Vector3.Distance(point, hit.position):F2}m from nearest navmesh point {hit.position}";
            }

            return "no navmesh surface found within 5m";
        }

        /// <summary>
        /// Whether this point is water. Uses WaterLevel.GetWaterLevel rather
        /// than the raw terrain WaterMap, since the WaterMap alone only
        /// reflects locally-baked rivers/lakes - the ocean's height only
        /// gets folded in through WaterLevel's topology-aware max() logic.
        /// </summary>
        public bool IsWater(Vector3 pos)
        {
            float waterHeight = WaterLevel.GetWaterLevel(pos, waves: false);
            float landHeight = TerrainMeta.HeightMap.GetHeight(pos);

            return waterHeight > landHeight + WaterDetectionMargin;
        }

        /// <summary>
        /// One raycast result from DebugProbeGroundSurface's replay of
        /// TryGetNextStep's own 5-point ground probe - everything needed to
        /// see exactly why a specific probe point won (or didn't), rather
        /// than only ever seeing TryGetNextStep's own single winning
        /// result. Built specifically to root-cause a live report of
        /// widespread false "step too high" blocks across many different
        /// monument dressing props (awnings, tents, sandbags, vehicles) -
        /// the working theory is that ONE of the 5 offset points is
        /// clipping the top of a nearby low prop instead of the real
        /// ground, and since TryGetNextStep only ever keeps the highest
        /// hit across all 5, a single bad outlier point can silently
        /// override 4 otherwise-correct ground readings.
        /// </summary>
        public readonly struct GroundProbeResult
        {
            public readonly Vector3 Offset;
            public readonly bool Hit;
            public readonly Collider Collider;
            public readonly string ColliderName;
            public readonly Vector3 Point;
            public readonly float SurfaceAngle;
            public readonly bool ExcludedByName;

            public GroundProbeResult(Vector3 offset, bool hit, Collider collider, Vector3 point, float surfaceAngle, bool excludedByName)
            {
                Offset = offset;
                Hit = hit;
                Collider = collider;
                ColliderName = collider != null ? collider.gameObject.name : null;
                Point = point;
                SurfaceAngle = surfaceAngle;
                ExcludedByName = excludedByName;
            }
        }

        /// <summary>
        /// How close together (vertically) two probe hits need to be to
        /// count as "the same surface" for TrySelectConsensusSurface's
        /// majority-vote logic - loose enough to tolerate a slightly
        /// uneven real floor, tight enough that a genuinely different
        /// surface (a real 1m+ step) never gets merged into the wrong
        /// cluster.
        /// </summary>
        private const float SurfaceClusterTolerance = 0.3f;

        /// <summary>
        /// The shared raycast loop behind both TryGetNextStep's real
        /// surface-height measurement and DebugProbeGroundSurface's
        /// diagnostic replay - identical offsets/heights/mask either way,
        /// so the debug tool always reports exactly what real navigation
        /// actually sees.
        /// </summary>
        private List<GroundProbeResult> ProbeGroundSurfaceRaw(Vector3 current, Vector3 flatNext)
        {
            var results = new List<GroundProbeResult>();

            foreach (Vector3 offset in ProbeOffsets)
            {
                Vector3 rayOrigin = new Vector3(flatNext.x + offset.x, current.y + ProbeStartHeight, flatNext.z + offset.z);

                // Real live bug (2026-08-29, Lucas's own report: a bot
                // couldn't walk through its own doorway, oscillating at
                // the threshold). A plain single-hit Physics.Raycast stops
                // at the very first thing it touches - marking a doorway
                // frame's own header collider "excluded by name" (see
                // IsNonSteppableCollider above) did nothing useful on its
                // own, since the raycast never even looked far enough to
                // see the REAL floor sitting just below/behind it. Switched
                // to FirstSteppableHit - the same real RaycastAll-then-
                // skip-excluded pattern this file already uses elsewhere
                // for exactly this reason (its own doc comment: "a plain
                // Raycast only ever reports the very first thing along the
                // ray... the real obstruction (or lack of one) behind it
                // never gets seen") - so a probe through an excluded
                // header now keeps going and actually finds the doorway's
                // real floor underneath instead of coming back empty.
                RaycastHit? steppableHit = FirstSteppableHit(rayOrigin, Vector3.down, ProbeSearchDistance);

                if (steppableHit.HasValue)
                {
                    float angle = Vector3.Angle(steppableHit.Value.normal, Vector3.up);

                    results.Add(new GroundProbeResult(offset, true, steppableHit.Value.collider, steppableHit.Value.point, angle, excludedByName: false));
                }
                else
                {
                    results.Add(new GroundProbeResult(offset, false, null, default, 0f, false));
                }
            }

            return results;
        }

        /// <summary>
        /// Picks whichever surface height the MOST probe points agree on
        /// (within SurfaceClusterTolerance), not just whichever single
        /// point reports the highest hit. The old max-only logic let one
        /// bad outlier silently override several otherwise-correct ground
        /// readings whenever a probe offset happened to clip an unrelated
        /// elevated part of the SAME compound mesh a walkable floor is
        /// also part of - confirmed via a live /lr.debug.scan capture at a
        /// real tent doorway: 4 of 5 probes agreed on height 14.44 (a
        /// genuinely flat, walkable step), the 5th hit the SAME
        /// tent_tunnel_300_junction collider 3.27m higher (its roof/ridge
        /// structure) at a 48-degree angle, and the max-only logic used
        /// that one point as "the ground," falsely blocking a step that
        /// was actually completely clear. A real, deliberate step-up onto
        /// a genuine obstacle (a log, a low wall) is wide enough that
        /// most/all 5 probes agree on ITS height too, so this doesn't
        /// change that case - ties are broken toward the highest cluster,
        /// preserving the original bias when genuinely ambiguous. General
        /// fix rather than a per-prop name exclusion, since this same
        /// compound-mesh pattern turned out to affect a wide, growing list
        /// of unrelated dressing props (tents, awnings, vehicles - 21
        /// distinct colliders seen live), not just one.
        /// </summary>
        private bool TrySelectConsensusSurface(List<GroundProbeResult> probes, out float surfaceHeight, out Collider surfaceCollider)
        {
            surfaceHeight = float.NegativeInfinity;
            surfaceCollider = null;

            var valid = new List<GroundProbeResult>();

            foreach (GroundProbeResult probe in probes)
            {
                if (probe.Hit && !probe.ExcludedByName && probe.SurfaceAngle <= MaxWalkableSurfaceAngle)
                {
                    valid.Add(probe);
                }
            }

            if (valid.Count == 0)
            {
                return false;
            }

            int bestClusterSize = 0;
            float bestClusterHeight = float.NegativeInfinity;
            Collider bestClusterCollider = null;

            foreach (GroundProbeResult candidate in valid)
            {
                int clusterSize = 0;
                float highestInCluster = float.NegativeInfinity;
                Collider colliderAtHighest = null;

                foreach (GroundProbeResult other in valid)
                {
                    if (Mathf.Abs(other.Point.y - candidate.Point.y) <= SurfaceClusterTolerance)
                    {
                        clusterSize++;

                        if (other.Point.y > highestInCluster)
                        {
                            highestInCluster = other.Point.y;
                            colliderAtHighest = other.Collider;
                        }
                    }
                }

                if (clusterSize > bestClusterSize || (clusterSize == bestClusterSize && highestInCluster > bestClusterHeight))
                {
                    bestClusterSize = clusterSize;
                    bestClusterHeight = highestInCluster;
                    bestClusterCollider = colliderAtHighest;
                }
            }

            surfaceHeight = bestClusterHeight;
            surfaceCollider = bestClusterCollider;
            return true;
        }

        /// <summary>
        /// Read-only replay of TryGetNextStep's own ground-surface probe
        /// loop (identical offsets/heights/mask), reporting every one of
        /// the 5 raycasts individually instead of only the winning
        /// (consensus) result. Diagnostic only - never called from real
        /// movement code, so it can't affect actual navigation behavior.
        /// </summary>
        public List<GroundProbeResult> DebugProbeGroundSurface(Vector3 current, Vector3 target)
        {
            Vector3 direction = target - current;
            direction.y = 0f;

            Vector3 flatNext = direction.sqrMagnitude <= 0.0001f
                ? target
                : current + direction.normalized * Mathf.Min(direction.magnitude, 1f);

            return ProbeGroundSurfaceRaw(current, flatNext);
        }

        /// <summary>
        /// Same consensus ground-surface probe TryGetNextStep uses for
        /// every in-progress step, exposed for a stationary point (current
        /// and flatNext are the same position, so the probe rays land
        /// straight down through it - no direction of travel needed).
        /// Added 2026-08-13 for a real live report: bots that stopped
        /// mid-task (arrived, gave up, timed out) could end up visibly
        /// hovering slightly above the ground. Root cause - ApplyMovementStep
        /// clamps how far Y can move in a single tick (MaxClimbSpeed) so
        /// climbing a step reads as gradual rather than teleporting; if a
        /// walk/follow stopped on the exact tick that clamp hadn't yet
        /// finished catching Y up to the real surface height underneath
        /// (mid-way through stepping onto/off a small bump - a pebble, a
        /// dirt-path edge, anything within a single step of uneven ground),
        /// the timer just stopped right there with nothing left to finish
        /// the catch-up. Callers use this for a one-time, unclamped settle
        /// correction the instant movement actually stops - never mid-step.
        /// </summary>
        public bool TryGetGroundHeight(Vector3 position, out float height)
        {
            List<GroundProbeResult> probes = ProbeGroundSurfaceRaw(position, position);
            return TrySelectConsensusSurface(probes, out height, out _);
        }

        /// <summary>
        /// Returns the next straight-line step from current toward target,
        /// snapped to terrain height. Never overshoots the target. If a low
        /// obstacle (like a fallen log) sits in the way, the step rises onto
        /// its top surface instead of stopping - mirroring the step-up
        /// behaviour Rust's own no-navmesh NPC movement uses. A taller
        /// obstacle (a wall) blocks the step entirely.
        /// </summary>
        public StepResult TryGetNextStep(Vector3 current, Vector3 target, float stepDistance, out Vector3 nextStep, out string blockReason, bool ignoreHeadroom = false, bool ignoreStepHeight = false)
        {
            blockReason = null;

            Vector3 direction = target - current;
            direction.y = 0f;

            Vector3 flatNext = (direction.sqrMagnitude <= stepDistance * stepDistance)
                ? target
                : current + direction.normalized * stepDistance;

            // Measure the actual surface height at flatNext (if any) first,
            // before checking for a wall - a cluster of plain raycasts
            // rather than a single SphereCast, since SphereCast/CapsuleCast
            // silently ignore non-convex mesh colliders (a real Unity
            // limitation) that would otherwise let the bot end up embedded
            // inside irregular rock meshes instead of stepping onto them.
            //
            // The probe is anchored to current.y (the bot's real elevation
            // right now), not the coarse terrain heightmap - once standing
            // on an elevated structure (a ramp, a platform many metres up),
            // heightmap queries only ever report ground level, which badly
            // misjudges both where to search and how big a step this is.
            //
            // Consensus (majority-agreement), not a raw max across the 5
            // probes - see TrySelectConsensusSurface's own doc comment for
            // why: a live capture caught a single outlier probe clipping
            // an elevated part of the SAME compound mesh a genuinely flat,
            // walkable floor is also part of, and the old max-only logic
            // let that one bad point override 4 otherwise-correct ground
            // readings.
            List<GroundProbeResult> probes = ProbeGroundSurfaceRaw(current, flatNext);
            bool foundSurface = TrySelectConsensusSurface(probes, out float highestSurface, out Collider highestSurfaceCollider);

            StepResult tentative;

            if (foundSurface)
            {
                // Relative to where the bot actually is now, not distant
                // ground level - this is what makes continuous climbing
                // (a ramp, stacked platforms) work in small increments
                // instead of "height above sea level" wrongly ballooning
                // past MaxStepUpHeight the higher up it climbs.
                float stepUpHeight = highestSurface - current.y;

                // ignoreStepHeight (2026-08-15) - a swimming survivor's Y
                // is externally overridden to a locked swim depth
                // regardless of whatever this method computes (see
                // ClampToWaterSurfaceIfSwimming/CorrectSwimDrift in
                // LivingRust.Commands.cs), so the real underwater terrain's
                // shape - which can genuinely drop off by several metres
                // over a short distance near a real slope - is completely
                // irrelevant to a floating character and shouldn't be able
                // to block it at all. Confirmed live via trace+log: a
                // survivor with a correctly locked, stable swim Y still got
                // stuck repeatedly hitting "step down too far onto
                // 'Terrain'" and never made forward progress, eventually
                // dying to a real player while stuck in place - the
                // Y-locking fix alone wasn't enough, this exact height
                // check (evaluated BEFORE any swim-Y override ever gets a
                // chance to run, since a Blocked step never reaches
                // ApplyMovementStep at all) needed its own bypass too.
                if (!ignoreStepHeight)
                {
                    if (stepUpHeight > MaxStepUpHeight)
                    {
                        nextStep = current;
                        string surfaceName = highestSurfaceCollider != null ? highestSurfaceCollider.gameObject.name : "unknown";
                        blockReason = $"step too high onto '{surfaceName}' ({stepUpHeight:F2}m > {MaxStepUpHeight:F2}m max)";
                        return StepResult.Blocked;
                    }

                    // See MaxClimbAboveTargetHeight's own doc comment - a
                    // separate check from the per-tick one just above,
                    // deliberately comparing against target.y (where this
                    // step is actually headed) rather than current.y, to
                    // catch a staircase-shaped SERIES of individually-legal
                    // steps climbing somewhere the actual destination never
                    // asked for. See ClimbAboveTargetCheckRadius's own doc
                    // comment - only evaluated once genuinely close to
                    // target at all, so an ordinary long walk down/up a
                    // sloped route (where early steps are naturally still
                    // far from the destination's own fixed elevation) isn't
                    // rejected step after step for the same reason a
                    // genuine wrong-turn climb right at arrival should be.
                    float distanceToTarget = Vector3.Distance(current, target);

                    if (distanceToTarget <= ClimbAboveTargetCheckRadius)
                    {
                        float climbAboveTarget = highestSurface - target.y;

                        if (climbAboveTarget > MaxClimbAboveTargetHeight)
                        {
                            nextStep = current;
                            string surfaceName = highestSurfaceCollider != null ? highestSurfaceCollider.gameObject.name : "unknown";
                            blockReason = $"would climb onto '{surfaceName}' {climbAboveTarget:F2}m above the actual target ({MaxClimbAboveTargetHeight:F2}m max) - not a real route toward it";
                            return StepResult.Blocked;
                        }
                    }

                    if (stepUpHeight < -MaxStepDownHeight)
                    {
                        nextStep = current;
                        string surfaceName = highestSurfaceCollider != null ? highestSurfaceCollider.gameObject.name : "unknown";
                        blockReason = $"step down too far onto '{surfaceName}' ({-stepUpHeight:F2}m > {MaxStepDownHeight:F2}m max)";
                        return StepResult.Blocked;
                    }
                }

                flatNext.y = highestSurface;
                tentative = (stepUpHeight > 0.05f) ? StepResult.SteppedUp : StepResult.Clear;
            }
            else
            {
                // Shouldn't normally happen (Terrain itself is in the
                // mask), but fall back to the coarse heightmap rather than
                // leave flatNext.y unset.
                flatNext.y = TerrainMeta.HeightMap.GetHeight(flatNext);
                tentative = StepResult.Clear;
            }

            // Volumetric safety net, not just the discrete raycasts above -
            // see BodyOverlapCheckRadius's own doc comment for the
            // junkpile-clipping case this catches that the sparse point
            // probes above can miss entirely.
            if (IsBodyOverlapping(flatNext, out string overlappingColliderName))
            {
                nextStep = current;
                blockReason = $"body would overlap solid geometry ('{overlappingColliderName}') at the candidate position";
                return StepResult.Blocked;
            }

            // Now check for a genuine wall/overhang at real head height -
            // using MaxStepUpHeight here (instead of HeadClearance) was
            // the bug: something close to that height (a cardboard box)
            // could clip this ray and get blocked before ever reaching the
            // surface-height measurement above. Skipped entirely when
            // ignoreHeadroom is set - ground/step-up/step-down collision
            // above stays fully active either way, only this upper-body
            // check is bypassed. For known choke points where a monument's
            // own oversized structural collider still clips this ray at
            // head/duck height despite NonSteppableColliderNames excluding
            // it elsewhere (the Powerline tower's plank-crossing gap near
            // Ladder_4) - confirmed via a fresh /lr.follow capture that a
            // real player's feet/ground collision stays intact crossing the
            // same gap, so real head-height contact was never the actual
            // obstacle there.
            Vector3 headroomStart = current + Vector3.up * HeadClearance;
            Vector3 headroomEnd = flatNext + Vector3.up * HeadClearance;
            Vector3 headroomOffset = headroomEnd - headroomStart;
            float headroomDistance = headroomOffset.magnitude;

            if (!ignoreHeadroom && headroomDistance > 0.01f)
            {
                RaycastHit? headroomHit = FirstSteppableHit(headroomStart, headroomOffset.normalized, headroomDistance + 0.25f);

                if (headroomHit.HasValue)
                {
                    // Standing height is blocked (a low beam/overhang) - real
                    // players can duck under this instead of being stopped
                    // outright, so retry at duck height before giving up.
                    // Reusing StepResult.SteppedUp (rather than a new enum
                    // value) is deliberate: every caller already treats it as
                    // "play the crouch animation for this step," which is
                    // exactly right here too.
                    Vector3 duckStart = current + Vector3.up * DuckClearance;
                    Vector3 duckEnd = flatNext + Vector3.up * DuckClearance;
                    Vector3 duckOffset = duckEnd - duckStart;

                    if (FirstSteppableHit(duckStart, duckOffset.normalized, duckOffset.magnitude + 0.25f).HasValue)
                    {
                        nextStep = current;
                        blockReason = $"headroom blocked by '{headroomHit.Value.collider.gameObject.name}' at {headroomHit.Value.point}";
                        return StepResult.Blocked;
                    }

                    tentative = StepResult.SteppedUp;
                }
            }

            nextStep = flatNext;
            return tentative;
        }

        /// <summary>
        /// Physics.Raycast, but ignoring anything in NonSteppableColliderNames
        /// instead of letting it count as a hit - a plain Raycast only ever
        /// reports the very first thing along the ray, so if that first hit
        /// is the Powerline tower's bogus compound frame collider, the real
        /// obstruction (or lack of one) behind it never gets seen. Uses
        /// RaycastAll and picks the nearest non-excluded hit instead.
        /// </summary>
        private static RaycastHit? FirstSteppableHit(Vector3 origin, Vector3 direction, float maxDistance)
        {
            RaycastHit[] hits = Physics.RaycastAll(origin, direction, maxDistance, ObstacleLayerMask, QueryTriggerInteraction.Collide);

            RaycastHit? nearest = null;

            foreach (RaycastHit hit in hits)
            {
                if (IsNonSteppableCollider(hit.collider.gameObject.name))
                {
                    continue;
                }

                if (!nearest.HasValue || hit.distance < nearest.Value.distance)
                {
                    nearest = hit;
                }
            }

            return nearest;
        }

        /// <summary>
        /// A single climbable ladder segment, in world space. Derived purely
        /// from the real TriggerLadder's collider bounds (confirmed via
        /// /lr.debug.nearby - the "Ladder Trigger" child object under each
        /// Ladder_N carries a TriggerLadder component) rather than any
        /// internal Rust climbing logic, since our bots don't run through
        /// Rust's own PlayerMovement/input-driven ladder handling at all.
        /// </summary>
        public readonly struct LadderInfo
        {
            /// <summary>World position at the base of the climbable segment.</summary>
            public readonly Vector3 Bottom;

            /// <summary>World position at the top of the climbable segment.</summary>
            public readonly Vector3 Top;

            /// <summary>
            /// Horizontal unit axis (world X or Z) you press into to mount
            /// the ladder - always whichever horizontal axis the trigger box
            /// is thin along, confirmed against two different ladder
            /// segments (0.24m/0.58m thin axis vs 4.88m/4.43m tall axis in
            /// both samples).
            /// </summary>
            public readonly Vector3 MountAxis;

            /// <summary>
            /// TriggerLadder.Type cast to int (Rungs = 0, Rope = 1) - matches
            /// the values ModelState.ladderType expects to pick the correct
            /// client-side climb animation.
            /// </summary>
            public readonly int LadderType;

            public LadderInfo(Vector3 bottom, Vector3 top, Vector3 mountAxis, int ladderType)
            {
                Bottom = bottom;
                Top = top;
                MountAxis = mountAxis;
                LadderType = ladderType;
            }
        }

        /// <summary>
        /// Finds the real ground/floor surface below a point via a plain
        /// downward raycast (not SphereCast - see TryGetNextStep for why).
        /// Used to find a ladder's true dismount floor instead of trusting
        /// the TriggerLadder collider's own bounds, which don't reliably
        /// reach the real floor on every ladder - confirmed on a watchtower
        /// whose trigger bottom sat noticeably above real ground, unlike the
        /// Powerline tower's ladders where the two happened to line up.
        /// </summary>
        public bool TryFindGroundBelow(Vector3 origin, float searchHeight, float searchDistance, out float groundY, out Collider hitCollider)
        {
            // Reuses TryGetNextStep's own 5-point consensus ground probe
            // (2026-08-15), not a single raycast - a single ray, even with
            // NonSteppableColliderNames applied, can still land on any
            // ONE of an effectively unbounded list of ordinary props (a
            // duct, a locker, a shelf - all confirmed live via
            // jitterzone-diag, not just the one giant compound mesh a name
            // exclusion could plausibly enumerate) sitting between the
            // probe start and the real floor. TrySelectConsensusSurface
            // already solves exactly this - 5 nearby offset rays, majority
            // vote wins, so one outlier prop under a single probe point
            // can't override the other 4 agreeing on the real floor.
            // searchHeight/searchDistance are no longer honoured
            // separately (every caller already passed the same 4f/6f the
            // shared probe itself uses) - kept as parameters so existing
            // call sites don't need touching.
            List<GroundProbeResult> probes = ProbeGroundSurfaceRaw(origin, origin);

            if (TrySelectConsensusSurface(probes, out float consensusHeight, out Collider consensusCollider))
            {
                groundY = consensusHeight;
                hitCollider = consensusCollider;
                return true;
            }

            groundY = default;
            hitCollider = null;
            return false;
        }

        /// <summary>
        /// Checks whether a real, melee-destructible Barricade (the actual
        /// game class - confirmed via decompile 2026-08-16, not a name
        /// guess) is near a stuck survivor. Deliberately live/uncached -
        /// every call does a fresh check against whatever's actually in
        /// the world right now, so a bot never needs to "remember" a
        /// barricade was destroyed earlier: once it's gone, this simply
        /// stops finding it, and normal pathing resumes with zero extra
        /// bookkeeping.
        ///
        /// Proximity search (OverlapSphere), not a directional raycast
        /// toward target (2026-08-16 rework) - the original raycast
        /// version never fired on a real, confirmed-present barricade in
        /// live testing ('LostSquatter'), for two compounding reasons: the
        /// short (3m) cast distance can be shorter than how far the bot
        /// actually is from the barricade once EscalateStuckRecovery
        /// finally fires (unlike a direct Blocked-outcome ladder detour,
        /// this only runs after the OUTER give-up watchdog trips, by which
        /// point wiggling/sidestepping may have already carried the
        /// survivor several metres off), and a straight-line-to-
        /// destination raycast can miss a barricade sitting only slightly
        /// off that exact line even at close range. A plain "what's near
        /// me" sphere check has neither failure mode - target is no longer
        /// needed at all.
        /// </summary>
        public bool TryFindBlockingBarricade(Vector3 origin, float maxDistance, out Barricade barricade)
        {
            barricade = null;

            Collider[] hits = Physics.OverlapSphere(origin, maxDistance, ObstacleLayerMask, QueryTriggerInteraction.Ignore);

            Barricade nearest = null;
            float nearestDistanceSqr = float.MaxValue;

            foreach (Collider hit in hits)
            {
                Barricade candidate = hit.GetComponentInParent<Barricade>();

                if (candidate == null || candidate.IsDestroyed || candidate.Health() <= 0f)
                {
                    continue;
                }

                float distanceSqr = (candidate.transform.position - origin).sqrMagnitude;

                if (distanceSqr < nearestDistanceSqr)
                {
                    nearestDistanceSqr = distanceSqr;
                    nearest = candidate;
                }
            }

            barricade = nearest;
            return nearest != null;
        }

        /// <summary>
        /// Finds the nearest collider within maxRadius whose GameObject name
        /// exactly matches targetName. A landmark-based fallback for
        /// structures with no dedicated lookup of their own - unlike
        /// ladders (TriggerLadder) or registered monuments
        /// (TerrainMeta.Path.Monuments), some structures (e.g. the
        /// Powerline tower) are just static dressing with no component or
        /// monument registration to search by, so the sub-object's own
        /// name (already known from a debug scan) is the only stable
        /// handle available.
        /// </summary>
        public bool TryFindNamedStructure(Vector3 origin, string targetName, float maxRadius, out Vector3 position)
        {
            Collider[] hits = Physics.OverlapSphere(origin, maxRadius, ObstacleLayerMask, QueryTriggerInteraction.Collide);

            Collider closest = null;
            float closestDistanceSqr = float.MaxValue;

            foreach (Collider hit in hits)
            {
                if (hit.gameObject.name != targetName)
                {
                    continue;
                }

                float distanceSqr = (hit.bounds.ClosestPoint(origin) - origin).sqrMagnitude;

                if (distanceSqr < closestDistanceSqr)
                {
                    closestDistanceSqr = distanceSqr;
                    closest = hit;
                }
            }

            if (closest == null)
            {
                position = default;
                return false;
            }

            position = closest.bounds.center;
            return true;
        }

        /// <summary>
        /// Finds the nearest TriggerLadder within maxRadius of origin (that
        /// also passes filter, if given) and reports its climb geometry.
        ///
        /// filter is applied *during* the search, not after picking the
        /// single closest candidate - confirmed via a captured debug scan
        /// that the two matter: right after dismounting a ladder, the one
        /// just climbed sits only 0.3-2.9m away while the next one up can
        /// be 8m+ away but still well within maxRadius. Picking nearest-
        /// overall and rejecting it afterward (e.g. via LadderGoesRightWay)
        /// permanently stalls right there, since the actually-reachable
        /// next ladder is never even considered. Callers that don't care
        /// about direction (e.g. /lr.climb, which derives direction from
        /// whichever ladder it finds rather than the other way around) can
        /// pass filter: null for the old nearest-any-ladder behaviour.
        /// </summary>
        public bool TryFindNearestLadder(Vector3 origin, float maxRadius, out LadderInfo ladder, Func<LadderInfo, bool> filter = null)
        {
            int triggerMask = LayerMask.GetMask("Trigger");

            Collider[] hits = Physics.OverlapSphere(origin, maxRadius, triggerMask, QueryTriggerInteraction.Collide);

            LadderInfo? closest = null;
            float closestDistanceSqr = float.MaxValue;

            foreach (Collider hit in hits)
            {
                TriggerLadder trigger = hit.GetComponent<TriggerLadder>();

                if (trigger == null)
                {
                    continue;
                }

                Bounds bounds = hit.bounds;
                Vector3 mountAxis = bounds.size.x <= bounds.size.z ? Vector3.right : Vector3.forward;
                Vector3 bottom = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                Vector3 top = new Vector3(bounds.center.x, bounds.max.y, bounds.center.z);
                var candidate = new LadderInfo(bottom, top, mountAxis, (int)trigger.Type);

                if (filter != null && !filter(candidate))
                {
                    continue;
                }

                float distanceSqr = (bounds.ClosestPoint(origin) - origin).sqrMagnitude;

                if (distanceSqr < closestDistanceSqr)
                {
                    closestDistanceSqr = distanceSqr;
                    closest = candidate;
                }
            }

            if (closest == null)
            {
                ladder = default;
                return false;
            }

            ladder = closest.Value;
            return true;
        }

        /// <summary>
        /// Finds the nearest StorageContainer within maxRadius of origin
        /// (that also passes filter, if given) - covers both world loot
        /// containers (LootContainer, e.g. monument barrels/crates -
        /// confirmed via decompiling Assembly-CSharp.dll that
        /// LootContainer : StorageContainer) and player-placed storage,
        /// since both share the same base type and inventory access.
        /// Uses ObstacleLayerMask rather than a dedicated mask since loot
        /// crates/barrels sit on the "Default" layer already included in
        /// it (see ObstacleLayerMask's own doc comment) - broader than
        /// strictly necessary, but candidates without a StorageContainer
        /// in their parent chain are just skipped, same tradeoff
        /// TryFindNamedStructure already makes.
        /// </summary>
        public bool TryFindNearestLootContainer(Vector3 origin, float maxRadius, out StorageContainer container, Func<StorageContainer, bool> filter = null)
        {
            Collider[] hits = Physics.OverlapSphere(origin, maxRadius, ObstacleLayerMask, QueryTriggerInteraction.Collide);

            StorageContainer closest = null;
            float closestDistanceSqr = float.MaxValue;

            foreach (Collider hit in hits)
            {
                StorageContainer candidate = hit.GetComponentInParent<StorageContainer>();

                if (candidate == null || candidate.IsDestroyed)
                {
                    continue;
                }

                if (filter != null && !filter(candidate))
                {
                    continue;
                }

                float distanceSqr = (candidate.transform.position - origin).sqrMagnitude;

                if (distanceSqr < closestDistanceSqr)
                {
                    closestDistanceSqr = distanceSqr;
                    closest = candidate;
                }
            }

            if (closest == null)
            {
                container = null;
                return false;
            }

            container = closest;
            return true;
        }

        /// <summary>
        /// Own layer mask, not ObstacleLayerMask (2026-08-15, real bug
        /// found via a live /lr.debug.scan Lucas ran right next to both a
        /// bot and a recycler) - confirmed a Recycler's real collider sits
        /// on layer 8 "Deployed" (`Hit 'col' | layer 8 (Deployed) | entity:
        /// Recycler ('recycler_static')`), which ObstacleLayerMask never
        /// included at all. TryFindNearestRecycler's Physics.OverlapSphere
        /// call structurally could never hit a recycler's own collider
        /// with that mask - the search was silently failing for EVERY
        /// recycler, everywhere, regardless of power/safezone status,
        /// since the moment this feature shipped. ObstacleLayerMask itself
        /// is left untouched (used broadly for movement obstacle avoidance
        /// and every other loot-container search, which evidently sit on
        /// layers already covered) - this is a dedicated mask scoped only
        /// to the recycler search.
        /// </summary>
        private static readonly int RecyclerLayerMask = ObstacleLayerMask | LayerMask.GetMask("Deployed");

        /// <summary>
        /// Same physics scan as TryFindNearestLootContainer, specifically
        /// for Recycler (2026-08-15) - confirmed via decompiling
        /// Assembly-CSharp.dll that Recycler : StorageContainer, so a
        /// plain TryFindNearestLootContainer call WOULD technically match
        /// one, but its own itemList is normally empty (nothing to loot
        /// until a bot/player actually feeds it), so it never qualified
        /// under the existing "has items" loot filters anyway - this is a
        /// dedicated search specifically for the recycler-as-a-machine
        /// itself, not for anything sitting inside it.
        /// </summary>
        public bool TryFindNearestRecycler(Vector3 origin, float maxRadius, out Recycler recycler, Func<Recycler, bool> filter = null)
        {
            Collider[] hits = Physics.OverlapSphere(origin, maxRadius, RecyclerLayerMask, QueryTriggerInteraction.Collide);

            Recycler closest = null;
            float closestDistanceSqr = float.MaxValue;

            foreach (Collider hit in hits)
            {
                Recycler candidate = hit.GetComponentInParent<Recycler>();

                if (candidate == null || candidate.IsDestroyed)
                {
                    continue;
                }

                if (filter != null && !filter(candidate))
                {
                    continue;
                }

                float distanceSqr = (candidate.transform.position - origin).sqrMagnitude;

                if (distanceSqr < closestDistanceSqr)
                {
                    closestDistanceSqr = distanceSqr;
                    closest = candidate;
                }
            }

            if (closest == null)
            {
                recycler = null;
                return false;
            }

            recycler = closest;
            return true;
        }

        /// <summary>
        /// Same physics scan as TryFindNearestLootContainer, but collects
        /// EVERY matching container in range instead of just the nearest
        /// one - built for the monument loot-zone auto-detection scan
        /// (LivingRust.MonumentLootZones.cs), which needs the full set to
        /// cluster, not a single nearest result. results is cleared first;
        /// caller owns the list's lifetime (no pooling here, this only ever
        /// runs from an explicit debug command, not a hot per-tick path).
        /// </summary>
        public void GetAllLootContainersInRange(Vector3 origin, float radius, Func<StorageContainer, bool> filter, List<StorageContainer> results)
        {
            results.Clear();

            Collider[] hits = Physics.OverlapSphere(origin, radius, ObstacleLayerMask, QueryTriggerInteraction.Collide);

            foreach (Collider hit in hits)
            {
                StorageContainer candidate = hit.GetComponentInParent<StorageContainer>();

                if (candidate == null || candidate.IsDestroyed || results.Contains(candidate))
                {
                    continue;
                }

                if (filter != null && !filter(candidate))
                {
                    continue;
                }

                results.Add(candidate);
            }
        }

        /// <summary>
        /// Same idea as TryFindNearestLootContainer, but for corpses -
        /// player, scientist/NPC, and animal deaths all leave a real,
        /// lootable LootableCorpse behind, which is a completely separate
        /// class hierarchy from StorageContainer (LootableCorpse : BaseCorpse
        /// : BaseCombatEntity, confirmed via decompiling Assembly-CSharp.dll)
        /// - the container search above never finds these no matter what
        /// filter it's given, since GetComponentInParent&lt;StorageContainer&gt;()
        /// simply never matches one. A kept-separate method rather than
        /// folding corpses into the same search, since a corpse's actual
        /// inventory access (LootableCorpse.containers, an array of up to 3
        /// real ItemContainers) is a different shape from a StorageContainer's
        /// single .inventory field - the caller needs to know which kind of
        /// thing it found.
        /// </summary>
        public bool TryFindNearestLootableCorpse(Vector3 origin, float maxRadius, out LootableCorpse corpse, Func<LootableCorpse, bool> filter = null)
        {
            Collider[] hits = Physics.OverlapSphere(origin, maxRadius, ObstacleLayerMask, QueryTriggerInteraction.Collide);

            LootableCorpse closest = null;
            float closestDistanceSqr = float.MaxValue;

            foreach (Collider hit in hits)
            {
                LootableCorpse candidate = hit.GetComponentInParent<LootableCorpse>();

                if (candidate == null || candidate.IsDestroyed)
                {
                    continue;
                }

                if (filter != null && !filter(candidate))
                {
                    continue;
                }

                float distanceSqr = (candidate.transform.position - origin).sqrMagnitude;

                if (distanceSqr < closestDistanceSqr)
                {
                    closestDistanceSqr = distanceSqr;
                    closest = candidate;
                }
            }

            if (closest == null)
            {
                corpse = null;
                return false;
            }

            corpse = closest;
            return true;
        }

        /// <summary>
        /// Same idea as TryFindNearestLootContainer/TryFindNearestLootableCorpse,
        /// but for dropped bags - what a destroyed/despawned body (fire,
        /// explosives, or a corpse's own timer) converts into. A FOURTH
        /// distinct class hierarchy (DroppedItemContainer : BaseCombatEntity,
        /// confirmed via decompiling Assembly-CSharp.dll - real prefab
        /// "item_drop_backpack"), separate from StorageContainer,
        /// LootableCorpse, AND the plain DroppedItem a single dropped item
        /// (like the bot's own discarded torch) uses. Its real inventory is
        /// a single public ItemContainer field (DroppedItemContainer.inventory),
        /// the same shape as StorageContainer's own .inventory - just not the
        /// same C# type, so it still needs its own search rather than
        /// reusing TryFindNearestLootContainer's type-specific component check.
        /// </summary>
        public bool TryFindNearestDroppedItemContainer(Vector3 origin, float maxRadius, out DroppedItemContainer container, Func<DroppedItemContainer, bool> filter = null)
        {
            Collider[] hits = Physics.OverlapSphere(origin, maxRadius, ObstacleLayerMask, QueryTriggerInteraction.Collide);

            DroppedItemContainer closest = null;
            float closestDistanceSqr = float.MaxValue;

            foreach (Collider hit in hits)
            {
                DroppedItemContainer candidate = hit.GetComponentInParent<DroppedItemContainer>();

                if (candidate == null || candidate.IsDestroyed)
                {
                    continue;
                }

                if (filter != null && !filter(candidate))
                {
                    continue;
                }

                float distanceSqr = (candidate.transform.position - origin).sqrMagnitude;

                if (distanceSqr < closestDistanceSqr)
                {
                    closestDistanceSqr = distanceSqr;
                    closest = candidate;
                }
            }

            if (closest == null)
            {
                container = null;
                return false;
            }

            container = closest;
            return true;
        }

        /// <summary>
        /// Real active-resource-node search (2026-08-25) - a standing,
        /// choppable TreeEntity, not the passive fallen-branch/stone-deposit
        /// CollectibleEntity pickups this project already handles via
        /// PickupCollectibleAndContinue. Reuses ObstacleLayerMask (already
        /// includes the real "Tree" layer, confirmed by its own existing
        /// doc comment) rather than a new dedicated mask. Health() &gt; 0
        /// excludes an already-felled/depleted tree still mid-respawn -
        /// same real ResourceEntity.Health() every other gather check
        /// here uses.
        /// </summary>
        public bool TryFindNearestTreeEntity(Vector3 origin, float maxRadius, out TreeEntity tree, Func<TreeEntity, bool> filter = null)
        {
            Collider[] hits = Physics.OverlapSphere(origin, maxRadius, ObstacleLayerMask, QueryTriggerInteraction.Collide);

            TreeEntity closest = null;
            float closestDistanceSqr = float.MaxValue;

            foreach (Collider hit in hits)
            {
                TreeEntity candidate = hit.GetComponentInParent<TreeEntity>();

                if (candidate == null || candidate.IsDestroyed || candidate.Health() <= 0f)
                {
                    continue;
                }

                if (filter != null && !filter(candidate))
                {
                    continue;
                }

                float distanceSqr = (candidate.transform.position - origin).sqrMagnitude;

                if (distanceSqr < closestDistanceSqr)
                {
                    closestDistanceSqr = distanceSqr;
                    closest = candidate;
                }
            }

            if (closest == null)
            {
                tree = null;
                return false;
            }

            tree = closest;
            return true;
        }

        /// <summary>
        /// Same idea as TryFindNearestTreeEntity, for real ore nodes
        /// (stone/sulfur/metal/HQ metal - OreResourceEntity : StagedResourceEntity
        /// : ResourceEntity, confirmed via decompile - a genuinely separate
        /// real class from TreeEntity, not just a different prefab of the
        /// same one).
        /// </summary>
        public bool TryFindNearestOreResourceEntity(Vector3 origin, float maxRadius, out OreResourceEntity ore, Func<OreResourceEntity, bool> filter = null)
        {
            Collider[] hits = Physics.OverlapSphere(origin, maxRadius, ObstacleLayerMask, QueryTriggerInteraction.Collide);

            OreResourceEntity closest = null;
            float closestDistanceSqr = float.MaxValue;

            foreach (Collider hit in hits)
            {
                OreResourceEntity candidate = hit.GetComponentInParent<OreResourceEntity>();

                if (candidate == null || candidate.IsDestroyed || candidate.Health() <= 0f)
                {
                    continue;
                }

                if (filter != null && !filter(candidate))
                {
                    continue;
                }

                float distanceSqr = (candidate.transform.position - origin).sqrMagnitude;

                if (distanceSqr < closestDistanceSqr)
                {
                    closestDistanceSqr = distanceSqr;
                    closest = candidate;
                }
            }

            if (closest == null)
            {
                ore = null;
                return false;
            }

            ore = closest;
            return true;
        }

        /// <summary>
        /// Real collider layer for a DroppedItem's own worldmodel - confirmed
        /// live via /lr.debug.scan (a dropped rifle.ak and shotgun.m4 both
        /// hit layer 31, "Physics Debris"), NOT present in ObstacleLayerMask
        /// at all. Live report 2026-08-14: the first version of this search
        /// reused ObstacleLayerMask (same as every other search here) and
        /// never found anything - a dropped bag still worked fine since
        /// Ragdoll (its own real layer) IS in that mask, but a standalone
        /// dropped item's own distinct layer genuinely isn't, so it needs
        /// its own dedicated mask instead of the shared one.
        /// </summary>
        private static readonly int DroppedItemLayerMask = LayerMask.GetMask("Physics Debris");

        /// <summary>
        /// Same idea again, but for a genuinely standalone DroppedItem - a
        /// SINGLE loose item lying directly on the ground, not inside any
        /// container (a fifth distinct real class from StorageContainer/
        /// LootableCorpse/DroppedItemContainer). Confirmed live via
        /// /lr.debug.scan: a manually/individually dropped weapon (rifle.ak,
        /// shotgun.m4 both seen) shows up as its own DroppedItem entity,
        /// completely separate from any nearby DroppedItemContainer bag -
        /// this project's own bot-discarded torch (DropUnneededLightSource,
        /// LivingRust.Hooks.cs) uses this exact same real entity type too.
        /// GetComponentInParent works the same way as the container search
        /// above since DroppedItem's own real collider sits on a child
        /// "worldmodel" object, not the entity itself.
        /// </summary>
        public bool TryFindNearestDroppedItem(Vector3 origin, float maxRadius, out DroppedItem droppedItem, Func<DroppedItem, bool> filter = null)
        {
            Collider[] hits = Physics.OverlapSphere(origin, maxRadius, DroppedItemLayerMask, QueryTriggerInteraction.Collide);

            DroppedItem closest = null;
            float closestDistanceSqr = float.MaxValue;

            foreach (Collider hit in hits)
            {
                DroppedItem candidate = hit.GetComponentInParent<DroppedItem>();

                if (candidate == null || candidate.IsDestroyed || candidate.item == null)
                {
                    continue;
                }

                if (filter != null && !filter(candidate))
                {
                    continue;
                }

                float distanceSqr = (candidate.transform.position - origin).sqrMagnitude;

                if (distanceSqr < closestDistanceSqr)
                {
                    closestDistanceSqr = distanceSqr;
                    closest = candidate;
                }
            }

            if (closest == null)
            {
                droppedItem = null;
                return false;
            }

            droppedItem = closest;
            return true;
        }

        /// <summary>
        /// Real gatherable-plant/surface-deposit/loose-wood class (hemp,
        /// corn, pumpkin, mushroom, small stone/metal/sulfur surface
        /// deposits, fallen branches - all the same CollectibleEntity type,
        /// confirmed via decompile) - deliberately NOT a Physics.OverlapSphere
        /// search like every other loot source above. Confirmed via
        /// decompile: CollectibleEntity.PreProcess strips its own Collider
        /// component entirely server-side ("if (serverside) RemoveComponent
        /// GetComponent&lt;Collider&gt;()"), so a physics-based sphere query
        /// would never find one no matter the layer mask. BaseEntity.Query.
        /// Server is Rust's own real spatial index instead - maintained
        /// automatically for every entity regardless of whether it has a
        /// live collider, so it's the correct tool here specifically (not
        /// swapped in for the other searches above, which all work fine
        /// with real colliders already).
        /// </summary>
        public bool TryFindNearestCollectible(Vector3 origin, float maxRadius, out CollectibleEntity collectible, Func<CollectibleEntity, bool> filter = null)
        {
            List<CollectibleEntity> results = Pool.Get<List<CollectibleEntity>>();
            BaseEntity.Query.Server.GetInSphere(origin, maxRadius, results);

            CollectibleEntity closest = null;
            float closestDistanceSqr = float.MaxValue;

            foreach (CollectibleEntity candidate in results)
            {
                if (candidate == null || candidate.IsDestroyed)
                {
                    continue;
                }

                if (filter != null && !filter(candidate))
                {
                    continue;
                }

                float distanceSqr = (candidate.transform.position - origin).sqrMagnitude;

                if (distanceSqr < closestDistanceSqr)
                {
                    closestDistanceSqr = distanceSqr;
                    closest = candidate;
                }
            }

            Pool.FreeUnmanaged(ref results);

            if (closest == null)
            {
                collectible = null;
                return false;
            }

            collectible = closest;
            return true;
        }

        /// <summary>
        /// Finds the nearest point on any real road spline within
        /// maxDistance - used by the loot task's road-following search
        /// escalation (LivingRust.Looting.cs) once a plain radius scan
        /// comes up empty, rather than a "no loot nearby, give up"
        /// dead end. Real Rust roads are procedurally generated splines
        /// (TerrainMeta.Path.Roads -&gt; PathList.Path, a PathInterpolator
        /// with a public Points array and a real Length), a completely
        /// different, geometry-based way of answering "is there a road
        /// near me" than any physic-material/terrain-texture reading
        /// this project doesn't have at all. TerrainMeta.Path.Roads is
        /// marked `internal` in Assembly-CSharp, but is confirmed
        /// accessible from this plugin via a live build test - Carbon
        /// evidently grants that access.
        ///
        /// distanceAlongRoad is an approximation (nearest Points[] INDEX
        /// converted to a distance via the same linear index/Length ratio
        /// PathInterpolator.GetPoint(float) itself uses internally), not
        /// exact arc-length - accurate enough for "walk roughly N more
        /// metres along this road," not precise enough for anything that
        /// needed exact positioning.
        /// </summary>
        public bool TryFindNearestRoadPoint(Vector3 origin, float maxDistance, out Vector3 nearestPoint, out PathInterpolator road, out float distanceAlongRoad)
        {
            nearestPoint = Vector3.zero;
            road = null;
            distanceAlongRoad = 0f;

            if (TerrainMeta.Path == null || TerrainMeta.Path.Roads == null)
            {
                return false;
            }

            float closestDistanceSqr = maxDistance * maxDistance;
            bool found = false;

            foreach (PathList pathList in TerrainMeta.Path.Roads)
            {
                PathInterpolator path = pathList?.Path;

                if (path == null || path.Points == null || path.Points.Length < 2)
                {
                    continue;
                }

                for (int i = 0; i < path.Points.Length; i++)
                {
                    float distanceSqr = (path.Points[i] - origin).sqrMagnitude;

                    if (distanceSqr < closestDistanceSqr)
                    {
                        closestDistanceSqr = distanceSqr;
                        nearestPoint = path.Points[i];
                        road = path;
                        distanceAlongRoad = path.Length * i / Mathf.Max(1, path.Points.Length - 1);
                        found = true;
                    }
                }
            }

            return found;
        }
    }
}
