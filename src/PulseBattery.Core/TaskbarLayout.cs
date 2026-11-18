namespace PulseBattery.Core;

public readonly record struct TaskbarSpan(int Left, int Right);
public readonly record struct TaskbarPlacement(bool Fits, int Left, int Width);
public enum TaskbarDisplayMode { Horizontal, Stacked, Hidden }
public readonly record struct TaskbarLayoutChoice(TaskbarDisplayMode Mode, TaskbarPlacement Placement);

public static class TaskbarLayout
{
    public static TaskbarLayoutChoice Choose(int buttonsRight, int trayLeft, int horizontalWidth, int stackedWidth,
        int gap, IReadOnlyList<TaskbarSpan> obstacles, TaskbarLayoutPreference preference = TaskbarLayoutPreference.Horizontal)
    {
        var horizontal = Place(buttonsRight, trayLeft, horizontalWidth, gap, obstacles);
        if (preference != TaskbarLayoutPreference.Compact && horizontal.Fits) return new(TaskbarDisplayMode.Horizontal, horizontal);
        var stacked = Place(buttonsRight, trayLeft, stackedWidth, gap, obstacles);
        return stacked.Fits ? new(TaskbarDisplayMode.Stacked, stacked) : new(TaskbarDisplayMode.Hidden, stacked);
    }
    // Reserve a gap on both sides without borrowing space from task buttons.
    public static TaskbarPlacement Place(int buttonsRight, int trayLeft, int width, int gap,
        IEnumerable<TaskbarSpan> obstacles)
    {
        if (width <= 0 || gap < 0) return new(false, 0, width);
        long right = (long)trayLeft - gap;
        var minimumLeft = (long)buttonsRight + gap;
        foreach (var obstacle in obstacles.Where(span => span.Right > span.Left).OrderByDescending(span => span.Right))
        {
            if (obstacle.Left >= right + gap) continue;
            if ((long)obstacle.Right + gap <= right - width) break;
            right = Math.Min(right, (long)obstacle.Left - gap);
        }
        var left = right - width;
        return left >= minimumLeft && left >= int.MinValue && left <= int.MaxValue
            ? new(true, (int)left, width) : new(false, 0, width);
    }
}
