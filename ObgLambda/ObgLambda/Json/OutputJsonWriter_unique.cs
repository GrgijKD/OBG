using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using ObgServices.Models;

namespace ObgLambda.Json
{
    /// <summary>
    /// Writes schedule JSON to <OBG_ROOT>/output/output.json.
    /// OBG root is discovered by walking up from Directory.GetCurrentDirectory()
    /// and looking for repo markers (input + ObgLambda + ObgServices folders).
    /// </summary>
    public static class OutputJsonWriter
    {
        private const string DefaultOutputFileName = "output.json";
        private const string DefaultTimeZone = "Europe/Kyiv";
        private const int DefaultHorizonDays = 168;

        // -----------------------------
        // DTOs that match requested JSON template
        // -----------------------------
        public sealed record Meta(string timeZone, int horizonDays);

        public sealed record TechnicianDirectoryItem(string techId, string name);

        public sealed record ServiceDirectoryItem(string serviceId, string name);

        public sealed record Visit(
            string type,
            string? serviceId,
            string arrival,
            int durationMin
        );

        public sealed record TechnicianDay(string techId, List<Visit> visits);

        public sealed record Day(string date, List<TechnicianDay> technicians);

        public sealed record OutputRoot(
            Meta meta,
            List<TechnicianDirectoryItem> technicianDirectory,
            List<ServiceDirectoryItem> serviceDirectory,
            List<Day> days
        );

        /// <summary>
        /// Writes JSON to <OBG_ROOT>/output/output.json.
        /// Optional: set env var OBG_OUTPUT_DIR or pass outputDirOverride.
        /// </summary>
        public static void TryWriteScheduleJson(
            IEnumerable<OptimizedRoute> routes,
            ILambdaLogger? logger = null,
            string? outputDirOverride = null
        )
        {
            try
            {
                var outputDir = ResolveOutputDir(outputDirOverride);
                Directory.CreateDirectory(outputDir);

                var outPath = Path.Combine(outputDir, DefaultOutputFileName);

                var model = BuildOutput(routes);

                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                File.WriteAllText(outPath, JsonSerializer.Serialize(model, jsonOptions));

                logger?.LogInformation($"[OutputJsonWriter] JSON записано: {outPath}");
            }
            catch (Exception ex)
            {
                logger?.LogError($"[OutputJsonWriter] Failed to write output json: {ex}");
            }
        }

        private static OutputRoot BuildOutput(IEnumerable<OptimizedRoute> routes)
{
    var routeList = routes?.ToList() ?? new List<OptimizedRoute>();

    // ---------------------------------------------------------
    // Technician IDs
    // ---------------------------------------------------------
    // TechnicianId in the current model may already be a stable unique ID.
    // But if duplicates appear (e.g. TechnicianId derived from name), we must avoid collisions in JSON.
    // Rule:
    //   - first occurrence keeps original id
    //   - next duplicates get suffix: "<id>-2", "<id>-3", ...
    //
    // IMPORTANT: we group/emit per ROUTE (per technician instance), not per TechnicianId,
    // so duplicated TechnicianId won't merge technicians together in "days".
    var routeWithIndex = routeList.Select((r, i) => (Route: r, Index: i)).ToList();

    var techIdCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var techIdByRouteIndex = new Dictionary<int, string>();

    foreach (var item in routeWithIndex)
    {
        var baseId = item.Route.TechnicianId;
        if (string.IsNullOrWhiteSpace(baseId))
            baseId = "tech";

        techIdCounts.TryGetValue(baseId, out var count);
        count++;
        techIdCounts[baseId] = count;

        techIdByRouteIndex[item.Index] = count == 1 ? baseId : $"{baseId}-{count}";
    }

    var technicianDirectory = routeWithIndex
        .Select(x =>
        {
            var name = string.IsNullOrWhiteSpace(x.Route.TechnicianName)
                ? x.Route.TechnicianId
                : x.Route.TechnicianName;

            return new TechnicianDirectoryItem(techIdByRouteIndex[x.Index], name);
        })
        .OrderBy(x => x.techId)
        .ToList();

    // ---------------------------------------------------------
    // Stops + Service directory
    // ---------------------------------------------------------
    var allStops = routeWithIndex
        .SelectMany(x => (x.Route.Stops ?? new List<RouteStop>())
            .Select(s => (RouteIndex: x.Index, Stop: s)))
        .ToList();

    // Build serviceDirectory from visited sites:
    // serviceId = SiteId, name = first non-empty SiteName for that SiteId
    var serviceDirectory = allStops
        .GroupBy(x => x.Stop.SiteId)
        .Select(g =>
        {
            var name = g.Select(s => s.Stop.SiteName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                       ?? g.Key;
            return new ServiceDirectoryItem(g.Key, name);
        })
        .OrderBy(x => x.serviceId)
        .ToList();

    // ---------------------------------------------------------
    // Days
    // ---------------------------------------------------------
    var days = allStops
        .GroupBy(x => x.Stop.ExpectedArrivalTime.Date)
        .OrderBy(g => g.Key)
        .Select(dayGroup =>
        {
            var dateStr = dayGroup.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            // group by RouteIndex to avoid merging duplicate TechnicianId routes
            var techsForDay = dayGroup
                .GroupBy(x => x.RouteIndex)
                .OrderBy(g => techIdByRouteIndex[g.Key])
                .Select(techGroup =>
                {
                    var visits = techGroup
                        .OrderBy(x => x.Stop.Sequence)
                        .ThenBy(x => x.Stop.ExpectedArrivalTime)
                        .Select(x =>
                        {
                            // For now, everything is "service".
                            // Later можна додати lunch/home/office коли з’являться ознаки в моделі.
                            var type = "service";

                            // serviceId == SiteId
                            var serviceId = x.Stop.SiteId;

                            var arrival = x.Stop.ExpectedArrivalTime.ToString("HH:mm", CultureInfo.InvariantCulture);

                            // No duration in RouteStop yet
                            var durationMin = 0;

                            return new Visit(type, serviceId, arrival, durationMin);
                        })
                        .ToList();

                    return new TechnicianDay(techIdByRouteIndex[techGroup.Key], visits);
                })
                .ToList();

            return new Day(dateStr, techsForDay);
        })
        .ToList();

    return new OutputRoot(
        new Meta(DefaultTimeZone, DefaultHorizonDays),
        technicianDirectory,
        serviceDirectory,
        days
    );
}

private static string ResolveOutputDir(string? outputDirOverride)
        {
            if (!string.IsNullOrWhiteSpace(outputDirOverride))
                return outputDirOverride!;

            var env = Environment.GetEnvironmentVariable("OBG_OUTPUT_DIR");
            if (!string.IsNullOrWhiteSpace(env))
                return env!;

            var root = ResolveObgRoot();
            return Path.Combine(root, "output");
        }

        /// <summary>
        /// Finds repo root folder "OBG" by walking up from current working directory.
        /// Works even when running from Amazon.Lambda.TestTool.
        /// </summary>
        private static string ResolveObgRoot()
        {
            var envRoot = Environment.GetEnvironmentVariable("OBG_ROOT_DIR");
            if (!string.IsNullOrWhiteSpace(envRoot) && Directory.Exists(envRoot))
                return envRoot!;

            var start = new DirectoryInfo(Directory.GetCurrentDirectory());
            var dir = start;

            while (dir != null)
            {
                // markers of your repo root
                var hasInput = Directory.Exists(Path.Combine(dir.FullName, "input"));
                var hasLambda = Directory.Exists(Path.Combine(dir.FullName, "ObgLambda"));
                var hasServices = Directory.Exists(Path.Combine(dir.FullName, "ObgServices"));

                if (hasInput && hasLambda && hasServices)
                    return dir.FullName;

                dir = dir.Parent;
            }

            // fallback: if not found, still prefer current working directory
            return start.FullName;
        }
    }
}
