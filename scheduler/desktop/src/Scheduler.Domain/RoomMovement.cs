using System.Collections.Frozen;

namespace Scheduler.Domain;

public static class RoomMovement
{
    public const int SameRoomCost = 0;
    public const int SameBuildingCost = 1;
    public const int SameZoneCost = 2;
    public const int CrossZoneCost = 3;

    private static readonly FrozenDictionary<string, string> ZoneByBuilding =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["A"] = "GD4",
            ["B"] = "GD4",
            ["T"] = "GD3",
            ["GĐ3"] = "GD3",
            ["G2"] = "GD2",
            ["G3"] = "GD2",
            ["E3"] = "GD2",
            ["E5"] = "GD2",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static string MovementZoneForBuilding(string building)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(building);

        return building.Contains("ĐHKHTN", StringComparison.Ordinal)
            ? "ĐHKHTN"
            : ZoneByBuilding.GetValueOrDefault(building, building);
    }

    public static int TransitionCost(
        string fromRoomCode,
        string fromBuilding,
        string toRoomCode,
        string toBuilding)
    {
        if (fromRoomCode == toRoomCode)
        {
            return SameRoomCost;
        }

        if (fromBuilding == toBuilding)
        {
            return SameBuildingCost;
        }

        return MovementZoneForBuilding(fromBuilding) == MovementZoneForBuilding(toBuilding)
            ? SameZoneCost
            : CrossZoneCost;
    }
}
