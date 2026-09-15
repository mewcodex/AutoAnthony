using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace AutoAnthonyCardTinkering;

/// <summary>
/// Inserts one ordinary question-mark Training Room immediately before the first Boss. Boss points remain the
/// native special objects: only their row is moved, so their scene, icon, scale and horizontal placement are never
/// replaced by a synthetic map point.
/// </summary>
internal sealed class TrainingActMap : ActMap
{
    private readonly MapPoint?[,] _grid;

    public override MapPoint BossMapPoint { get; }
    public override MapPoint StartingMapPoint { get; }
    public override MapPoint? SecondBossMapPoint { get; }
    protected override MapPoint?[,] Grid => _grid;

    internal MapPoint TrainingMapPoint { get; }

    private TrainingActMap(ActMap source)
    {
        _grid = new MapPoint?[source.GetColumnCount(), source.GetRowCount() + 1];
        var clones = new Dictionary<MapPoint, MapPoint>();
        foreach (var point in source.GetAllMapPoints())
        {
            var clone = ClonePoint(point, point.coord);
            clones.Add(point, clone);
            _grid[clone.coord.col, clone.coord.row] = clone;
        }

        StartingMapPoint = ClonePoint(source.StartingMapPoint, source.StartingMapPoint.coord);
        BossMapPoint = ClonePoint(source.BossMapPoint,
            new MapCoord(source.BossMapPoint.coord.col, source.BossMapPoint.coord.row + 1));
        SecondBossMapPoint = source.SecondBossMapPoint is null ? null : ClonePoint(source.SecondBossMapPoint,
            new MapCoord(source.SecondBossMapPoint.coord.col, source.SecondBossMapPoint.coord.row + 1));

        TrainingMapPoint = new MapPoint(source.BossMapPoint.coord.col, source.BossMapPoint.coord.row)
        {
            PointType = MapPointType.Unknown,
            CanBeModified = false
        };
        _grid[TrainingMapPoint.coord.col, TrainingMapPoint.coord.row] = TrainingMapPoint;

        foreach (var child in source.StartingMapPoint.Children)
            if (clones.TryGetValue(child, out var clone)) StartingMapPoint.AddChildPoint(clone);

        foreach (var point in source.GetAllMapPoints())
        {
            var clone = clones[point];
            foreach (var child in point.Children)
            {
                if (ReferenceEquals(child, source.BossMapPoint)) clone.AddChildPoint(TrainingMapPoint);
                else if (clones.TryGetValue(child, out var childClone)) clone.AddChildPoint(childClone);
            }
        }
        TrainingMapPoint.AddChildPoint(BossMapPoint);
        if (SecondBossMapPoint is not null) BossMapPoint.AddChildPoint(SecondBossMapPoint);

        foreach (var point in source.startMapPoints)
            if (clones.TryGetValue(point, out var clone)) startMapPoints.Add(clone);
    }

    private static MapPoint ClonePoint(MapPoint point, MapCoord coord)
    {
        var clone = new MapPoint(coord.col, coord.row)
        {
            PointType = point.PointType,
            CanBeModified = point.CanBeModified
        };
        foreach (var quest in point.Quests) clone.AddQuest(quest);
        return clone;
    }

    internal static bool TryCreate(ActMap source, out ActMap result)
    {
        result = source;
        if (IsTrainingMap(source) || IsLegacyBeforeAncientMap(source) || IsLegacyPostAncientMap(source))
            return false;
        if (source.StartingMapPoint.PointType != MapPointType.Ancient
            || source.StartingMapPoint.coord.row != 0
            || source.BossMapPoint.PointType != MapPointType.Boss) return false;
        result = new TrainingActMap(source);
        return true;
    }

    internal static bool IsTrainingMap(ActMap map) => TryGetTrainingPoint(map, out _);

    internal static bool TryGetTrainingPoint(ActMap map, out MapPoint point)
    {
        if (map is TrainingActMap training)
        {
            point = training.TrainingMapPoint;
            return true;
        }

        // Saved maps lose their wrapper type. Identify the single question-mark point directly before the native
        // Boss by topology. CanBeModified is deliberately not part of the signature: the game resets that flag on
        // visited/restored map points, which previously made an in-progress Training Room look ordinary after load
        // and caused this mod to append a second room in front of the Boss.
        var candidate = map.GetAllMapPoints().FirstOrDefault(item =>
            item.PointType == MapPointType.Unknown
            && item.coord.row == map.BossMapPoint.coord.row - 1
            && map.GetPointsInRow(item.coord.row).Take(2).Count() == 1
            && item.Children.Any(child => child.coord == map.BossMapPoint.coord));
        if (candidate is not null)
        {
            point = candidate;
            return true;
        }
        if (TryGetLegacyPostAncientPoint(map, out candidate) || TryGetLegacyBeforeAncientPoint(map, out candidate))
        {
            point = candidate;
            return true;
        }
        point = null!;
        return false;
    }

    internal static bool IsLegacyBeforeAncientMap(ActMap map) =>
        TryGetLegacyBeforeAncientPoint(map, out _);

    private static bool TryGetLegacyBeforeAncientPoint(ActMap map, out MapPoint point)
    {
        point = map.StartingMapPoint;
        return point.PointType == MapPointType.Unknown
            && point.coord.row == 0
            && point.Children.Count == 1
            && point.Children.Single().PointType == MapPointType.Ancient
            && point.Children.Single().coord.row == 1;
    }

    private static bool IsLegacyPostAncientMap(ActMap map) => TryGetLegacyPostAncientPoint(map, out _);

    private static bool TryGetLegacyPostAncientPoint(ActMap map, out MapPoint point)
    {
        point = null!;
        if (map.StartingMapPoint.PointType != MapPointType.Ancient || map.StartingMapPoint.coord.row != 0
            || map.StartingMapPoint.Children.Count != 1) return false;
        var candidate = map.StartingMapPoint.Children.Single();
        if (candidate.PointType != MapPointType.Unknown || candidate.coord.row != 1)
            return false;
        point = candidate;
        return true;
    }

    internal static bool IsTrainingPoint(ActMap map, MapPoint? point)
    {
        if (point is null || !TryGetTrainingPoint(map, out var training)) return false;
        return point.coord == training.coord;
    }

    internal static bool IsTrainingPoint(SerializableActMap map, MapCoord? coord)
    {
        if (coord is null) return false;
        var boss = map.BossPoint.Coord;
        var candidate = map.Points.FirstOrDefault(item =>
            item.PointType == MapPointType.Unknown
            && item.Coord.row == boss.row - 1
            && map.Points.Count(other => other.Coord.row == item.Coord.row) == 1
            && item.ChildCoords?.Contains(boss) == true);
        return candidate is not null && candidate.Coord == coord.Value;
    }
}
