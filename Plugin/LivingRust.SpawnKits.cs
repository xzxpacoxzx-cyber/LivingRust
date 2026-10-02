using System;
using System.Collections.Generic;
using System.Linq;
using LivingRust.Models;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Preset weapon-kit spawn commands, one /lr.spawn.X command per weapon with a
/// combat WeaponFireProfile, spawning a survivor equipped with that weapon,
/// matching ammo, full metal armor, and medical supplies. Snipers are excluded.
/// </summary>
public partial class LivingRust
{
    /// <summary>
    /// Armor-tier presets for kit spawns. Every tier wears a base clothing layer
    /// (hoodie/pants/boots) under its own armor. High wears a metal facemask,
    /// chestplate, and roadsign kilt; Medium wears a full roadsign set; Low wears a
    /// full wood set. EquipKitArmor's container-then-inventory fallback handles any
    /// slot conflict gracefully.
    /// </summary>
    private enum KitArmorTier
    {
        High,
        Medium,
        Low,
    }

    private const string KitHoodieShortname = "hoodie";
    private const string KitPantsShortname = "pants";
    private const string KitBootsShortname = "shoes.boots";
    private const string KitFacemaskShortname = "metal.facemask";
    private const string KitChestplateShortname = "metal.plate.torso";
    private const string KitRoadsignJacketShortname = "roadsign.jacket";
    private const string KitRoadsignGlovesShortname = "roadsign.gloves";
    private const string KitRoadsignKiltShortname = "roadsign.kilt";
    private const string KitCoffeeCanHelmetShortname = "coffeecan.helmet";
    private const string KitWoodJacketShortname = "wood.armor.jacket";
    private const string KitWoodPantsShortname = "wood.armor.pants";
    private const string KitWoodHelmetShortname = "wood.armor.helmet";
    private const string KitSyringeShortname = "syringe.medical";
    private const string KitBandageShortname = "bandage";
    private const int KitSyringeCount = 4;
    private const int KitBandageCount = 4;

    private readonly struct WeaponKit
    {
        public readonly string WeaponShortname;
        public readonly string AmmoShortname;
        public readonly string DisplayName;

        public WeaponKit(string weaponShortname, string ammoShortname, string displayName)
        {
            WeaponShortname = weaponShortname;
            AmmoShortname = ammoShortname;
            DisplayName = displayName;
        }
    }

    /// <summary>
    /// Maps each weapon to its real matching ammo type and display name.
    /// </summary>
    private static readonly Dictionary<string, WeaponKit> SpawnKits = new()
    {
        ["ak"] = new WeaponKit("rifle.ak", "ammo.rifle", "AK47"),
        ["lr300"] = new WeaponKit("rifle.lr300", "ammo.rifle", "LR-300"),
        ["m249"] = new WeaponKit("lmg.m249", "ammo.rifle", "M249"),
        ["hmlmg"] = new WeaponKit("hmlmg", "ammo.rifle", "HMLMG"),
        ["semiautorifle"] = new WeaponKit("rifle.semiauto", "ammo.rifle", "Semi-Automatic Rifle"),
        ["sks"] = new WeaponKit("rifle.sks", "ammo.rifle", "SKS"),
        ["m39"] = new WeaponKit("rifle.m39", "ammo.rifle", "M39 Rifle"),
        ["m16a2"] = new WeaponKit("m16a2", "ammo.rifle", "M16A2"),

        ["mp5"] = new WeaponKit("smg.mp5", "ammo.pistol", "MP5A4"),
        ["thompson"] = new WeaponKit("smg.thompson", "ammo.pistol", "Thompson"),
        ["customsmg"] = new WeaponKit("smg.2", "ammo.pistol", "Custom SMG"),
        ["handmadesmg"] = new WeaponKit("t1_smg", "ammo.pistol", "Handmade SMG"),

        ["python"] = new WeaponKit("pistol.python", "ammo.pistol", "Python Revolver"),
        ["revolver"] = new WeaponKit("pistol.revolver", "ammo.pistol", "Revolver"),
        ["semiautopistol"] = new WeaponKit("pistol.semiauto", "ammo.pistol", "Semi-Auto Pistol"),
        ["m92"] = new WeaponKit("pistol.m92", "ammo.pistol", "M92 Pistol"),
        ["hcrevolver"] = new WeaponKit("revolver.hc", "ammo.pistol", "High Caliber Revolver"),
        ["p17"] = new WeaponKit("pistol.prototype17", "ammo.pistol", "Prototype 17"),

        ["pump"] = new WeaponKit("shotgun.pump", "ammo.shotgun", "Pump Shotgun"),
        ["m4shotgun"] = new WeaponKit("shotgun.m4", "ammo.shotgun", "M4 Shotgun"),
        ["spas12"] = new WeaponKit("shotgun.spas12", "ammo.shotgun", "Spas-12"),
        ["doublebarrel"] = new WeaponKit("shotgun.double", "ammo.shotgun", "Double Barrel Shotgun"),
    };

    [ChatCommand("lr.spawn.ak")]
    private void CmdSpawnKitAk(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "ak", args);

    /// <summary>
    /// Console/bindable version, forwarding any armor-tier arg the same way other
    /// console commands in this project do.
    /// </summary>
    [ConsoleCommand("lr.spawn.ak")]
    private void CmdSpawnKitAkConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunSpawnKit(player, "ak", arg.HasArgs() ? arg.Args.Select(a => a.ToString()).ToArray() : Array.Empty<string>());
        }
    }

    [ChatCommand("lr.spawn.lr300")]
    private void CmdSpawnKitLr300(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "lr300", args);

    [ChatCommand("lr.spawn.m249")]
    private void CmdSpawnKitM249(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "m249", args);

    [ChatCommand("lr.spawn.hmlmg")]
    private void CmdSpawnKitHmlmg(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "hmlmg", args);

    [ChatCommand("lr.spawn.semiautorifle")]
    private void CmdSpawnKitSemiAutoRifle(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "semiautorifle", args);

    [ChatCommand("lr.spawn.sks")]
    private void CmdSpawnKitSks(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "sks", args);

    [ChatCommand("lr.spawn.m39")]
    private void CmdSpawnKitM39(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "m39", args);

    [ChatCommand("lr.spawn.m16a2")]
    private void CmdSpawnKitM16A2(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "m16a2", args);

    [ChatCommand("lr.spawn.mp5")]
    private void CmdSpawnKitMp5(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "mp5", args);

    [ChatCommand("lr.spawn.thompson")]
    private void CmdSpawnKitThompson(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "thompson", args);

    [ChatCommand("lr.spawn.customsmg")]
    private void CmdSpawnKitCustomSmg(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "customsmg", args);

    [ChatCommand("lr.spawn.handmadesmg")]
    private void CmdSpawnKitHandmadeSmg(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "handmadesmg", args);

    [ChatCommand("lr.spawn.python")]
    private void CmdSpawnKitPython(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "python", args);

    [ChatCommand("lr.spawn.revolver")]
    private void CmdSpawnKitRevolver(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "revolver", args);

    [ChatCommand("lr.spawn.semiautopistol")]
    private void CmdSpawnKitSemiAutoPistol(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "semiautopistol", args);

    [ChatCommand("lr.spawn.m92")]
    private void CmdSpawnKitM92(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "m92", args);

    [ChatCommand("lr.spawn.hcrevolver")]
    private void CmdSpawnKitHcRevolver(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "hcrevolver", args);

    [ChatCommand("lr.spawn.p17")]
    private void CmdSpawnKitP17(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "p17", args);

    [ChatCommand("lr.spawn.pump")]
    private void CmdSpawnKitPump(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "pump", args);

    [ChatCommand("lr.spawn.m4shotgun")]
    private void CmdSpawnKitM4Shotgun(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "m4shotgun", args);

    [ChatCommand("lr.spawn.spas12")]
    private void CmdSpawnKitSpas12(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "spas12", args);

    [ChatCommand("lr.spawn.doublebarrel")]
    private void CmdSpawnKitDoubleBarrel(BasePlayer player, string command, string[] args) => RunSpawnKit(player, "doublebarrel", args);

    /// <summary>
    /// Thin wrapper that spawns exactly one kit at the caller's aim point. All the
    /// real work is in SpawnKitAt below, shared with RunSpawnAllKits. args[0], if
    /// given, picks the armor tier ("medium"/"low", otherwise "high").
    /// </summary>
    private void RunSpawnKit(BasePlayer player, string kitKey, string[] args)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        KitArmorTier armorTier = KitArmorTier.High;
        string tierLabel = "high";

        if (args.Length > 0)
        {
            if (args[0].Equals("medium", StringComparison.OrdinalIgnoreCase))
            {
                armorTier = KitArmorTier.Medium;
                tierLabel = "medium";
            }
            else if (args[0].Equals("low", StringComparison.OrdinalIgnoreCase))
            {
                armorTier = KitArmorTier.Low;
                tierLabel = "low";
            }
        }

        Vector3 spawnPosition = FindSpawnAimPoint(player, out bool aimedSpawn);
        Character character = SpawnKitAt(player, kitKey, spawnPosition, player.transform.rotation, armorTier);

        if (character == null)
        {
            return;
        }

        string where = aimedSpawn ? "where you're looking" : "near you (nothing solid in view)";
        player.ChatMessage($"[LivingRust] Spawned '{character.Alias}' {where} with a {SpawnKits[kitKey].DisplayName} kit ({tierLabel} armor). (ID {character.BotId})");
    }

    /// <summary>
    /// Spawns a survivor at the given position/rotation and layers the kit on top:
    /// weapon, ammo, worn armor, medical supplies, and a topped-off magazine.
    /// Returns the new Character on success, or null on failure.
    /// </summary>
    private Character SpawnKitAt(BasePlayer player, string kitKey, Vector3 position, Quaternion rotation, KitArmorTier armorTier = KitArmorTier.High)
    {
        if (!SpawnKits.TryGetValue(kitKey, out WeaponKit kit))
        {
            player.ChatMessage($"[LivingRust] Unknown kit '{kitKey}'.");
            return null;
        }

        Character character = _engine.CharacterManager.CreateInitialSurvivor();
        Survivor survivor = _engine.SurvivorManager.Create(character);

        BasePlayer npc = SpawnSurvivor(survivor, position, rotation);

        if (npc == null)
        {
            player.ChatMessage("[LivingRust] Failed to spawn survivor.");
            Puts($"ERROR: Failed to spawn kitted survivor '{character.Alias}' ({kit.DisplayName}).");
            return null;
        }

        ApplyKit(npc, survivor, kitKey, armorTier);

        return character;
    }

    /// <summary>
    /// Kit-application logic extracted out of SpawnKitAt, so a caller with an
    /// already-spawned npc/survivor can apply the same armor/weapon/ammo/magazine
    /// kitting. Returns false only for an unknown kitKey.
    /// </summary>
    private bool ApplyKit(BasePlayer npc, Survivor survivor, string kitKey, KitArmorTier armorTier = KitArmorTier.High)
    {
        if (!SpawnKits.TryGetValue(kitKey, out WeaponKit kit))
        {
            return false;
        }

        // Explicit full-health guarantee for kitted test spawns only, independent of
        // SpawnSurvivor's own default, and clears wounded state too.
        npc.InitializeHealth(npc.MaxHealth(), npc.MaxHealth());

        if (npc.IsWounded())
        {
            npc.StopWounded();
        }

        // Base clothing layer, worn under the tier-specific armor for every tier.
        EquipKitArmor(npc, KitHoodieShortname);
        EquipKitArmor(npc, KitPantsShortname);
        EquipKitArmor(npc, KitBootsShortname);

        switch (armorTier)
        {
            case KitArmorTier.Medium:
                EquipKitArmor(npc, KitRoadsignJacketShortname);
                EquipKitArmor(npc, KitRoadsignGlovesShortname);
                EquipKitArmor(npc, KitRoadsignKiltShortname);
                EquipKitArmor(npc, KitCoffeeCanHelmetShortname);
                break;
            case KitArmorTier.Low:
                EquipKitArmor(npc, KitWoodJacketShortname);
                EquipKitArmor(npc, KitWoodPantsShortname);
                EquipKitArmor(npc, KitWoodHelmetShortname);
                break;
            default:
                EquipKitArmor(npc, KitFacemaskShortname);
                EquipKitArmor(npc, KitChestplateShortname);
                EquipKitArmor(npc, KitRoadsignKiltShortname);
                break;
        }

        GiveItem(npc, kit.WeaponShortname, 1);

        ItemDefinition ammoDefinition = ItemManager.FindItemDefinition(kit.AmmoShortname);
        int ammoStackSize = ammoDefinition != null ? ammoDefinition.stackable : 128;
        GiveItem(npc, kit.AmmoShortname, ammoStackSize);
        GiveItem(npc, kit.AmmoShortname, ammoStackSize);

        GiveItem(npc, KitSyringeShortname, KitSyringeCount);
        GiveItem(npc, KitBandageShortname, KitBandageCount);

        EquipBestWeaponForDisplay(survivor);

        // Tops the magazine off using ServerTryReload, the same inventory-aware
        // reload method used for mid-fight reloads, rather than a hardcoded
        // per-weapon capacity table.
        if (npc.GetHeldEntity() is BaseProjectile spawnedWeapon)
        {
            spawnedWeapon.ServerTryReload(npc.inventory);
        }

        return true;
    }

    /// <summary>
    /// Gather-tool test spawns, separate from SpawnKitAt's weapon-kit machinery
    /// (no ammo/magazine, no combat armor tier). Jackhammer is a powered tool that
    /// needs low-grade fuel to run, so fuel is included alongside it. The dual kit
    /// gives both a pickaxe and a hatchet, letting a bot gather either resource
    /// without a return trip.
    /// </summary>
    private enum GatherToolKit
    {
        Jackhammer,
        PickaxeAndHatchet,
    }

    private const string JackhammerShortname = "jackhammer";
    private const string JackhammerFuelShortname = "lowgradefuel";
    private const int JackhammerFuelCount = 100;

    [ChatCommand("lr.spawn.jackhammer")]
    private void CmdSpawnJackhammer(BasePlayer player, string command, string[] args) => RunSpawnGatherToolKit(player, GatherToolKit.Jackhammer);

    [ConsoleCommand("lr.spawn.jackhammer")]
    private void CmdSpawnJackhammerConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunSpawnGatherToolKit(player, GatherToolKit.Jackhammer);
        }
    }

    [ChatCommand("lr.spawn.gathertools")]
    private void CmdSpawnGatherTools(BasePlayer player, string command, string[] args) => RunSpawnGatherToolKit(player, GatherToolKit.PickaxeAndHatchet);

    [ConsoleCommand("lr.spawn.gathertools")]
    private void CmdSpawnGatherToolsConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunSpawnGatherToolKit(player, GatherToolKit.PickaxeAndHatchet);
        }
    }

    private void RunSpawnGatherToolKit(BasePlayer player, GatherToolKit kit)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        Vector3 spawnPosition = FindSpawnAimPoint(player, out bool aimedSpawn);
        Character character = _engine.CharacterManager.CreateInitialSurvivor();
        Survivor survivor = _engine.SurvivorManager.Create(character);

        BasePlayer npc = SpawnSurvivor(survivor, spawnPosition, player.transform.rotation);

        if (npc == null)
        {
            player.ChatMessage("[LivingRust] Failed to spawn survivor.");
            Puts($"ERROR: Failed to spawn gather-tool-kitted survivor '{character.Alias}'.");
            return;
        }

        npc.InitializeHealth(npc.MaxHealth(), npc.MaxHealth());

        if (npc.IsWounded())
        {
            npc.StopWounded();
        }

        string kitLabel;
        string activeToolShortname;

        if (kit == GatherToolKit.Jackhammer)
        {
            GiveItem(npc, JackhammerShortname, 1);
            GiveItem(npc, JackhammerFuelShortname, JackhammerFuelCount);
            kitLabel = "Jackhammer";
            activeToolShortname = JackhammerShortname;
        }
        else
        {
            GiveItem(npc, "hatchet", 1);
            GiveItem(npc, "pickaxe", 1);
            kitLabel = "Pickaxe + Hatchet";
            activeToolShortname = "pickaxe";
        }

        // Sets the active item directly rather than going through
        // EquipBestWeaponForDisplay/EquipBestMeleeTool, since this only needs to
        // show one specific known-correct tool immediately.
        Item activeTool = npc.inventory.FindItemByItemName(activeToolShortname);

        if (activeTool != null)
        {
            npc.UpdateActiveItem(activeTool.uid);
            ForceRefreshHeldEntity(npc);
        }

        string where = aimedSpawn ? "where you're looking" : "near you (nothing solid in view)";
        player.ChatMessage($"[LivingRust] Spawned '{character.Alias}' {where} with a {kitLabel} kit. (ID {character.BotId})");
    }

    /// <summary>
    /// Base-building test spawn: a survivor holding everything needed to test
    /// placement/construction, including a hammer, building plan, tool cupboard,
    /// code lock, sheet metal door, and a large stock of raw stone/wood.
    /// </summary>
    private const string BuildingPlanShortname = "building.planner";
    private const string HammerShortname = "hammer";
    private const int BaseBuilderStoneCount = 5000;
    private const int BaseBuilderWoodCount = 3000;

    [ChatCommand("lr.spawn.basebuilder")]
    private void CmdSpawnBaseBuilder(BasePlayer player, string command, string[] args) => RunSpawnBaseBuilderKit(player);

    [ConsoleCommand("lr.spawn.basebuilder")]
    private void CmdSpawnBaseBuilderConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunSpawnBaseBuilderKit(player);
        }
    }

    private void RunSpawnBaseBuilderKit(BasePlayer player)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        Vector3 spawnPosition = FindSpawnAimPoint(player, out bool aimedSpawn);
        Character character = _engine.CharacterManager.CreateInitialSurvivor();
        Survivor survivor = _engine.SurvivorManager.Create(character);

        BasePlayer npc = SpawnSurvivor(survivor, spawnPosition, player.transform.rotation);

        if (npc == null)
        {
            player.ChatMessage("[LivingRust] Failed to spawn survivor.");
            Puts($"ERROR: Failed to spawn base-builder-kitted survivor '{character.Alias}'.");
            return;
        }

        npc.InitializeHealth(npc.MaxHealth(), npc.MaxHealth());

        if (npc.IsWounded())
        {
            npc.StopWounded();
        }

        GiveItem(npc, HammerShortname, 1);
        GiveItem(npc, BuildingPlanShortname, 1);
        GiveItem(npc, StoneShortname, BaseBuilderStoneCount);
        GiveItem(npc, WoodShortname, BaseBuilderWoodCount);
        GiveItem(npc, ToolCupboardShortname, 1);
        GiveItem(npc, CodeLockShortname, 1);
        GiveItem(npc, SheetMetalDoorShortname, 1);

        // Set directly rather than going through EquipBestWeaponForDisplay/
        // EquipBestMeleeTool, same reasoning as RunSpawnGatherToolKit.
        Item activeTool = npc.inventory.FindItemByItemName(HammerShortname);

        if (activeTool != null)
        {
            npc.UpdateActiveItem(activeTool.uid);
            ForceRefreshHeldEntity(npc);
        }

        string where = aimedSpawn ? "where you're looking" : "near you (nothing solid in view)";
        player.ChatMessage($"[LivingRust] Spawned '{character.Alias}' {where} with a Base Builder kit. (ID {character.BotId})");
    }

    // Spacing between each kitted bot in the grid below, wide enough to avoid bots
    // overlapping and getting stuck on navmesh gaps at spawn.
    private const float SpawnAllKitsSpacing = 15f;

    // How many bots per row before wrapping to the next row, keeping all kits within
    // a roughly square area instead of one long line.
    private const int SpawnAllKitsColumns = 5;

    [ChatCommand("lr.spawn.allkits")]
    private void CmdSpawnAllKits(BasePlayer player, string command, string[] args) => RunSpawnAllKits(player);

    [ConsoleCommand("lr.spawn.allkits")]
    private void CmdSpawnAllKitsConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunSpawnAllKits(player);
        }
    }

    /// <summary>
    /// Spawns one of every kit in SpawnKits, laid out in a grid in front of the
    /// caller, centered on the caller's facing direction and spaced
    /// SpawnAllKitsSpacing apart. Each position's height comes from the terrain
    /// heightmap rather than a precise per-point ground probe.
    /// </summary>
    private void RunSpawnAllKits(BasePlayer player)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        Vector3 origin = FindSpawnAimPoint(player, out _);
        Quaternion rotation = player.transform.rotation;

        Vector3 forward = player.eyes.BodyForward();
        forward.y = 0f;
        forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;

        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

        int spawned = 0;
        int index = 0;

        foreach (string kitKey in SpawnKits.Keys)
        {
            int col = index % SpawnAllKitsColumns;
            int row = index / SpawnAllKitsColumns;

            float colOffset = (col - (SpawnAllKitsColumns - 1) / 2f) * SpawnAllKitsSpacing;
            float rowOffset = row * SpawnAllKitsSpacing;

            Vector3 position = origin + right * colOffset + forward * rowOffset;
            position.y = TerrainMeta.HeightMap.GetHeight(position);

            if (SpawnKitAt(player, kitKey, position, rotation) != null)
            {
                spawned++;
            }

            index++;
        }

        player.ChatMessage($"[LivingRust] Spawned {spawned}/{SpawnKits.Count} kitted survivors in front of you, {SpawnAllKitsSpacing:F0}m apart.");
    }

    /// <summary>
    /// Equips an item straight into containerWear so it is actually worn, using the
    /// same MoveToContainer pattern as the loot task's auto-equip logic.
    /// </summary>
    private void EquipKitArmor(BasePlayer npc, string shortname)
    {
        Item item = ItemManager.CreateByName(shortname, 1);

        if (item == null)
        {
            Puts($"WARNING: couldn't create kit armor item '{shortname}' - unknown shortname?");
            return;
        }

        if (!item.MoveToContainer(npc.inventory.containerWear) && !npc.inventory.GiveItem(item))
        {
            item.Remove();
        }
    }
}
