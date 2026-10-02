using System;
using System.Reflection;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Rust's October 1, 2026 "Livestock Update" locked down several fields/methods this
/// project already depended on - made them internal/protected instead of public, with
/// no public replacement (confirmed one by one via decompiling the updated
/// Assembly-CSharp.dll, not guessed). Reflection is the only way left to reach the
/// handful listed here; everywhere else a real public alternative was found and used
/// directly instead (e.g. HeldEntity.GetOwnerItem() -> HeldEntity.GetItem(),
/// ResourceEntity.resourceDispenser -> GetComponent&lt;ResourceDispenser&gt;()). Every
/// accessor here is cached (reflection lookups are comparatively slow and these run on
/// hot paths like aiming/wheel-turning) and documented with exactly which member it
/// stands in for, so a future Rust update that actually REMOVES one of these (rather
/// than just hiding it) fails loudly and locally instead of silently doing nothing.
/// </summary>
public partial class LivingRust
{
    private const BindingFlags InstanceAnyVisibility = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // BasePlayer.tickViewAngles - public getter, "private set" as of this update.
    // Combat/recycling-guard aiming writes this directly because the weapon-aim IK
    // reads tickViewAngles specifically, not the plain (still-settable-via-
    // OverrideViewAngles) viewAngles field.
    private static readonly PropertyInfo TickViewAnglesProperty =
        typeof(BasePlayer).GetProperty("tickViewAngles", InstanceAnyVisibility);

    private static void SetTickViewAngles(BasePlayer npc, Vector3 angles)
    {
        TickViewAnglesProperty?.SetValue(npc, angles);
    }

    // WheelSwitch.rotatorPlayer/.progressTickRate - both now private. rotatorPlayer
    // still needs to be set so the real WheelSwitch.RotateProgress() (still public -
    // left untouched, it does its own power-draw accounting we don't want to
    // reimplement) passes its own "am I being turned by someone valid" guard.
    private static readonly FieldInfo WheelRotatorPlayerField =
        typeof(WheelSwitch).GetField("rotatorPlayer", InstanceAnyVisibility);

    private static readonly FieldInfo WheelProgressTickRateField =
        typeof(WheelSwitch).GetField("progressTickRate", InstanceAnyVisibility);

    private static void SetWheelRotatorPlayer(WheelSwitch wheel, BasePlayer player)
    {
        WheelRotatorPlayerField?.SetValue(wheel, player);
    }

    private static BasePlayer GetWheelRotatorPlayer(WheelSwitch wheel)
    {
        return WheelRotatorPlayerField?.GetValue(wheel) as BasePlayer;
    }

    private static float GetWheelProgressTickRate(WheelSwitch wheel)
    {
        return WheelProgressTickRateField != null ? (float)WheelProgressTickRateField.GetValue(wheel) : 0.1f;
    }

    // ElevatorLift.ownerElevator - private EntityRef<Elevator> field; EntityRef<T>
    // itself (and its public Get(bool)) is untouched, only the field holding it on
    // ElevatorLift was hidden.
    private static readonly FieldInfo ElevatorLiftOwnerField =
        typeof(ElevatorLift).GetField("ownerElevator", InstanceAnyVisibility);

    private static Elevator GetElevatorLiftMover(ElevatorLift lift)
    {
        if (ElevatorLiftOwnerField?.GetValue(lift) is EntityRef<Elevator> ownerRef)
        {
            return ownerRef.Get(false);
        }

        return null;
    }

    // ElevatorStatic.ownerElevator - private ElevatorStatic field (a per-floor call
    // point redirecting to whichever ElevatorStatic actually owns the shared cabin).
    private static readonly FieldInfo ElevatorStaticOwnerField =
        typeof(ElevatorStatic).GetField("ownerElevator", InstanceAnyVisibility);

    private static ElevatorStatic GetElevatorStaticOwner(ElevatorStatic elevatorStatic)
    {
        return ElevatorStaticOwnerField?.GetValue(elevatorStatic) as ElevatorStatic;
    }

    // ItemBasedFlowRestrictor.inventory - private ItemContainer field (read-only use:
    // checking what's sitting in the restrictor's single item slot).
    private static readonly FieldInfo FlowRestrictorInventoryField =
        typeof(ItemBasedFlowRestrictor).GetField("inventory", InstanceAnyVisibility);

    private static ItemContainer GetFlowRestrictorInventory(ItemBasedFlowRestrictor restrictor)
    {
        return FlowRestrictorInventoryField?.GetValue(restrictor) as ItemContainer;
    }

    // CargoPlane.dropPosition/startPos/endPos/secondsToTake/secondsTaken - all five
    // became private in this update. Confirmed via decompile that CargoPlane's own
    // logic (Update(), Save()/Load()) is otherwise completely unchanged - this is the
    // same drop-position/flight-timing math as before, just no longer reachable
    // without reflection. Read-only use (the airdrop heads-up timer needs to know
    // where/when the plane will drop its crate), so only getters are needed.
    private static readonly FieldInfo CargoPlaneDropPositionField =
        typeof(CargoPlane).GetField("dropPosition", InstanceAnyVisibility);

    private static readonly FieldInfo CargoPlaneStartPosField =
        typeof(CargoPlane).GetField("startPos", InstanceAnyVisibility);

    private static readonly FieldInfo CargoPlaneEndPosField =
        typeof(CargoPlane).GetField("endPos", InstanceAnyVisibility);

    private static readonly FieldInfo CargoPlaneSecondsToTakeField =
        typeof(CargoPlane).GetField("secondsToTake", InstanceAnyVisibility);

    private static readonly FieldInfo CargoPlaneSecondsTakenField =
        typeof(CargoPlane).GetField("secondsTaken", InstanceAnyVisibility);

    private static Vector3 GetCargoPlaneDropPosition(CargoPlane plane) =>
        CargoPlaneDropPositionField != null ? (Vector3)CargoPlaneDropPositionField.GetValue(plane) : Vector3.zero;

    private static Vector3 GetCargoPlaneStartPos(CargoPlane plane) =>
        CargoPlaneStartPosField != null ? (Vector3)CargoPlaneStartPosField.GetValue(plane) : Vector3.zero;

    private static Vector3 GetCargoPlaneEndPos(CargoPlane plane) =>
        CargoPlaneEndPosField != null ? (Vector3)CargoPlaneEndPosField.GetValue(plane) : Vector3.zero;

    private static float GetCargoPlaneSecondsToTake(CargoPlane plane) =>
        CargoPlaneSecondsToTakeField != null ? (float)CargoPlaneSecondsToTakeField.GetValue(plane) : 0f;

    private static float GetCargoPlaneSecondsTaken(CargoPlane plane) =>
        CargoPlaneSecondsTakenField != null ? (float)CargoPlaneSecondsTakenField.GetValue(plane) : 0f;

    // PlayerBlueprints.Reset() - now internal; only used from an admin/debug command
    // to reset a test survivor's unlocked-blueprint state.
    private static readonly MethodInfo PlayerBlueprintsResetMethod =
        typeof(PlayerBlueprints).GetMethod("Reset", InstanceAnyVisibility, null, Type.EmptyTypes, null);

    private static void ResetPlayerBlueprints(PlayerBlueprints blueprints)
    {
        if (blueprints == null)
        {
            return;
        }

        PlayerBlueprintsResetMethod?.Invoke(blueprints, null);
    }
}
