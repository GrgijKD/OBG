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
            var root = Function.ResolveProjectRoot();
            var path = Path.Combine(root, "config", "routing-planning-config.json");

            if (!File.Exists(path))
            {
                logger.LogLine($"[CONFIG] routing-planning-config.json not found, fallback={DefaultRoutePlanningDays}");
                return DefaultRoutePlanningDays;
            }

            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<RoutingPlanningConfig>(json);

            if (config is null || config.RoutePlanningDays <= 0)
            {
                logger.LogLine($"[CONFIG] invalid RoutePlanningDays, fallback={DefaultRoutePlanningDays}");
                return DefaultRoutePlanningDays;
            }

            return config.RoutePlanningDays;
        }
        catch (Exception ex)
        {
            logger.LogLine($"[CONFIG] failed to load routing config, fallback={DefaultRoutePlanningDays}. {ex.Message}");
            return DefaultRoutePlanningDays;
        }
    }

    private sealed class RoutingPlanningConfig
    {
        public int RoutePlanningDays { get; set; }
    }
}
