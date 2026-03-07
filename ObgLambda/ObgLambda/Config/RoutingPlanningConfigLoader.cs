using System.Text.Json;
using Amazon.Lambda.Core;

namespace ObgLambda.Config;

public sealed class RoutingPlanningConfig
{
    public int RoutePlanningDays { get; set; } = 7;
}

public static class RoutingPlanningConfigLoader
{
    private const string DefaultConfigRelativePath = "config/routing-planning-config.json";

    public static RoutingPlanningConfig Load(ILambdaLogger? logger = null)
    {
        try
        {
            var root = ResolveProjectRoot();
            var path = Path.Combine(root, DefaultConfigRelativePath);

            if (!File.Exists(path))
            {
                logger?.LogLine($"[CONFIG] routing config not found, using defaults: {path}");
                return new RoutingPlanningConfig();
            }

            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<RoutingPlanningConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new RoutingPlanningConfig();

            if (config.RoutePlanningDays <= 0)
                config.RoutePlanningDays = 1;

            logger?.LogLine($"[CONFIG] RoutePlanningDays={config.RoutePlanningDays}");
            return config;
        }
        catch (Exception ex)
        {
            logger?.LogLine($"[CONFIG] failed to load routing config, using defaults. {ex.Message}");
            return new RoutingPlanningConfig();
        }
    }

    private static string ResolveProjectRoot()
    {
        var cur = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (cur != null)
        {
            var input = Path.Combine(cur.FullName, "input");
            var lambda = Path.Combine(cur.FullName, "ObgLambda");
            var services = Path.Combine(cur.FullName, "ObgServices");

            if (Directory.Exists(lambda) && Directory.Exists(services))
                return cur.FullName;

            cur = cur.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
