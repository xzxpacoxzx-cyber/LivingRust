using LivingRust.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using LivingRust.Models;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Post-base progression and base hygiene (2026-10-03, Lucas's live-server audit after 20+ hours
/// on the hosted server): stack-size repair, torch purge, open-door sweeper, the in-base
/// workshop (metal tools, clothing/armor, gunpowder, ammo, firearms, free scrap-gated research),
/// the sheet-metal base upgrade, base sleeping-bag placement, death-site revisit rules, the
/// ghost-route-only monument policy (Launch Site) and the "stop farming wood/stone once based"
/// resource policy.
/// </summary>
public partial class LivingRust
{
    // ============================================================
    // Stack-size safety
    // ============================================================

    /// <summary>
    /// ItemManager.Create* makes ONE Item of the requested amount with no splitting at the game's
    /// stack limit, and MoveToContainer into an empty slot doesn't split it either - which is how
    /// a single 6,639-stone "stack" ended up in a base box (tier-upgrade pity grant). Everything
    /// that creates bulk items should go through this.
    /// </summary>
    private static List<Item> CreateStackSizedItems(ItemDefinition def, int amount)
    {
        List<Item> items = new();
        int stack = Mathf.Max(1, def.stackable);
        int remaining = amount;

        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, stack);
            Item item = ItemManager.CreateByItemID(def.itemid, chunk);

            if (item == null)
            {
                break;
            }

            items.Add(item);
            remaining -= chunk;
        }

        return items;
    }

    /// <summary>
    /// Splits any stack in container that exceeds its item's real stack limit back into legal
    /// stacks (repairs boxes already holding impossible stacks). Overflow goes to fallbacks, then
    /// the ground as a last resort. Returns how many splits were made.
    /// </summary>
    private int SplitOversizedStacks(ItemContainer container, Vector3 overflowDropPosition, IList<ItemContainer> fallbacks = null)
    {
        if (container == null)
        {
            return 0;
        }

        int splits = 0;

        foreach (Item item in new List<Item>(container.itemList))
        {
            int max = item.info.stackable;

            if (max <= 0 || item.amount <= max)
            {
                continue;
            }

            int guard = 0;

            while (item.amount > max && guard++ < 500)
            {
                Item split = item.SplitItem(max);

                if (split == null)
                {
                    break;
                }

                bool placed = split.MoveToContainer(container);

                if (!placed && fallbacks != null)
                {
                    foreach (ItemContainer fallback in fallbacks)
                    {
                        if (fallback != null && fallback != container && split.MoveToContainer(fallback))
                        {
                            placed = true;
                            break;
                        }
                    }
                }

                if (!placed)
                {
                    split.Drop(overflowDropPosition + Vector3.up, Vector3.zero);
                }

                splits++;
            }
        }

        return splits;
    }

    private static readonly string[] TorchShortnames = { "torch", "torch.torch.skull", "divertorch" };

    private static bool IsTorchItem(Item item)
    {
        return item?.info != null && Array.IndexOf(TorchShortnames, item.info.shortname) >= 0;
    }

    private static int PurgeTorches(ItemContainer container)
    {
        if (container == null)
        {
            return 0;
        }

        int removed = 0;

        foreach (Item item in new List<Item>(container.itemList))
        {
            if (IsTorchItem(item))
            {
                item.Remove();
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Run at the start of every base trip: repairs impossible stacks in the survivor's boxes,
    /// cupboard and inventory, and destroys torches (the respawn kit used to hand one over every
    /// life and the trip home banked it - dozens of torches per base).
    /// </summary>
    private void SanitizeBaseStorage(Survivor survivor)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;

        if (home == null || npc == null || npc.IsDestroyed)
        {
            return;
        }

        List<StorageContainer> boxes = FindOwnedStorageBoxesNear(npc, home.Position, HomeStorageSearchRadius);
        BuildingPrivlidge cupboard = FindOwnedCupboard(home);

        List<ItemContainer> all = boxes.Where(b => b != null && !b.IsDestroyed && b.inventory != null).Select(b => b.inventory).ToList();
        List<ItemContainer> overflowTargets = new(all);

        int torches = 0;
        int splits = 0;

        foreach (ItemContainer container in all)
        {
            torches += PurgeTorches(container);
            splits += SplitOversizedStacks(container, home.Position, overflowTargets);
        }

        if (cupboard != null && cupboard.inventory != null)
        {
            splits += SplitOversizedStacks(cupboard.inventory, home.Position, overflowTargets);
        }

        torches += PurgeTorches(npc.inventory.containerMain) + PurgeTorches(npc.inventory.containerBelt);
        splits += SplitOversizedStacks(npc.inventory.containerMain, npc.transform.position, overflowTargets);
        splits += SplitOversizedStacks(npc.inventory.containerBelt, npc.transform.position, overflowTargets);

        if (torches > 0 || splits > 0)
        {
            Puts($"home-storage: '{survivor.Character.Alias}' base cleanup - destroyed {torches} torch(es), split {splits} oversized stack(s).");
        }
    }

    // ============================================================
    // Open-door sweeper
    // ============================================================

    private const float DoorSweepIntervalSeconds = 30f;
    private const float DoorSweepBaseRadius = 45f;
    private const float DoorSweepPersonClearance = 3.5f;

    private readonly Dictionary<Guid, float> _baseTripUntil = new();
    private Timer _doorSweepTimer;

    private void StartDoorSweeper()
    {
        _doorSweepTimer?.Destroy();
        _doorSweepTimer = timer.Every(DoorSweepIntervalSeconds, RunDoorSweep);
    }

    private void StopDoorSweeper()
    {
        _doorSweepTimer?.Destroy();
        _doorSweepTimer = null;
    }

    /// <summary>
    /// Doors only ever got closed by the callback at the end of an uninterrupted enter/exit
    /// route. Anything that cancelled the route mid-way (a new task, combat, the watchdog, death)
    /// left the door standing open, so most bases ended up wide open. This sweeps every survivor's
    /// own doors on a timer and closes any that are open while nobody is using them.
    /// </summary>
    private void RunDoorSweep()
    {
        if (_engine == null)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        Dictionary<ulong, Survivor> homeOwners = new();

        foreach (Survivor survivor in _engine.SurvivorManager.GetAll())
        {
            if (survivor.Character.Home == null)
            {
                continue;
            }

            Guid id = survivor.Character.Id;

            if (_activeHomeDoorCrossings.Contains(id) || (_baseTripUntil.TryGetValue(id, out float until) && until > now) || IsInWorkshop(id))
            {
                continue;
            }

            homeOwners[survivor.Character.BotId] = survivor;
        }

        if (homeOwners.Count == 0)
        {
            return;
        }

        int closed = 0;

        foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
        {
            if (entity is not Door door || door.IsDestroyed || !door.IsOpen() || !homeOwners.TryGetValue(door.OwnerID, out Survivor owner))
            {
                continue;
            }

            HomeBase home = owner.Character.Home;

            if (Vector3.Distance(door.transform.position, home.Position) > DoorSweepBaseRadius)
            {
                continue;
            }

            BasePlayer ownerNpc = owner.Player;

            if (ownerNpc != null && !ownerNpc.IsDestroyed && Vector3.Distance(ownerNpc.transform.position, door.transform.position) < DoorSweepPersonClearance)
            {
                continue;
            }

            door.SetOpen(false);
            door.SendNetworkUpdate();
            closed++;
        }

        if (closed > 0)
        {
            VerbosePuts($"door-sweep: closed {closed} open base door(s).");
        }
    }

    // ============================================================
    // Workshop exemption (the stall watchdog must not rescue a bot standing at its workbench)
    // ============================================================

    private readonly Dictionary<Guid, float> _workshopUntil = new();

    private bool IsInWorkshop(Guid characterId)
    {
        return _workshopUntil.TryGetValue(characterId, out float until) && until > Time.realtimeSinceStartup;
    }

    // ============================================================
    // Resource-farming policy
    // ============================================================

    private static bool YieldsContain(IEnumerable<ItemAmount> yields, params string[] shortnames)
    {
        return yields != null && yields.Any(y => y?.itemDef != null && Array.IndexOf(shortnames, y.itemDef.shortname) >= 0);
    }

    /// <summary>
    /// Once a survivor has a base, bulk wood/stone farming (trees, stone nodes, wood/stone piles)
    /// is dropped entirely - the bases were swimming in both while having no scrap, components or
    /// guns. Metal/sulfur ore stays wanted, and everything else (hemp, berries, ...) is unchanged.
    /// Pre-base behaviour is untouched (a base needs the wood/stone).
    /// </summary>
    private bool IsFarmedResourceWanted(Survivor survivor, IEnumerable<ItemAmount> yields)
    {
        if (survivor?.Character.Home == null || yields == null)
        {
            return true;
        }

        if (YieldsContain(yields, SulfurOreShortname, MetalOreShortname, "hq.metal.ore"))
        {
            return true;
        }

        return !YieldsContain(yields, WoodShortname, StoneShortname);
    }

    // ============================================================
    // Recycling extras
    // ============================================================

    private static readonly HashSet<string> RecycleExtraShortnames = new()
    {
        "electric.random.switch", "electric.pressurepad", "hopper", "electric.flasherlight",
        "targeting.computer", "electric.doorcontroller", "cctv.camera", "fireplace.stone",
        "waterpump", "floor.grill", "electric.heater", "electrical.branch", "electric.xorswitch",
        "fluid.combiner", "electric.sprinkler", "electric.solarpanel.large", "storageadaptor",
        "tool.binoculars", "electrical.combiner", "electric.blocker", "electric.rf.receiver",
        "target.reactive", "electric.splitter",
    };

    private static bool IsExplicitRecycleItem(Item item)
    {
        return item?.info != null && RecycleExtraShortnames.Contains(item.info.shortname);
    }

    /// <summary>
    /// Brings the explicit junk sitting in base boxes (banked there by the old deposit rule) back
    /// out so the next recycler trip can process it. Capped so it can't swamp the inventory.
    /// </summary>
    private void WithdrawRecycleJunkFromBoxes(BasePlayer npc, List<StorageContainer> boxes, int maxStacks = 6)
    {
        int moved = 0;

        foreach (StorageContainer box in boxes)
        {
            if (box == null || box.IsDestroyed || box.inventory == null)
            {
                continue;
            }

            foreach (Item item in new List<Item>(box.inventory.itemList))
            {
                if (moved >= maxStacks)
                {
                    return;
                }

                if (IsExplicitRecycleItem(item) && item.MoveToContainer(npc.inventory.containerMain))
                {
                    moved++;
                }
            }
        }
    }

    // ============================================================
    // Deposit filter wrapper
    // ============================================================

    private bool ShouldDepositAtBaseFor(Survivor survivor, Item item)
    {
        BasePlayer npc = survivor.Player;
        HomeBase home = survivor.Character.Home;

        if (IsTorchItem(item))
        {
            return false;
        }

        // A bag being carried gets placed at the base before it's ever banked.
        if (item.info.shortname == SleepingBagShortname)
        {
            return home != null && npc != null && HasSleepingBagNearHome(npc, home);
        }

        // Explicit junk rides along to a recycler instead of piling up in boxes (capped so a
        // survivor with no reachable recycler doesn't fill up with it).
        if (IsExplicitRecycleItem(item) && npc != null
            && npc.inventory.containerMain.itemList.Concat(npc.inventory.containerBelt.itemList).Count(IsExplicitRecycleItem) <= 8)
        {
            return false;
        }

        return ShouldDepositAtBase(item, npc);
    }

    // ============================================================
    // Free blueprints
    // ============================================================

    private const int ScrapPerFreeResearch = 150;
    private const string ScrapShortname = "scrap";

    private static int CountInBoxes(List<StorageContainer> boxes, int itemId)
    {
        int total = 0;

        foreach (StorageContainer box in boxes)
        {
            if (box == null || box.IsDestroyed || box.inventory == null)
            {
                continue;
            }

            foreach (Item item in box.inventory.itemList)
            {
                if (item.info.itemid == itemId)
                {
                    total += item.amount;
                }
            }
        }

        return total;
    }

    private static int CountOwned(BasePlayer npc, List<StorageContainer> boxes, ItemDefinition def)
    {
        return npc.inventory.GetAmount(def.itemid) + CountInBoxes(boxes, def.itemid);
    }

    private void FreeUnlock(BasePlayer npc, ItemDefinition def, string why, string alias)
    {
        if (def == null || npc?.blueprints == null || npc.blueprints.IsUnlocked(def))
        {
            return;
        }

        npc.blueprints.Unlock(def);
        Puts($"research: '{alias}' learned the '{def.shortname}' blueprint for free ({why}).");
    }

    /// <summary>
    /// Every full 150 scrap sitting in a survivor's base storage entitles it to ONE free
    /// firearm blueprint (no table, no scrap spent - the hoard just has to exist). Entitlements
    /// already used are tracked on the Character so spending/recycling scrap later can't hand the
    /// same one out twice. Picks the best-scoring firearm its current workbench can build.
    /// </summary>
    private void GrantScrapResearch(Survivor survivor, BasePlayer npc, List<StorageContainer> boxes, int workbenchLevel)
    {
        ItemDefinition scrapDef = ItemManager.FindItemDefinition(ScrapShortname);

        if (scrapDef == null || npc.blueprints == null)
        {
            return;
        }

        int entitled = CountOwned(npc, boxes, scrapDef) / ScrapPerFreeResearch;

        while (survivor.Character.FreeResearchesUsed < entitled)
        {
            string target = null;
            int bestScore = int.MinValue;

            // The weapon the survivor decided it wants at its last assessment comes first.
            string goal = survivor.Character.CraftGoal;
            ItemDefinition goalDef = string.IsNullOrEmpty(goal) ? null : ItemManager.FindItemDefinition(goal);

            if (goalDef?.Blueprint != null && IsWeaponGoal(goal) && !npc.blueprints.IsUnlocked(goalDef)
                && goalDef.Blueprint.userCraftable && goalDef.Blueprint.workbenchLevelRequired <= workbenchLevel)
            {
                target = goal;
            }

            foreach (string shortname in target != null ? Array.Empty<string>() : WeaponGoalShortnames)
            {
                ItemDefinition def = ItemManager.FindItemDefinition(shortname);
                ItemBlueprint bp = def?.Blueprint;

                if (bp == null || !bp.userCraftable || npc.blueprints.IsUnlocked(def) || bp.workbenchLevelRequired > workbenchLevel)
                {
                    continue;
                }

                int score = WeaponGearScore.TryGetValue(shortname, out int s) ? s : 0;

                if (score > bestScore)
                {
                    bestScore = score;
                    target = shortname;
                }
            }

            if (target == null)
            {
                return;
            }

            npc.blueprints.Unlock(ItemManager.FindItemDefinition(target));
            survivor.Character.FreeResearchesUsed++;

            Puts($"research: '{survivor.Character.Alias}' has {CountOwned(npc, boxes, scrapDef)} scrap banked - free research #{survivor.Character.FreeResearchesUsed} unlocked the '{target}' blueprint (workbench level {ItemManager.FindItemDefinition(target).Blueprint.workbenchLevelRequired}).");
        }
    }

    // ============================================================
    // Workshop
    // ============================================================

    private sealed class WorkshopJob
    {
        public string Shortname = "";
        public int Batches = 1;
        public bool WithdrawOnly;
        public string Reason = "";
    }

    private const int WorkshopMaxJobsPerVisit = 12;
    private const float WorkshopCraftTimeScale = 0.25f;
    private const float WorkshopMaxSecondsPerCraft = 8f;
    private const float WorkshopStallExemptionSeconds = 300f;

    private static readonly (string Group, string[] Candidates)[] WorkshopClothingGroups =
    {
        ("torso", new[] { "metal.plate.torso", "roadsign.jacket", "wood.armor.jacket", "hoodie", "tshirt", "burlap.shirt" }),
        ("legs", new[] { "roadsign.kilt", "wood.armor.pants", "pants", "burlap.trousers" }),
        ("head", new[] { "metal.facemask", "bucket.helmet", "wood.armor.helmet", "hat.wolf", "burlap.headwrap" }),
        ("feet", new[] { "shoes.boots", "burlap.shoes" }),
        ("hands", new[] { "roadsign.gloves", "burlap.gloves" }),
    };

    private const int AmmoWorkshopStockTarget = 200;
    private const int AmmoWorkshopRefillBelow = 120;
    private const int GunpowderStockCap = 1000;
    private const int GunpowderMaxBatchesPerJob = 40;

    // Missing recipe components for the best unlocked-but-unbuildable firearm, logged so it's
    // visible what each bot is waiting on (and which pickups the base is saving up for).
    private readonly Dictionary<Guid, string> _lastWantedRecipeLog = new();

    private BaseEntity FindOwnedWorkbench(BasePlayer npc, HomeBase home, out int level)
    {
        BaseEntity best = null;
        level = 0;

        foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities)
        {
            if (networkable is not BaseEntity entity || entity.IsDestroyed || entity.OwnerID != npc.userID)
            {
                continue;
            }

            string prefab = entity.ShortPrefabName ?? string.Empty;

            if (!prefab.StartsWith("workbench", StringComparison.Ordinal) || !prefab.EndsWith(".deployed", StringComparison.Ordinal) || prefab.Length < 10)
            {
                continue;
            }

            if (Vector3.Distance(entity.transform.position, home.Position) > HomeStorageSearchRadius || !int.TryParse(prefab.Substring(9, 1), out int benchLevel))
            {
                continue;
            }

            if (benchLevel > level)
            {
                level = benchLevel;
                best = entity;
            }
        }

        return best;
    }

    private static int MaxAffordableBatches(BasePlayer npc, List<StorageContainer> boxes, ItemBlueprint bp, int cap)
    {
        int best = cap;

        foreach (ItemAmount ingredient in bp.GetIngredients())
        {
            if (ingredient?.itemDef == null)
            {
                continue;
            }

            int per = Mathf.Max(1, (int)ingredient.amount);
            best = Math.Min(best, CountOwned(npc, boxes, ingredient.itemDef) / per);

            if (best <= 0)
            {
                return 0;
            }
        }

        return best;
    }

    private static bool CanWorkshopCraft(BasePlayer npc, List<StorageContainer> boxes, ItemDefinition def, int batches, int workbenchLevel)
    {
        ItemBlueprint bp = def?.Blueprint;
        return bp != null && bp.userCraftable && bp.workbenchLevelRequired <= workbenchLevel && MaxAffordableBatches(npc, boxes, bp, batches) >= batches;
    }

    private static bool HasToolBeyondStone(BasePlayer npc, string[] family)
    {
        return npc.inventory.containerMain.itemList.Concat(npc.inventory.containerBelt.itemList)
            .Any(i => Array.IndexOf(family, i.info.shortname) >= 0
                && i.info.shortname != StoneHatchetShortname && i.info.shortname != StonePickaxeShortname);
    }

    /// <summary>
    /// Clothing/armor pieces worth making right now, best first within each slot: anything in the
    /// slot's wish list that is ranked above what is worn there AND not already covered by a worn
    /// piece of equal or better armor tier. Shared by the workshop planner and the assessment.
    /// </summary>
    private List<(string Group, string Shortname)> GetUsefulClothingCandidates(BasePlayer npc, int wbLevel)
    {
        List<(string, string)> result = new();
        List<Item> worn = npc.inventory.containerWear.itemList;

        foreach ((string group, string[] candidates) in WorkshopClothingGroups)
        {
            int bestWornRank = int.MaxValue;

            foreach (Item w in worn)
            {
                int rank = Array.IndexOf(candidates, w.info.shortname);

                if (rank >= 0 && rank < bestWornRank)
                {
                    bestWornRank = rank;
                }
            }

            for (int i = 0; i < candidates.Length && i < bestWornRank; i++)
            {
                string shortname = candidates[i];
                ItemDefinition def = ItemManager.FindItemDefinition(shortname);
                ItemModWearable wearable = def?.GetComponent<ItemModWearable>();

                if (def?.Blueprint == null || wearable == null || def.Blueprint.workbenchLevelRequired > wbLevel)
                {
                    continue;
                }

                ArmorTier candidateTier = GetArmorTier(shortname);

                bool coveredAtEqualOrBetter = worn.Any(w =>
                {
                    ItemModWearable wornWearable = w.info.GetComponent<ItemModWearable>();
                    return wornWearable != null && !wearable.CanExistWith(wornWearable) && GetArmorTier(w.info.shortname) >= candidateTier;
                });

                if (!coveredAtEqualOrBetter)
                {
                    result.Add((group, shortname));
                }
            }
        }

        return result;
    }

    private WorkshopJob PlanNextWorkshopJob(Survivor survivor, BasePlayer npc, List<StorageContainer> boxes, int wbLevel, HashSet<string> failed)
    {
        // 1. Metal hatchet + pickaxe: a respawned survivor should never walk back out with a rock.
        foreach ((string tool, string[] family) in new[] { ("hatchet", HatchetFamily), ("pickaxe", PickaxeFamily) })
        {
            ItemDefinition def = ItemManager.FindItemDefinition(tool);

            if (def == null || failed.Contains(tool) || HasToolBeyondStone(npc, family))
            {
                continue;
            }

            if (CountInBoxes(boxes, def.itemid) > 0)
            {
                return new WorkshopJob { Shortname = tool, WithdrawOnly = true, Reason = "metal tool from storage" };
            }

            if (CanWorkshopCraft(npc, boxes, def, 1, wbLevel))
            {
                return new WorkshopJob { Shortname = tool, Reason = "metal tool" };
            }
        }

        // 1b. The item this survivor decided it wants next at its last coffer assessment.
        string goal = survivor.Character.CraftGoal;

        if (!string.IsNullOrEmpty(goal))
        {
            ItemDefinition goalDef = ItemManager.FindItemDefinition(goal);

            if (goalDef == null || IsCraftGoalSatisfied(npc, boxes, goalDef))
            {
                if (goalDef != null)
                {
                    Puts($"assess: '{survivor.Character.Alias}' has its craft goal '{goal}' now - it will pick a new one at its next assessment.");
                }

                survivor.Character.CraftGoal = null;
            }
            else if (!failed.Contains(goal) && goalDef.category != ItemCategory.Ammunition
                && (npc.blueprints.IsUnlocked(goalDef) || !IsWeaponGoal(goal))
                && CanWorkshopCraft(npc, boxes, goalDef, 1, wbLevel))
            {
                return new WorkshopJob { Shortname = goal, Reason = "assessed craft goal" };
            }
        }

        // 2. Clothing / armor: heads out wearing something in every slot it can afford to cover.
        foreach ((string group, string shortname) in GetUsefulClothingCandidates(npc, wbLevel))
        {
            ItemDefinition def = ItemManager.FindItemDefinition(shortname);

            if (def == null || failed.Contains(shortname) || !CanWorkshopCraft(npc, boxes, def, 1, wbLevel))
            {
                continue;
            }

            return new WorkshopJob { Shortname = shortname, Reason = $"{group} clothing" };
        }

        // 3. Gunpowder: sulfur + charcoal sitting around is wasted progression.
        ItemDefinition gunpowderDef = ItemManager.FindItemDefinition("gunpowder");
        ItemDefinition sulfurDef = ItemManager.FindItemDefinition("sulfur");
        ItemDefinition charcoalDef = ItemManager.FindItemDefinition("charcoal");

        if (gunpowderDef?.Blueprint != null && sulfurDef != null && charcoalDef != null && !failed.Contains("gunpowder"))
        {
            int threshold = survivor.Character.KnownAmmoTypes != null && survivor.Character.KnownAmmoTypes.Count > 0 ? 100 : 300;

            if (CountOwned(npc, boxes, gunpowderDef) < GunpowderStockCap
                && CountOwned(npc, boxes, sulfurDef) >= threshold
                && CountOwned(npc, boxes, charcoalDef) >= threshold
                && gunpowderDef.Blueprint.workbenchLevelRequired <= wbLevel)
            {
                int batches = MaxAffordableBatches(npc, boxes, gunpowderDef.Blueprint, GunpowderMaxBatchesPerJob);

                if (batches >= 1)
                {
                    return new WorkshopJob { Shortname = "gunpowder", Batches = batches, Reason = "gunpowder from banked sulfur/charcoal" };
                }
            }
        }

        // 4. Ammunition for every ammo type the survivor's guns use.
        TryUnlockAmmoTypesFromWeapons(survivor, npc);

        foreach (string ammoShortname in survivor.Character.KnownAmmoTypes ?? new List<string>())
        {
            ItemDefinition ammoDef = ItemManager.FindItemDefinition(ammoShortname);
            ItemBlueprint bp = ammoDef?.Blueprint;

            if (bp == null || failed.Contains(ammoShortname) || npc.inventory.GetAmount(ammoDef.itemid) >= AmmoWorkshopRefillBelow || bp.workbenchLevelRequired > wbLevel)
            {
                continue;
            }

            int perBatch = Mathf.Max(1, (int)bp.amountToCreate);
            int wanted = Mathf.CeilToInt((AmmoWorkshopStockTarget - npc.inventory.GetAmount(ammoDef.itemid)) / (float)perBatch);
            int batches = MaxAffordableBatches(npc, boxes, bp, Math.Min(wanted, 40));

            if (batches >= 1)
            {
                return new WorkshopJob { Shortname = ammoShortname, Batches = batches, Reason = "ammo" };
            }
        }

        // 5. A firearm, if it has none: best unlocked blueprint its bench and stores can build.
        bool ownsFirearm = CountRealFirearms(npc) > 0
            || boxes.Any(b => b != null && !b.IsDestroyed && b.inventory != null
                && b.inventory.itemList.Any(i => WeaponAmmoType.ContainsKey(i.info.shortname)));

        if (!ownsFirearm)
        {
            string wantedWeapon = null;
            int wantedScore = int.MinValue;

            foreach (string shortname in WeaponAmmoType.Keys.OrderByDescending(k => WeaponGearScore.TryGetValue(k, out int sc) ? sc : 0))
            {
                ItemDefinition def = ItemManager.FindItemDefinition(shortname);
                ItemBlueprint bp = def?.Blueprint;

                if (bp == null || !bp.userCraftable || failed.Contains(shortname) || !npc.blueprints.IsUnlocked(def) || bp.workbenchLevelRequired > wbLevel)
                {
                    continue;
                }

                if (CanWorkshopCraft(npc, boxes, def, 1, wbLevel))
                {
                    return new WorkshopJob { Shortname = shortname, Reason = "firearm from a researched blueprint" };
                }

                int score = WeaponGearScore.TryGetValue(shortname, out int s) ? s : 0;

                if (wantedWeapon == null || score > wantedScore)
                {
                    wantedWeapon = shortname;
                    wantedScore = score;
                }
            }

            if (wantedWeapon != null)
            {
                LogWantedRecipe(survivor, npc, boxes, ItemManager.FindItemDefinition(wantedWeapon));
            }
        }

        return null;
    }

    private void LogWantedRecipe(Survivor survivor, BasePlayer npc, List<StorageContainer> boxes, ItemDefinition def)
    {
        List<string> missing = new();

        foreach (ItemAmount ingredient in def.Blueprint.GetIngredients())
        {
            if (ingredient?.itemDef == null)
            {
                continue;
            }

            int have = CountOwned(npc, boxes, ingredient.itemDef);

            if (have < (int)ingredient.amount)
            {
                missing.Add($"{(int)ingredient.amount - have}x {ingredient.itemDef.shortname}");
            }
        }

        string summary = $"{def.shortname}: needs {string.Join(", ", missing)}";

        if (_lastWantedRecipeLog.TryGetValue(survivor.Character.Id, out string previous) && previous == summary)
        {
            return;
        }

        _lastWantedRecipeLog[survivor.Character.Id] = summary;
        Puts($"workshop: '{survivor.Character.Alias}' has the '{def.shortname}' blueprint but can't build it yet - still needs {string.Join(", ", missing)} (it will keep banking components that match).");
    }

    /// <summary>
    /// The in-base workshop step of a base trip: stands at the workbench and crafts what the
    /// survivor is missing from what its own storage holds, applying free research where the
    /// spec allows it. Ends with the sheet-metal base upgrade check. Always calls onComplete.
    /// </summary>
    private void TryRunBaseWorkshop(Survivor survivor, Action onComplete)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer startNpc = survivor.Player;
        Guid characterId = survivor.Character.Id;

        if (home == null || startNpc == null || startNpc.IsDestroyed)
        {
            onComplete?.Invoke();
            return;
        }

        List<StorageContainer> boxes = FindOwnedStorageBoxesNear(startNpc, home.Position, HomeStorageSearchRadius);
        BaseEntity workbench = FindOwnedWorkbench(startNpc, home, out int wbLevel);

        _workshopUntil[characterId] = Time.realtimeSinceStartup + WorkshopStallExemptionSeconds;

        WithdrawRecycleJunkFromBoxes(startNpc, boxes);
        GrantScrapResearch(survivor, startNpc, boxes, wbLevel);

        // Wears anything already held before deciding what's still missing.
        EvaluateAndUpgradeArmor(survivor);

        HashSet<string> failed = new();
        bool atBench = false;
        int jobsDone = 0;
        bool finished = false;

        void Finish()
        {
            if (finished)
            {
                return;
            }

            finished = true;

            BasePlayer endNpc = survivor.Player;

            if (endNpc != null && !endNpc.IsDestroyed && survivor.Character.State != CharacterState.Dead)
            {
                RunLootHookSafely(survivor, nameof(EvaluateAndUpgradeArmor), () => EvaluateAndUpgradeArmor(survivor));
                RunLootHookSafely(survivor, nameof(EquipBestMeleeTool), () => EquipBestMeleeTool(survivor));
                RunLootHookSafely(survivor, nameof(EquipBestWeaponForDisplay), () => EquipBestWeaponForDisplay(survivor));
            }

            TryUpgradeBaseToMetal(survivor, boxes, () =>
            {
                _workshopUntil.Remove(characterId);
                onComplete?.Invoke();
            });
        }

        void RunNext()
        {
            BasePlayer npc = survivor.Player;

            if (npc == null || npc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
            {
                _workshopUntil.Remove(characterId);
                return;
            }

            if (jobsDone >= WorkshopMaxJobsPerVisit)
            {
                Finish();
                return;
            }

            WorkshopJob job = PlanNextWorkshopJob(survivor, npc, boxes, wbLevel, failed);

            if (job == null)
            {
                Finish();
                return;
            }

            jobsDone++;

            void Execute()
            {
                atBench = true;
                ExecuteWorkshopJob(survivor, job, boxes, wbLevel, failed, RunNext);
            }

            // Craft standing next to the workbench, inside the base - not from wherever the deposit trip left it.
            if (!atBench && workbench != null && !workbench.IsDestroyed && !job.WithdrawOnly)
            {
                Vector3 away = npc.transform.position - workbench.transform.position;
                away.y = 0f;

                if (away.sqrMagnitude < 0.01f)
                {
                    away = npc.transform.forward;
                }

                Vector3 benchPoint = workbench.transform.position + away.normalized * 1.3f;
                benchPoint.y = workbench.transform.position.y;

                StartPhasingToDestination(survivor, benchPoint, onArrived: Execute, onFailed: Execute);
                return;
            }

            Execute();
        }

        RunNext();
    }

    private void ExecuteWorkshopJob(Survivor survivor, WorkshopJob job, List<StorageContainer> boxes, int wbLevel, HashSet<string> failed, Action next)
    {
        BasePlayer npc = survivor.Player;
        ItemDefinition def = ItemManager.FindItemDefinition(job.Shortname);
        ItemBlueprint bp = def?.Blueprint;

        if (npc == null || npc.IsDestroyed || def == null)
        {
            next();
            return;
        }

        if (job.WithdrawOnly)
        {
            if (WithdrawUpToAmount(boxes, job.Shortname, 1, npc.inventory.containerMain) < 1)
            {
                failed.Add(job.Shortname);
            }
            else
            {
                Puts($"workshop: '{survivor.Character.Alias}' took a '{job.Shortname}' out of storage ({job.Reason}).");
            }

            next();
            return;
        }

        if (bp == null)
        {
            failed.Add(job.Shortname);
            next();
            return;
        }

        // Free research where the spec allows it: metal tools, basic clothing, gunpowder and ammo for
        // guns it actually owns. Firearms are never unlocked here - those cost scrap-gated research.
        if (!npc.blueprints.IsUnlocked(def) && !IsWeaponGoal(job.Shortname))
        {
            FreeUnlock(npc, def, job.Reason, survivor.Character.Alias);
        }

        if (!npc.blueprints.IsUnlocked(def))
        {
            failed.Add(job.Shortname);
            next();
            return;
        }

        List<ItemAmount> ingredients = bp.GetIngredients();

        foreach (ItemAmount ingredient in ingredients)
        {
            if (ingredient?.itemDef == null)
            {
                continue;
            }

            int need = (int)ingredient.amount * job.Batches;
            int have = npc.inventory.GetAmount(ingredient.itemid);

            if (have < need)
            {
                WithdrawUpToAmount(boxes, ingredient.itemDef.shortname, need - have, npc.inventory.containerMain);
            }
        }

        if (ingredients.Any(i => i?.itemDef != null && npc.inventory.GetAmount(i.itemid) < (int)i.amount * job.Batches))
        {
            Puts($"workshop: '{survivor.Character.Alias}' couldn't gather the ingredients for {job.Batches}x '{job.Shortname}' into its inventory (full?) - skipping it this visit.");
            failed.Add(job.Shortname);
            next();
            return;
        }

        foreach (ItemAmount ingredient in ingredients)
        {
            if (ingredient?.itemDef == null)
            {
                continue;
            }

            List<Item> collected = new();
            npc.inventory.Take(collected, ingredient.itemid, (int)ingredient.amount * job.Batches);

            foreach (Item taken in collected)
            {
                taken.Remove();
            }
        }

        float delay = Mathf.Clamp(bp.time * job.Batches * WorkshopCraftTimeScale, 1f, WorkshopMaxSecondsPerCraft);
        int produced = Mathf.Max(1, (int)bp.amountToCreate) * job.Batches;

        timer.Once(delay, () =>
        {
            BasePlayer liveNpc = survivor.Player;

            if (liveNpc == null || liveNpc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
            {
                _workshopUntil.Remove(survivor.Character.Id);
                return;
            }

            foreach (Item crafted in CreateStackSizedItems(def, produced))
            {
                if (!liveNpc.inventory.GiveItem(crafted))
                {
                    crafted.Drop(liveNpc.transform.position + Vector3.up, Vector3.zero);
                }
            }

            Puts($"workshop: '{survivor.Character.Alias}' crafted {produced}x '{job.Shortname}' at its workbench ({job.Reason}).");

            // A garment is put on straight away, and never crafted twice in one visit even if it
            // turned out not to be an upgrade (the planner would otherwise keep re-planning it).
            if (def.GetComponent<ItemModWearable>() != null)
            {
                failed.Add(job.Shortname);
                RunLootHookSafely(survivor, nameof(EvaluateAndUpgradeArmor), () => EvaluateAndUpgradeArmor(survivor));
            }

            next();
        });
    }

    // ============================================================
    // Storage organisation
    // ============================================================

    private const int StorageRowWidth = 6;

    private static readonly string[] ProcessedMetalOrder = { "metal.fragments", "metal.ore", "metal.refined", "hq.metal.ore" };
    private static readonly string[] SulfurGroupOrder = { "sulfur.ore", "sulfur", "charcoal", "gunpowder" };

    /// <summary>
    /// Which shelf a stored item belongs on. Items in the same group end up next to each other,
    /// sorted by name within it: scrap, components, metal (fragments/ore/refined), sulfur and its
    /// by-products, wood, stone, other raw resources, then ammo, weapons, attire, tools,
    /// medical, food, electrical, construction and everything else.
    /// </summary>
    private static (int Group, int Order) GetStorageSortKey(Item item)
    {
        string shortname = item.info.shortname;

        if (shortname == ScrapShortname)
        {
            return (0, 0);
        }

        if (item.info.category == ItemCategory.Component)
        {
            return (1, 0);
        }

        int metal = Array.IndexOf(ProcessedMetalOrder, shortname);

        if (metal >= 0)
        {
            return (2, metal);
        }

        int sulfur = Array.IndexOf(SulfurGroupOrder, shortname);

        if (sulfur >= 0)
        {
            return (3, sulfur);
        }

        if (shortname == WoodShortname)
        {
            return (4, 0);
        }

        if (shortname == StoneShortname)
        {
            return (5, 0);
        }

        return item.info.category switch
        {
            ItemCategory.Resources => (6, 0),
            ItemCategory.Ammunition => (7, 0),
            ItemCategory.Weapon => (8, 0),
            ItemCategory.Attire => (9, 0),
            ItemCategory.Tool => (10, 0),
            ItemCategory.Medical => (11, 0),
            ItemCategory.Food => (12, 0),
            ItemCategory.Electrical => (13, 0),
            ItemCategory.Construction => (14, 0),
            _ => (15, 0),
        };
    }

    /// <summary>
    /// Plain stackables only: no condition, no nested contents (attachments/backpack contents),
    /// no per-instance blueprint/data - safe to fold into one pile.
    /// </summary>
    private static bool IsMergeableStack(Item item)
    {
        return item.info.stackable > 1 && !item.hasCondition && item.contents == null && item.instanceData == null;
    }

    /// <summary>
    /// Tidies a survivor's boxes: merges partial stacks into full ones, then re-lays everything
    /// grouped by GetStorageSortKey, each group starting on a fresh row when there is room for
    /// that (packed tightly when there isn't). Synchronous and all-or-nothing in spirit: every
    /// detached item is put back somewhere, or dropped at the base as a last resort.
    /// </summary>
    private void OrganizeBaseStorage(Survivor survivor)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;

        if (home == null || npc == null || npc.IsDestroyed)
        {
            return;
        }

        List<StorageContainer> boxes = FindOwnedStorageBoxesNear(npc, home.Position, HomeStorageSearchRadius)
            .Where(b => b != null && !b.IsDestroyed && b.inventory != null)
            .ToList();

        if (boxes.Count == 0)
        {
            return;
        }

        List<Item> detached = new();

        try
        {
            foreach (StorageContainer box in boxes)
            {
                foreach (Item item in new List<Item>(box.inventory.itemList))
                {
                    item.RemoveFromContainer();
                    detached.Add(item);
                }
            }

            if (detached.Count == 0)
            {
                return;
            }

            // Fold plain stackables of the same item into as few, fuller stacks as possible,
            // reusing the existing Item objects (the extras are destroyed).
            List<Item> arranged = new();

            foreach (IGrouping<(int ItemId, ulong Skin), Item> group in detached.Where(IsMergeableStack).GroupBy(i => (i.info.itemid, i.skin)))
            {
                List<Item> pile = group.ToList();
                int total = pile.Sum(i => i.amount);
                int stack = pile[0].info.stackable;

                foreach (Item item in pile)
                {
                    if (total <= 0)
                    {
                        item.Remove();
                        continue;
                    }

                    item.amount = Math.Min(total, stack);
                    item.MarkDirty();
                    total -= item.amount;
                    arranged.Add(item);
                }
            }

            arranged.AddRange(detached.Where(i => !IsMergeableStack(i)));

            List<List<Item>> shelves = arranged
                .OrderBy(i => GetStorageSortKey(i).Group)
                .ThenBy(i => GetStorageSortKey(i).Order)
                .ThenBy(i => i.info.shortname, StringComparer.Ordinal)
                .ThenByDescending(i => i.amount)
                .GroupBy(i => GetStorageSortKey(i).Group)
                .Select(g => g.ToList())
                .ToList();

            int[] capacities = boxes.Select(b => b.inventory.capacity).ToArray();
            int totalCapacity = capacities.Sum();
            int paddedNeed = shelves.Sum(s => (int)Math.Ceiling(s.Count / (double)StorageRowWidth) * StorageRowWidth);
            bool padRows = paddedNeed <= totalCapacity;

            int slot = 0;
            List<Item> unplaced = new();

            foreach (List<Item> shelf in shelves)
            {
                foreach (Item item in shelf)
                {
                    if (!TryPlaceAtGlobalSlot(boxes, capacities, slot, item))
                    {
                        unplaced.Add(item);
                    }

                    slot++;
                }

                if (padRows && slot % StorageRowWidth != 0)
                {
                    slot += StorageRowWidth - slot % StorageRowWidth;
                }
            }

            // Anything that didn't land on its planned slot goes in the first free space anywhere.
            foreach (Item item in unplaced)
            {
                if (!boxes.Any(b => item.MoveToContainer(b.inventory)))
                {
                    item.Drop(home.Position + Vector3.up, Vector3.zero);
                }
            }

            detached.Clear();
            VerbosePuts($"home-storage: '{survivor.Character.Alias}' organised {arranged.Count} stack(s) across {boxes.Count} box(es).");
        }
        catch (Exception ex)
        {
            Puts($"WARNING: home-storage: organising '{survivor.Character.Alias}' base storage failed ({ex.Message}) - putting everything back.");
        }
        finally
        {
            // Whatever is still detached (an exception mid-way) goes back into any box with room.
            foreach (Item item in detached)
            {
                try
                {
                    if (item.parent == null && !boxes.Any(b => item.MoveToContainer(b.inventory)))
                    {
                        item.Drop(home.Position + Vector3.up, Vector3.zero);
                    }
                }
                catch (Exception)
                {
                    // An item already destroyed by the stack-merge step - nothing to restore.
                }
            }
        }
    }

    private static bool TryPlaceAtGlobalSlot(List<StorageContainer> boxes, int[] capacities, int globalSlot, Item item)
    {
        int remaining = globalSlot;

        for (int i = 0; i < boxes.Count; i++)
        {
            if (remaining < capacities[i])
            {
                return item.MoveToContainer(boxes[i].inventory, remaining, allowStack: false);
            }

            remaining -= capacities[i];
        }

        return false;
    }

    // ============================================================
    // Coffer assessment (every 5th recycler trip)
    // ============================================================

    private const int RecycleTripsPerAssessment = 5;

    private static string[] _weaponGoalShortnames;

    // Every firearm the project knows plus the crossbow (not in WeaponAmmoType - it fires arrows).
    private static string[] WeaponGoalShortnames =>
        _weaponGoalShortnames ??= WeaponAmmoType.Keys.Concat(new[] { "crossbow" }).ToArray();

    private static bool IsWeaponGoal(string shortname)
    {
        return Array.IndexOf(WeaponGoalShortnames, shortname) >= 0;
    }

    private static bool IsCraftGoalSatisfied(BasePlayer npc, List<StorageContainer> boxes, ItemDefinition def)
    {
        if (def.category == ItemCategory.Ammunition)
        {
            return npc.inventory.GetAmount(def.itemid) >= AmmoWorkshopRefillBelow;
        }

        return CountOwned(npc, boxes, def) > 0 || npc.inventory.containerWear.itemList.Any(w => w.info.itemid == def.itemid);
    }

    /// <summary>
    /// Keeps the components of the survivor's current craft goal out of the recycler.
    /// </summary>
    private bool IsRecycleFodderFor(Survivor survivor, Item item)
    {
        if (!IsRecycleFodder(item))
        {
            return false;
        }

        string goal = survivor.Character.CraftGoal;

        if (string.IsNullOrEmpty(goal))
        {
            return true;
        }

        ItemBlueprint bp = ItemManager.FindItemDefinition(goal)?.Blueprint;
        return bp == null || !bp.GetIngredients().Any(i => i?.itemDef != null && i.itemDef.itemid == item.info.itemid);
    }

    private void NoteRecycleTripCompleted(Survivor survivor)
    {
        Character character = survivor.Character;

        if (character.Home == null)
        {
            return;
        }

        if (++character.RecycleTripsSinceAssessment < RecycleTripsPerAssessment)
        {
            return;
        }

        character.RecycleTripsSinceAssessment = 0;
        AssessCoffers(survivor);
    }

    private sealed class GoalCandidate
    {
        public string Shortname = "";
        public int Category;
        public int Score;
        public bool Affordable;
        public List<string> Missing = new();
    }

    /// <summary>
    /// After five recycler trips a survivor reads what its base storage holds and decides what to
    /// craft next, aiming at the next best thing its workbench tier allows: a better firearm or
    /// crossbow first, then ammunition for what it owns, then armour/clothing it is missing
    /// (wood armour, bucket helmet, wolf headdress, shirts, pants, boots/shoes at tier 1 ...).
    /// The choice is stored as Character.CraftGoal: the workshop crafts it as soon as it is
    /// affordable, its components stop being recycled, and a goal that is already affordable
    /// pulls the next base trip forward. Weapons still need a researched blueprint (the scrap
    /// entitlement), so a weapon the survivor can't research yet is skipped for now.
    /// </summary>
    private void AssessCoffers(Survivor survivor)
    {
        BasePlayer npc = survivor.Player;
        HomeBase home = survivor.Character.Home;

        if (npc == null || npc.IsDestroyed || home == null || npc.blueprints == null)
        {
            return;
        }

        List<StorageContainer> boxes = FindOwnedStorageBoxesNear(npc, home.Position, HomeStorageSearchRadius);
        FindOwnedWorkbench(npc, home, out int wbLevel);

        ItemDefinition scrapDef = ItemManager.FindItemDefinition(ScrapShortname);
        int scrap = scrapDef != null ? CountOwned(npc, boxes, scrapDef) : 0;
        int researchLeft = scrap / ScrapPerFreeResearch - survivor.Character.FreeResearchesUsed;

        // What it already owns, so it only aims at genuine upgrades.
        List<Item> everything = boxes.Where(b => b != null && !b.IsDestroyed && b.inventory != null)
            .SelectMany(b => b.inventory.itemList)
            .Concat(npc.inventory.containerMain.itemList)
            .Concat(npc.inventory.containerBelt.itemList)
            .ToList();

        int ownedBestWeaponScore = 0;
        HashSet<string> ownedAmmoTypes = new(survivor.Character.KnownAmmoTypes ?? new List<string>());

        foreach (Item item in everything)
        {
            string shortname = item.info.shortname;

            if (!IsWeaponGoal(shortname))
            {
                continue;
            }

            ownedBestWeaponScore = Math.Max(ownedBestWeaponScore, WeaponGearScore.TryGetValue(shortname, out int s) ? s : 1);

            if (WeaponAmmoType.TryGetValue(shortname, out string ammo))
            {
                ownedAmmoTypes.Add(ammo);
            }
            else if (shortname == "crossbow")
            {
                ownedAmmoTypes.Add(ArrowShortname);
            }
        }

        GoalCandidate Evaluate(string shortname, int category, int score)
        {
            ItemDefinition def = ItemManager.FindItemDefinition(shortname);
            ItemBlueprint bp = def?.Blueprint;

            if (bp == null || !bp.userCraftable || bp.workbenchLevelRequired > wbLevel)
            {
                return null;
            }

            GoalCandidate candidate = new() { Shortname = shortname, Category = category, Score = score };

            foreach (ItemAmount ingredient in bp.GetIngredients())
            {
                if (ingredient?.itemDef == null)
                {
                    continue;
                }

                int have = CountOwned(npc, boxes, ingredient.itemDef);

                if (have < (int)ingredient.amount)
                {
                    candidate.Missing.Add($"{(int)ingredient.amount - have}x {ingredient.itemDef.shortname}");
                }
            }

            candidate.Affordable = candidate.Missing.Count == 0;
            return candidate;
        }

        List<GoalCandidate> pool = new();

        // 0. A better firearm / crossbow than anything owned (research permitting).
        foreach (string shortname in WeaponGoalShortnames)
        {
            int score = WeaponGearScore.TryGetValue(shortname, out int s) ? s : 1;
            ItemDefinition def = ItemManager.FindItemDefinition(shortname);

            if (def == null || score <= ownedBestWeaponScore || (!npc.blueprints.IsUnlocked(def) && researchLeft <= 0))
            {
                continue;
            }

            GoalCandidate candidate = Evaluate(shortname, 0, score);

            if (candidate != null)
            {
                pool.Add(candidate);
            }
        }

        // 1. Ammunition for the weapons it owns.
        foreach (string ammoShortname in ownedAmmoTypes)
        {
            ItemDefinition ammoDef = ItemManager.FindItemDefinition(ammoShortname);

            if (ammoDef == null || npc.inventory.GetAmount(ammoDef.itemid) >= AmmoWorkshopRefillBelow)
            {
                continue;
            }

            GoalCandidate candidate = Evaluate(ammoShortname, 1, 0);

            if (candidate != null)
            {
                pool.Add(candidate);
            }
        }

        // 2. Armour / clothing for any slot it could be wearing something better in.
        int position = 0;

        foreach ((string _, string shortname) in GetUsefulClothingCandidates(npc, wbLevel))
        {
            GoalCandidate candidate = Evaluate(shortname, 2, (int)GetArmorTier(shortname) * 100 - position++);

            if (candidate != null)
            {
                pool.Add(candidate);
            }
        }

        GoalCandidate chosen = null;

        for (int category = 0; category <= 2 && chosen == null; category++)
        {
            chosen = pool.Where(c => c.Category == category)
                .OrderByDescending(c => c.Affordable)
                .ThenBy(c => c.Missing.Count)
                .ThenByDescending(c => c.Score)
                .FirstOrDefault();
        }

        string header = $"assess: '{survivor.Character.Alias}' read its coffers ({boxes.Count} box(es), {scrap} scrap, tier {wbLevel} workbench)";

        if (chosen == null)
        {
            survivor.Character.CraftGoal = null;
            Puts($"{header} - nothing worth aiming for with what it has and can research right now.");
            return;
        }

        survivor.Character.CraftGoal = chosen.Shortname;

        if (chosen.Category == 1 && survivor.Character.KnownAmmoTypes != null && !survivor.Character.KnownAmmoTypes.Contains(chosen.Shortname))
        {
            survivor.Character.KnownAmmoTypes.Add(chosen.Shortname);
        }

        string[] categoryNames = { "weapon", "ammunition", "armour/clothing" };

        if (chosen.Affordable)
        {
            _nextBaseReturnTime[survivor.Character.Id] = Time.realtimeSinceStartup + 10f;
            Puts($"{header} - wants a {categoryNames[chosen.Category]} next: '{chosen.Shortname}', and it can afford it now - heading home to craft it.");
        }
        else
        {
            Puts($"{header} - wants a {categoryNames[chosen.Category]} next: '{chosen.Shortname}'; still needs {string.Join(", ", chosen.Missing)} (those components are now kept out of the recycler).");
        }
    }

    // ============================================================
    // Sheet-metal base upgrade
    // ============================================================

    private const int MetalUpgradeFragmentTrigger = 2000;
    private const int MetalUpgradeFragmentReserve = 300;
    private const int MetalUpgradeMaxBlocksPerVisit = 40;
    private const float MetalUpgradeStepSeconds = 0.35f;

    /// <summary>
    /// With 2000+ metal fragments banked, upgrades the survivor's foundations, walls and floors to
    /// sheet metal using a hammer (crafted for wood if it has none). Incremental real costs are
    /// paid from the survivor's own stock, withdrawn from its boxes as needed.
    /// </summary>
    private void TryUpgradeBaseToMetal(Survivor survivor, List<StorageContainer> boxes, Action onComplete)
    {
        BasePlayer npc = survivor.Player;
        HomeBase home = survivor.Character.Home;
        ItemDefinition fragDef = ItemManager.FindItemDefinition("metal.fragments");

        if (npc == null || npc.IsDestroyed || home == null || fragDef == null || CountOwned(npc, boxes, fragDef) < MetalUpgradeFragmentTrigger)
        {
            onComplete?.Invoke();
            return;
        }

        List<BuildingBlock> blocks = new();

        foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities)
        {
            if (networkable is BuildingBlock block && !block.IsDestroyed && block.OwnerID == npc.userID
                && block.grade < BuildingGrade.Enum.Metal
                && Vector3.Distance(block.transform.position, home.Position) <= HomeStorageSearchRadius)
            {
                blocks.Add(block);
            }
        }

        if (blocks.Count == 0)
        {
            onComplete?.Invoke();
            return;
        }

        // Foundations first (they protect everything above), then walls, then the rest.
        blocks.Sort((a, b) => UpgradePriority(a).CompareTo(UpgradePriority(b)));

        if (!EnsureHammerForUpgrade(survivor, npc, boxes))
        {
            Puts($"upgrade: '{survivor.Character.Alias}' has {CountOwned(npc, boxes, fragDef)} metal fragments banked but no hammer and can't make one - skipping the sheet-metal upgrade this visit.");
            onComplete?.Invoke();
            return;
        }

        Puts($"upgrade: '{survivor.Character.Alias}' has {CountOwned(npc, boxes, fragDef)} metal fragments banked - upgrading its base to sheet metal ({blocks.Count} piece(s) below metal).");

        int index = 0;
        int upgraded = 0;

        void Step()
        {
            BasePlayer liveNpc = survivor.Player;

            if (liveNpc == null || liveNpc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
            {
                return;
            }

            if (index >= blocks.Count || upgraded >= MetalUpgradeMaxBlocksPerVisit)
            {
                Puts($"upgrade: '{survivor.Character.Alias}' upgraded {upgraded} piece(s) to sheet metal this visit.");
                onComplete?.Invoke();
                return;
            }

            BuildingBlock block = blocks[index++];

            if (block == null || block.IsDestroyed || block.grade >= BuildingGrade.Enum.Metal)
            {
                Step();
                return;
            }

            Construction construction = PrefabAttribute.server.Find<Construction>(block.prefabID);
            ConstructionGrade metalGrade = construction?.GetGrade(BuildingGrade.Enum.Metal, 0);

            if (metalGrade == null)
            {
                Step();
                return;
            }

            // Incremental cost from the block's current grade.
            Dictionary<ItemDefinition, int> cost = new();

            foreach (ItemAmount amount in metalGrade.CostToBuild(block.grade))
            {
                if (amount.itemDef != null && amount.amount > 0f)
                {
                    cost[amount.itemDef] = (int)amount.amount;
                }
            }

            foreach (KeyValuePair<ItemDefinition, int> need in cost)
            {
                int have = liveNpc.inventory.GetAmount(need.Key.itemid);
                int reserve = need.Key.itemid == fragDef.itemid ? MetalUpgradeFragmentReserve : 0;

                if (have < need.Value)
                {
                    WithdrawUpToAmount(boxes, need.Key.shortname, need.Value - have + reserve, liveNpc.inventory.containerMain);
                }
            }

            if (cost.Any(c => liveNpc.inventory.GetAmount(c.Key.itemid) < c.Value))
            {
                Puts($"upgrade: '{survivor.Character.Alias}' ran out of materials after {upgraded} piece(s) - stopping the sheet-metal upgrade for now.");
                onComplete?.Invoke();
                return;
            }

            EquipHeldItemByShortname(liveNpc, HammerShortname);

            BuildingGrade.Enum fromGrade = block.grade;
            block.ChangeGrade(BuildingGrade.Enum.Metal, playEffect: true);
            block.SetHealthToMax();
            DeductRealConstructionCost(liveNpc, block, BuildingGrade.Enum.Metal, fromGrade);
            block.ClientRPC(RpcTarget.NetworkGroup("DoUpgradeEffect"), (int)BuildingGrade.Enum.Metal, 0uL);

            upgraded++;
            timer.Once(MetalUpgradeStepSeconds, Step);
        }

        Step();
    }

    private static int UpgradePriority(BuildingBlock block)
    {
        string name = block.ShortPrefabName ?? string.Empty;

        if (name.Contains("foundation"))
        {
            return 0;
        }

        return name.Contains("wall") ? 1 : 2;
    }

    private bool EnsureHammerForUpgrade(Survivor survivor, BasePlayer npc, List<StorageContainer> boxes)
    {
        ItemDefinition hammerDef = ItemManager.FindItemDefinition(HammerShortname);

        if (hammerDef == null)
        {
            return false;
        }

        if (npc.inventory.GetAmount(hammerDef.itemid) > 0)
        {
            return true;
        }

        // The hammer is normally banked in the tool cupboard's general slots.
        BuildingPrivlidge cupboard = survivor.Character.Home != null ? FindOwnedCupboard(survivor.Character.Home) : null;
        Item banked = cupboard?.inventory?.itemList.FirstOrDefault(i => i.info.itemid == hammerDef.itemid);

        if (banked != null && banked.MoveToContainer(npc.inventory.containerMain))
        {
            return true;
        }

        if (CountInBoxes(boxes, hammerDef.itemid) > 0 && WithdrawUpToAmount(boxes, HammerShortname, 1, npc.inventory.containerMain) > 0)
        {
            return true;
        }

        ItemBlueprint bp = hammerDef.Blueprint;

        if (bp == null || MaxAffordableBatches(npc, boxes, bp, 1) < 1)
        {
            return false;
        }

        foreach (ItemAmount ingredient in bp.GetIngredients())
        {
            if (ingredient?.itemDef == null)
            {
                continue;
            }

            int need = (int)ingredient.amount;
            int have = npc.inventory.GetAmount(ingredient.itemid);

            if (have < need)
            {
                WithdrawUpToAmount(boxes, ingredient.itemDef.shortname, need - have, npc.inventory.containerMain);
            }

            List<Item> collected = new();
            npc.inventory.Take(collected, ingredient.itemid, need);

            foreach (Item taken in collected)
            {
                taken.Remove();
            }
        }

        Item hammer = ItemManager.CreateByItemID(hammerDef.itemid, 1);

        if (hammer == null || !npc.inventory.GiveItem(hammer))
        {
            hammer?.Remove();
            return false;
        }

        Puts($"upgrade: '{survivor.Character.Alias}' crafted a hammer for the sheet-metal upgrade.");
        return true;
    }

    // ============================================================
    // Base sleeping bag
    // ============================================================

    private readonly Dictionary<Guid, int> _baseBagFailCount = new();
    private const int BaseBagPityAfterFailures = 3;

    /// <summary>
    /// Places the survivor's respawn bag right at its base cupboard. Uses, in order: a bag it is
    /// already carrying (the checklist bag is now held until the base exists instead of being
    /// dropped wherever the survivor happened to be), one banked in storage, or one it crafts from
    /// carried / banked cloth. After three failed trips it is handed the missing cloth, because a
    /// base with no bag means every death respawns the survivor on a far-away beach.
    /// </summary>
    private void TryPlaceSleepingBagAtBase(Survivor survivor, Action onComplete)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;
        Guid id = survivor.Character.Id;

        if (home == null || npc == null || npc.IsDestroyed || HasSleepingBagNearHome(npc, home))
        {
            _baseBagFailCount.Remove(id);
            onComplete?.Invoke();
            return;
        }

        ItemDefinition bagDef = ItemManager.FindItemDefinition(SleepingBagShortname);
        ItemBlueprint bp = bagDef?.Blueprint;
        ItemCrafter crafter = npc.inventory.crafting;

        if (bagDef == null || bp == null || crafter == null)
        {
            onComplete?.Invoke();
            return;
        }

        List<StorageContainer> boxes = FindOwnedStorageBoxesNear(npc, home.Position, HomeStorageSearchRadius);

        if (npc.inventory.FindItemByItemID(bagDef.itemid) == null)
        {
            WithdrawUpToAmount(boxes, SleepingBagShortname, 1, npc.inventory.containerMain);
        }

        if (npc.inventory.FindItemByItemID(bagDef.itemid) != null)
        {
            PlaceBagAtCupboard(survivor, bagDef, onComplete);
            return;
        }

        foreach (ItemAmount ingredient in bp.GetIngredients())
        {
            if (ingredient?.itemDef == null)
            {
                continue;
            }

            int have = npc.inventory.GetAmount(ingredient.itemid);

            if (have < (int)ingredient.amount)
            {
                WithdrawUpToAmount(boxes, ingredient.itemDef.shortname, (int)ingredient.amount - have, npc.inventory.containerMain);
            }
        }

        if (!crafter.CanCraft(bp, SleepingBagTargetBatches, free: false))
        {
            int fails = _baseBagFailCount.GetValueOrDefault(id) + 1;
            _baseBagFailCount[id] = fails;

            if (fails < BaseBagPityAfterFailures)
            {
                VerbosePuts($"home-storage: '{survivor.Character.Alias}' has no sleeping bag at its base and couldn't make one yet (attempt {fails}/{BaseBagPityAfterFailures}).");
                onComplete?.Invoke();
                return;
            }

            foreach (ItemAmount ingredient in bp.GetIngredients())
            {
                if (ingredient?.itemDef == null)
                {
                    continue;
                }

                int missing = (int)ingredient.amount - npc.inventory.GetAmount(ingredient.itemid);

                foreach (Item grant in CreateStackSizedItems(ingredient.itemDef, Math.Max(0, missing)))
                {
                    if (!npc.inventory.GiveItem(grant))
                    {
                        grant.Remove();
                    }
                }
            }

            Puts($"home-storage: '{survivor.Character.Alias}' failed to put a sleeping bag together {fails} trips running - handing it the missing materials so it can respawn at its base.");

            if (!crafter.CanCraft(bp, SleepingBagTargetBatches, free: false))
            {
                onComplete?.Invoke();
                return;
            }
        }

        crafter.CraftItem(bp, npc, amount: SleepingBagTargetBatches);
        Puts($"home-storage: '{survivor.Character.Alias}' is crafting a sleeping bag to place at its base.");

        int ticks = 0;
        Timer pollTimer = null;

        pollTimer = timer.Every(1f, () =>
        {
            BasePlayer liveNpc = survivor.Player;

            if (liveNpc == null || liveNpc.IsDestroyed || survivor.Character.State == CharacterState.Dead)
            {
                pollTimer.Destroy();
                onComplete?.Invoke();
                return;
            }

            if (liveNpc.inventory.FindItemByItemID(bagDef.itemid) != null)
            {
                pollTimer.Destroy();
                PlaceBagAtCupboard(survivor, bagDef, onComplete);
                return;
            }

            if (++ticks >= SleepingBagDeployWaitMaxTicks)
            {
                pollTimer.Destroy();
                onComplete?.Invoke();
            }
        });
    }

    private void PlaceBagAtCupboard(Survivor survivor, ItemDefinition bagDef, Action onComplete)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;

        if (home == null || npc == null || npc.IsDestroyed)
        {
            onComplete?.Invoke();
            return;
        }

        BuildingPrivlidge cupboard = FindOwnedCupboard(home);
        Vector3 anchor = cupboard != null ? cupboard.transform.position : home.Position;
        Vector3 away = npc.transform.position - anchor;
        away.y = 0f;

        if (away.sqrMagnitude < 0.01f)
        {
            away = npc.transform.forward;
        }

        Vector3 point = anchor + away.normalized * 1.5f;
        point.y = anchor.y;

        void Deploy()
        {
            BasePlayer liveNpc = survivor.Player;
            Item bagItem = liveNpc != null && !liveNpc.IsDestroyed ? liveNpc.inventory.FindItemByItemID(bagDef.itemid) : null;

            if (bagItem != null)
            {
                DeployBaseSleepingBag(survivor, liveNpc, bagDef, bagItem);
                _baseBagFailCount.Remove(survivor.Character.Id);
            }

            onComplete?.Invoke();
        }

        StartPhasingToDestination(survivor, point, onArrived: Deploy, onFailed: Deploy);
    }

    // ============================================================
    // Death-site rules
    // ============================================================

    private const float DeathRepeatRadius = 40f;
    private const float DangerZoneRadius = 40f;
    private const float DangerZoneSeconds = 600f;
    private const int DeathClusterThreshold = 4;
    private const float DeathClusterWindowSeconds = 1200f;

    private readonly List<(Vector3 Position, float Time)> _recentDeaths = new();

    /// <summary>
    /// "Died the same way, in the same place, twice" and "four bodies in one spot" both mean the
    /// spot is lethal (50+ corpses piled up at Launch Site while bots kept running back to loot
    /// their own death bags). Poisons the area for everyone for ten minutes.
    /// </summary>
    private void PoisonDangerZone(Vector3 position, string why)
    {
        _globalStallZones.Add((position, DangerZoneRadius, Time.realtimeSinceStartup + DangerZoneSeconds));
        Puts($"death-zone: {why} - avoiding the {DangerZoneRadius:F0}m area around {position} for {DangerZoneSeconds / 60f:F0} minutes.");
    }

    private static string DeathMechanism(string cause)
    {
        if (string.IsNullOrEmpty(cause))
        {
            return "Unknown";
        }

        int space = cause.IndexOfAny(new[] { ' ', '(' });
        return space > 0 ? cause.Substring(0, space) : cause;
    }

    /// <summary>
    /// Decides whether a survivor may walk back to the spot it just died at. It gets exactly one
    /// attempt per death location; dying there again the same way poisons the area instead.
    /// </summary>
    private bool RegisterDeathAndAllowRevisit(Survivor survivor, Vector3 position, string cause)
    {
        Character character = survivor.Character;
        float now = Time.realtimeSinceStartup;
        string mechanism = DeathMechanism(cause);

        bool sameSpot = character.LastDeathCause != null && Vector3.Distance(character.LastDeathPosition, position) <= DeathRepeatRadius;
        bool sameMechanism = sameSpot && character.LastDeathCause == mechanism;

        character.LastDeathPosition = position;
        character.LastDeathCause = mechanism;

        _recentDeaths.RemoveAll(d => now - d.Time > DeathClusterWindowSeconds);
        _recentDeaths.Add((position, now));

        int clustered = _recentDeaths.Count(d => Vector3.Distance(d.Position, position) <= DangerZoneRadius);

        if (sameMechanism)
        {
            PoisonDangerZone(position, $"'{character.Alias}' died to {mechanism} here twice in a row");
            return false;
        }

        if (clustered >= DeathClusterThreshold)
        {
            PoisonDangerZone(position, $"{clustered} survivors have died within {DangerZoneRadius:F0}m of here in the last {DeathClusterWindowSeconds / 60f:F0} minutes");
            return false;
        }

        return !IsInGlobalStallZone(position) && !IsInGhostRouteOnlyMonumentArea(position);
    }

    // ============================================================
    // Ghost-route-only monuments (Launch Site)
    // ============================================================

    // Monuments where bots may ONLY run the authored loot / card-puzzle ghost routes. Ambient
    // looting there got dozens of bots killed (they wandered between containers in the open);
    // with no route available they leave rather than fall back to general looting.
    private static readonly string[] GhostRouteOnlyMonumentSubstrings = { "launch_site" };

    private const float GhostRouteOnlyAreaPadding = 30f;
    private List<MonumentInfo> _ghostRouteOnlyMonuments;

    private static bool IsGhostRouteOnlyMonument(MonumentInfo monument)
    {
        return monument != null && GhostRouteOnlyMonumentSubstrings.Any(s => monument.name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private List<MonumentInfo> GetGhostRouteOnlyMonuments()
    {
        if (_ghostRouteOnlyMonuments == null)
        {
            // Resolved once, as soon as the monument list exists - a map with no Launch Site caches an empty list.
            if (MonumentAccess.GetAllMonuments().Count == 0)
            {
                return new List<MonumentInfo>();
            }

            _ghostRouteOnlyMonuments = MonumentAccess.GetAllMonuments().Where(IsGhostRouteOnlyMonument).ToList();
        }

        return _ghostRouteOnlyMonuments;
    }

    private bool IsInGhostRouteOnlyMonumentArea(Vector3 position)
    {
        foreach (MonumentInfo monument in GetGhostRouteOnlyMonuments())
        {
            if (monument == null)
            {
                continue;
            }

            Bounds padded = monument.Bounds;
            padded.Expand(GhostRouteOnlyAreaPadding * 2f);

            if (new OBB(monument.transform, padded).Contains(position))
            {
                return true;
            }
        }

        return false;
    }

    // How many survivors may run ghost routes at the same monument at once (2026-10-03, Lucas's
    // spec): 3 at Tier 2 and above, 2 at Tier 0-1 - provided no two of them start from the same
    // route start location. The old rule was one holder per route file.
    private static int GetGhostRouteConcurrencyCap(string monumentName)
    {
        return GetMonumentTier(monumentName) >= MonumentTier.TierTwo ? 3 : 2;
    }

    // Two routes whose recorded start points sit closer than this (monument-local space) count as
    // the same start location.
    private const float GhostRouteSameStartDistance = 4f;

    private readonly Dictionary<string, Vector3> _ghostRouteLocalStartCache = new();

    /// <summary>
    /// A route's first waypoint in monument-local space (or raw world space if the trace was not
    /// recorded near a monument) - comparable between two routes of the same monument.
    /// </summary>
    private bool TryGetGhostRouteLocalStart(string route, string monumentName, out Vector3 start)
    {
        if (_ghostRouteLocalStartCache.TryGetValue(route, out start))
        {
            return true;
        }

        if (TryLoadTraceWaypoints(route, out List<GhostRouteWaypoint> waypoints, out MonumentInfo _, monumentName) && waypoints.Count > 0)
        {
            start = waypoints[0].Position;
            _ghostRouteLocalStartCache[route] = start;
            return true;
        }

        start = default;
        return false;
    }

    /// <summary>
    /// Whether characterId may take this route right now: not recently exhausted, not held by
    /// someone else, under the monument's concurrency cap, and starting somewhere different from
    /// every other survivor currently on a route of the same monument.
    /// </summary>
    private bool IsGhostRouteClaimable(string route, string monumentName, Guid characterId, float now)
    {
        if (_ghostRouteExhaustedUntil.TryGetValue(route, out float exhaustedUntil) && exhaustedUntil > now)
        {
            return false;
        }

        if (_ghostRouteClaims.TryGetValue(route, out (Guid CharacterId, float ExpiresAt) own) && own.ExpiresAt > now)
        {
            // Already this survivor's own claim: always fine. Someone else's: never.
            return own.CharacterId == characterId;
        }

        string folder = System.IO.Path.GetDirectoryName(route) ?? string.Empty;
        int activeOthers = 0;
        bool haveStart = TryGetGhostRouteLocalStart(route, monumentName, out Vector3 myStart);

        foreach (KeyValuePair<string, (Guid CharacterId, float ExpiresAt)> claim in _ghostRouteClaims)
        {
            if (claim.Value.ExpiresAt <= now || claim.Value.CharacterId == characterId
                || !string.Equals(System.IO.Path.GetDirectoryName(claim.Key) ?? string.Empty, folder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            activeOthers++;

            if (haveStart && TryGetGhostRouteLocalStart(claim.Key, monumentName, out Vector3 otherStart)
                && Vector3.Distance(myStart, otherStart) < GhostRouteSameStartDistance)
            {
                return false;
            }
        }

        return activeOthers < GetGhostRouteConcurrencyCap(monumentName);
    }

    private readonly Dictionary<string, Vector3> _ghostRouteStartCache = new();

    /// <summary>
    /// Start point of a ghost route currently free for this monument (not exhausted, not held by
    /// another survivor), without claiming it. False when there is nothing to run.
    /// </summary>
    private bool TryGetAvailableGhostRouteStart(MonumentInfo monument, Guid characterId, out Vector3 start)
    {
        start = default;

        foreach (KeyValuePair<string, string> entry in MonumentGhostRouteFolders)
        {
            if (monument.name.IndexOf(entry.Key, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            string folderPath = $"{TraceDirectory}/{entry.Value}";

            if (!System.IO.Directory.Exists(folderPath))
            {
                return false;
            }

            float now = Time.realtimeSinceStartup;

            foreach (string candidate in System.IO.Directory.GetFiles(folderPath, "*.csv"))
            {
                string route = candidate.Replace('\\', '/');

                if (!IsGhostRouteClaimable(route, monument.name, characterId, now))
                {
                    continue;
                }

                string cacheKey = $"{route}|{monument.GetInstanceID()}";

                if (_ghostRouteStartCache.TryGetValue(cacheKey, out Vector3 cached))
                {
                    start = cached;
                    return true;
                }

                if (TryLoadTraceWaypoints(route, out List<GhostRouteWaypoint> waypoints, out MonumentInfo recordedAt, monument.name) && waypoints.Count > 0)
                {
                    List<GhostRouteWaypoint> world = recordedAt != null ? ProjectGhostRouteToMonument(waypoints, monument) : waypoints;
                    start = world[0].Position;
                    _ghostRouteStartCache[cacheKey] = start;
                    return true;
                }
            }

            return false;
        }

        return false;
    }

    // ============================================================
    // Returning to base after dying away from it
    // ============================================================

    /// <summary>
    /// A survivor that respawns somewhere other than its base bag (beach spawn, or an old far-away
    /// bag) used to just carry on from there with a rock - and run straight back to wherever it
    /// died. It now heads home first (workshop, storage, tools, clothes) and goes out properly kitted.
    /// </summary>
    private void BeginReturnToBaseAfterRespawn(Survivor survivor, Action<Survivor> runHomeCatchUp)
    {
        HomeBase home = survivor.Character.Home;
        BasePlayer npc = survivor.Player;

        if (home == null || npc == null || npc.IsDestroyed)
        {
            StartLootForResourcesTask(survivor);
            return;
        }

        Vector3 fromHome = npc.transform.position - home.Position;
        fromHome.y = 0f;

        if (fromHome.sqrMagnitude < 1f)
        {
            fromHome = Vector3.forward;
        }

        Vector3 outsidePoint = SnapApproachPointToNavMesh(npc, home.Position + fromHome.normalized * (HomeCrossingRadius + 2f));

        Puts($"'{survivor.Character.Alias}' respawned away from its base ({Vector3.Distance(npc.transform.position, home.Position):F0}m) - heading home to kit up before doing anything else.");

        survivor.Character.CurrentTask = TaskType.LootForResources;

        StartLongDistanceWalkDirect(
            survivor,
            outsidePoint,
            onArrived: () => runHomeCatchUp(survivor),
            onFailed: () => runHomeCatchUp(survivor));
    }
}
