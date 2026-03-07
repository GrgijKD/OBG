using Amazon.Lambda.Core;
using System.Text.Json;

namespace ObgLambda;

public static class RoutingPlanningConfigLoader
{
    private const int DefaultRoutePlanningDays = 7;

    public static int LoadRoutePlanningDays(ILambdaLogger logger)
    {
        try
        {
            var envValue = Environment.GetEnvironmentVariable("OBG_ROUTE_PLANNING_DAYS");
            if (int.TryParse(envValue, out var envDays) && envDays > 0)
            {
                logger.LogLine($"[CONFIG] RoutePlanningDays={envDays} (source=ENV:OBG_ROUTE_PLANNING_DAYS)");
                return envDays;
            }

            var explicitConfigDir = Environment.GetEnvironmentVariable("OBG_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(explicitConfigDir))
            {
                var explicitPath = Path.Combine(explicitConfigDir, "routing-planning-config.json");
                if (TryLoadFromFile(explicitPath, logger, out var explicitDays))
                    return explicitDays;
            }

            foreach (var path in EnumerateCandidatePaths())
            {
                if (TryLoadFromFile(path, logger, out var days))
                    return days;
            }

            logger.LogLine($"[CONFIG] routing-planning-config.json not found, fallback={DefaultRoutePlanningDays}");
            return DefaultRoutePlanningDays;
        }
        catch (Exception ex)
        {
            logger.LogLine($"[CONFIG] failed to load routing config, fallback={DefaultRoutePlanningDays}. {ex.Message}");
            return DefaultRoutePlanningDays;
        }
    }

    private static IEnumerable<string> EnumerateCandidatePaths()
    {
        var bases = new List<string>();

        void AddBase(string? dir)
        {
            if (!string.IsNullOrWhiteSpace(dir))
                bases.Add(Path.GetFullPath(dir));
        }

        AddBase(AppContext.BaseDirectory);
        AddBase(Directory.GetCurrentDirectory());

        try
        {
            AddBase(Function.ResolveProjectRoot());
        }
        catch
        {
        }

        foreach (var baseDir in bases.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var current in WalkUp(baseDir))
            {
                yield return Path.Combine(current, "config", "routing-planning-config.json");
                yield return Path.Combine(current, "routing-planning-config.json");
            }
        }
    }

    private static IEnumerable<string> WalkUp(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            yield return dir.FullName;
            dir = dir.Parent;
        }
    }

    private static bool TryLoadFromFile(string path, ILambdaLogger logger, out int routePlanningDays)
    {
        routePlanningDays = DefaultRoutePlanningDays;

        if (!File.Exists(path))
            return false;

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<RoutingPlanningConfig>(json);

        if (config is null || config.RoutePlanningDays <= 0)
        {
            logger.LogLine($"[CONFIG] invalid RoutePlanningDays in '{path}', fallback={DefaultRoutePlanningDays}");
            routePlanningDays = DefaultRoutePlanningDays;
            return true;
        }

        routePlanningDays = config.RoutePlanningDays;
        logger.LogLine($"[CONFIG] RoutePlanningDays={routePlanningDays} (source={path})");
        return true;
    }

    private sealed class RoutingPlanningConfig
    {
        public int RoutePlanningDays { get; set; }
    }
}
