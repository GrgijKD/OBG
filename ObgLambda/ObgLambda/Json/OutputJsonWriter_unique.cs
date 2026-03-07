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
    /// NOTE: This is presentation/integration layer only. It must NOT affect solver logic.
    /// </summary>
    public static class OutputJsonWriter
    {
        private const string DefaultOutputFileName = "output.json";
        private const string DefaultTimetableFileName = "timetable.json";
        private const string DefaultMasterCalendarFileName = "master-calendar.json";
        private const string DefaultTimeZone = "Europe/Kyiv"; // TODO: later derive from office coordinates

        // -----------------------------
        // DTOs for the agreed presentation JSON
        // -----------------------------
        public sealed record Meta(string timeZone, string scheduleStartDate, int horizonDays);

        public sealed record TechnicianDirectoryItem(string techId, string name);
        public sealed record ServiceDirectoryItem(string serviceId, string name);

        public sealed record StopDto(
            string type,
            string arrivalTime,
            int durationMinutes,
            string? address = null,
            string? serviceId = null
        );

        public sealed record RouteDto(string techId, List<StopDto> stops);

        public sealed record DayDto(string day, List<RouteDto> routes);

        public sealed record TechnicianWeekSummaryItem(string techId, int workedMinutes, double workedHours);

        public sealed record WeekDto(
            string weekStartDate,
            string weekEndDate,
            List<TechnicianWeekSummaryItem> technicianWeekSummary,
            List<DayDto> days
        );

        public sealed record OutputRoot(
            Meta meta,
            List<TechnicianDirectoryItem> technicianDirectory,
            List<ServiceDirectoryItem> serviceDirectory,
            List<WeekDto> weeks
        );

        public sealed record TimetableStopDto(
            string SiteId,
            string? SiteName,
            string ExpectedArrivalTime,
            int Sequence
        );

        public sealed record TimetableRouteDto(
            string TechnicianId,
            string TechnicianName,
            List<TimetableStopDto> Stops,
            double TotalDistanceKm,
            double TotalDurationMinutes
        );

        public sealed record TimetableDayDto(
            int Day,
            string Date,
            List<TimetableRouteDto> Routes
        );


        public sealed record MasterCalendarServiceDto(
            string SiteId,
            string? SiteName
        );

        public sealed record MasterCalendarDayDto(
            int Day,
            string Date,
            List<MasterCalendarServiceDto> Services
        );

        public sealed record MasterCalendarRootDto(
            string CycleStartDate,
            int HorizonDays,
            List<MasterCalendarDayDto> Days
        );

        // ------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------

        /// <summary>
        /// Backward-compatible overload.
        /// </summary>
        public static void TryWriteScheduleJson(IEnumerable<OptimizedRoute> routes, ILambdaLogger? logger = null)
            => TryWriteScheduleJson(routes, technicians: null, sites: null, logger);

        /// <summary>
        /// Primary overload used by Function.cs.
        /// </summary>
        public static void TryWriteScheduleJson(
            IEnumerable<OptimizedRoute> routes,
            IEnumerable<Technician>? technicians,
            IEnumerable<ServiceSite>? sites,
            ILambdaLogger? logger = null,
            string? outputDirOverride = null
        )
        {
            try
            {
                var outputDir = ResolveOutputDir(outputDirOverride);
                Directory.CreateDirectory(outputDir);

                var outPath = Path.Combine(outputDir, DefaultOutputFileName);

                var model = BuildOutput(routes, technicians, sites);

                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                File.WriteAllText(outPath, JsonSerializer.Serialize(model, jsonOptions));
                logger?.LogLine($"[EXPORT] JSON записано: {outPath}");
            }
            catch (Exception ex)
            {
                logger?.LogLine($"[EXPORT][ERROR] Failed to write output json: {ex.Message}");
            }
        }


        public static void TryWriteTimetableJson(
            IEnumerable<WeeklyRoute> weeklyRoutes,
            ILambdaLogger? logger = null,
            string? outputDirOverride = null
        )
        {
            try
            {
                var outputDir = ResolveTimetableDir(outputDirOverride);
                Directory.CreateDirectory(outputDir);

                var outPath = Path.Combine(outputDir, DefaultTimetableFileName);
                var model = BuildTimetableOutput(weeklyRoutes);

                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                File.WriteAllText(outPath, JsonSerializer.Serialize(model, jsonOptions));
                logger?.LogLine($"[EXPORT] Timetable JSON записано: {outPath}");
            }
            catch (Exception ex)
            {
                logger?.LogLine($"[EXPORT][ERROR] Failed to write timetable json: {ex.Message}");
            }
        }


        public static void TryWriteMasterCalendarJson(
            IReadOnlyDictionary<int, List<ServiceSite>> masterSchedule,
            DateTime cycleStartDate,
            int horizonDays,
            IEnumerable<ServiceSite>? allSites = null,
            ILambdaLogger? logger = null,
            string? outputDirOverride = null
        )
        {
            try
            {
                var outputDir = ResolveMasterCalendarDir(outputDirOverride);
                Directory.CreateDirectory(outputDir);

                var outPath = Path.Combine(outputDir, DefaultMasterCalendarFileName);
                var model = BuildMasterCalendarOutput(masterSchedule, cycleStartDate, horizonDays, allSites);

                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                File.WriteAllText(outPath, JsonSerializer.Serialize(model, jsonOptions));
                logger?.LogLine($"[EXPORT] Master calendar JSON записано: {outPath}");
            }
            catch (Exception ex)
            {
                logger?.LogLine($"[EXPORT][ERROR] Failed to write master calendar json: {ex.Message}");
            }
        }

        // ------------------------------------------------------------
        // Build
        // ------------------------------------------------------------

        private static OutputRoot BuildOutput(
            IEnumerable<OptimizedRoute> routes,
            IEnumerable<Technician>? technicians,
            IEnumerable<ServiceSite>? sites
        )
        {
            var routeList = (routes ?? Array.Empty<OptimizedRoute>()).ToList();
            var techList = (technicians ?? Array.Empty<Technician>()).ToList();
            var siteList = (sites ?? Array.Empty<ServiceSite>()).ToList();

            // Map internal IDs to presentation sequential IDs
            // tech: trch-1, trch-2, ... in TECH LIST order if provided; otherwise, routes order.
            var techIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var techDirectory = new List<TechnicianDirectoryItem>();

            IEnumerable<(string InternalId, string Name)> techSource;
            if (techList.Count > 0)
            {
                techSource = techList.Select(t => (t.Id, t.Name));
            }
            else
            {
                techSource = routeList.Select(r => (r.TechnicianId, string.IsNullOrWhiteSpace(r.TechnicianName) ? r.TechnicianId : r.TechnicianName));
            }

            var techSeq = 0;
            foreach (var t in techSource)
            {
                if (string.IsNullOrWhiteSpace(t.InternalId))
                    continue;

                if (techIdMap.ContainsKey(t.InternalId))
                    continue;

                techSeq++;
                var outId = $"trch-{techSeq}";
                techIdMap[t.InternalId] = outId;
                techDirectory.Add(new TechnicianDirectoryItem(outId, t.Name));
            }

            // sites: srv-1, srv-2, ... in SITE LIST order if provided; otherwise, first appearance in routes.
            var siteIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var serviceDirectory = new List<ServiceDirectoryItem>();

            IEnumerable<(string InternalId, string Name)> siteSource;
            if (siteList.Count > 0)
            {
                siteSource = siteList.Select(s => (s.Id, s.Name ?? s.Id));
            }
            else
            {
                siteSource = routeList
                    .SelectMany(r => r.Stops ?? new List<RouteStop>())
                    .Where(s => !string.IsNullOrWhiteSpace(s.SiteId))
                    .GroupBy(s => s.SiteId)
                    .Select(g => (g.Key, g.Select(x => x.SiteName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? g.Key));
            }

            var srvSeq = 0;
            foreach (var s in siteSource)
            {
                if (string.IsNullOrWhiteSpace(s.InternalId))
                    continue;

                if (siteIdMap.ContainsKey(s.InternalId))
                    continue;

                srvSeq++;
                var outId = $"srv-{srvSeq}";
                siteIdMap[s.InternalId] = outId;
                serviceDirectory.Add(new ServiceDirectoryItem(outId, s.Name));
            }

            // Schedule start date: next calendar Monday
            var scheduleStart = NextMonday(DateTime.Today);

            // Determine offset between solver dates and desired presentation dates.
            var minSolverDate = routeList
                .SelectMany(r => r.Stops ?? new List<RouteStop>())
                .Select(s => s.ExpectedArrivalTime.Date)
                .DefaultIfEmpty(scheduleStart.Date)
                .Min();

            var dayOffset = (scheduleStart.Date - minSolverDate).Days;

            // helper maps
            var techById = techList.ToDictionary(t => t.Id, t => t, StringComparer.OrdinalIgnoreCase);
            var siteById = siteList.ToDictionary(s => s.Id, s => s, StringComparer.OrdinalIgnoreCase);

            // Collect all stop records with shifted dates
            var stopRecs = new List<(string TechInternalId, DateTime Arrival, RouteStop Stop)>();
            foreach (var r in routeList)
            {
                foreach (var st in (r.Stops ?? new List<RouteStop>()))
                {
                    var shiftedArrival = st.ExpectedArrivalTime.AddDays(dayOffset);
                    stopRecs.Add((r.TechnicianId, shiftedArrival, new RouteStop
                    {
                        SiteId = st.SiteId,
                        SiteName = st.SiteName,
                        ExpectedArrivalTime = shiftedArrival,
                        Sequence = st.Sequence
                    }));
                }
            }

            // horizonDays based on service intervals
            var intervals = siteList.Select(s => s.VisitIntervalDays).Where(d => d > 0).ToList();
            var horizonDays = ComputeHorizonDays(intervals);

            // Build days => weeks
            var dayGroups = stopRecs
                .GroupBy(x => x.Arrival.Date)
                .OrderBy(g => g.Key)
                .ToList();

            var dayDtos = new List<DayDto>();
            foreach (var dg in dayGroups)
            {
                var dayStr = dg.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                var routesForDay = dg
                    .GroupBy(x => x.TechInternalId, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => techIdMap.TryGetValue(g.Key, out var outId) ? outId : g.Key)
                    .Select(techGroup =>
                    {
                        var techInternalId = techGroup.Key;
                        var outTechId = techIdMap.TryGetValue(techInternalId, out var tid) ? tid : techInternalId;

                        var orderedStops = techGroup
                            .OrderBy(x => x.Stop.Sequence)
                            .ThenBy(x => x.Stop.ExpectedArrivalTime)
                            .Select(x => x.Stop)
                            .ToList();

                        var stopsOut = BuildStopsForRoute(orderedStops, techInternalId, techById, siteById, siteIdMap);

                        return new RouteDto(outTechId, stopsOut);
                    })
                    .ToList();

                dayDtos.Add(new DayDto(dayStr, routesForDay));
            }

            // Weeks grouping
            var weeks = BuildWeeks(dayDtos);

            return new OutputRoot(
                meta: new Meta(DefaultTimeZone, scheduleStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), horizonDays),
                technicianDirectory: techDirectory.OrderBy(t => t.techId).ToList(),
                serviceDirectory: serviceDirectory.OrderBy(s => s.serviceId).ToList(),
                weeks: weeks
            );
        }

        private static MasterCalendarRootDto BuildMasterCalendarOutput(
            IReadOnlyDictionary<int, List<ServiceSite>> masterSchedule,
            DateTime cycleStartDate,
            int horizonDays,
            IEnumerable<ServiceSite>? allSites)
        {
            var safeHorizon = Math.Max(horizonDays, 0);
            var days = new List<MasterCalendarDayDto>(safeHorizon);

            var siteIdMap = BuildServiceIdMapForPresentation(allSites, masterSchedule);

            for (var dayIndex = 0; dayIndex < safeHorizon; dayIndex++)
            {
                masterSchedule.TryGetValue(dayIndex, out var servicesForDay);
                var services = (servicesForDay ?? new List<ServiceSite>())
                    .OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(site => new MasterCalendarServiceDto(
                        SiteId: siteIdMap.TryGetValue(site.Id, out var mappedId) ? mappedId : site.Id,
                        SiteName: site.Name
                    ))
                    .ToList();

                days.Add(new MasterCalendarDayDto(
                    Day: dayIndex + 1,
                    Date: cycleStartDate.Date.AddDays(dayIndex).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Services: services
                ));
            }

            return new MasterCalendarRootDto(
                CycleStartDate: cycleStartDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                HorizonDays: safeHorizon,
                Days: days
            );
        }


        private static Dictionary<string, string> BuildServiceIdMapForPresentation(
            IEnumerable<ServiceSite>? allSites,
            IReadOnlyDictionary<int, List<ServiceSite>> masterSchedule)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            IEnumerable<ServiceSite> source;
            if (allSites != null && allSites.Any())
            {
                source = allSites;
            }
            else
            {
                source = masterSchedule
                    .OrderBy(kvp => kvp.Key)
                    .SelectMany(kvp => kvp.Value ?? new List<ServiceSite>())
                    .GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First());
            }

            var seq = 0;
            foreach (var site in source)
            {
                if (string.IsNullOrWhiteSpace(site.Id) || map.ContainsKey(site.Id))
                    continue;

                seq++;
                map[site.Id] = $"srv-{seq}";
            }

            return map;
        }

        private static List<TimetableDayDto> BuildTimetableOutput(IEnumerable<WeeklyRoute> weeklyRoutes)
        {
            var days = (weeklyRoutes ?? Array.Empty<WeeklyRoute>()).ToList();
            var result = new List<TimetableDayDto>(days.Count);

            for (var i = 0; i < days.Count; i++)
            {
                var day = days[i];
                var routes = (day.Routes ?? new List<OptimizedRoute>())
                    .Select(route => new TimetableRouteDto(
                        TechnicianId: route.TechnicianId,
                        TechnicianName: route.TechnicianName,
                        Stops: (route.Stops ?? new List<RouteStop>())
                            .OrderBy(s => s.Sequence)
                            .ThenBy(s => s.ExpectedArrivalTime)
                            .Select(stop => new TimetableStopDto(
                                SiteId: stop.SiteId,
                                SiteName: stop.SiteName,
                                ExpectedArrivalTime: stop.ExpectedArrivalTime.ToString("o", CultureInfo.InvariantCulture),
                                Sequence: stop.Sequence
                            ))
                            .ToList(),
                        TotalDistanceKm: Math.Round(route.TotalDistanceKm, 3),
                        TotalDurationMinutes: Math.Round(route.TotalDurationMinutes, 1)
                    ))
                    .ToList();

                var date = day.Date != default
                    ? day.Date.Date
                    : routes
                        .SelectMany(r => r.Stops)
                        .Select(s => DateTime.TryParse(s.ExpectedArrivalTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed.Date : (DateTime?)null)
                        .Where(d => d.HasValue)
                        .Select(d => d!.Value)
                        .DefaultIfEmpty(DateTime.MinValue)
                        .Min();

                result.Add(new TimetableDayDto(
                    Day: i + 1,
                    Date: date == DateTime.MinValue ? string.Empty : date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Routes: routes
                ));
            }

            return result;
        }

        private static List<StopDto> BuildStopsForRoute(
            List<RouteStop> orderedStops,
            string techInternalId,
            Dictionary<string, Technician> techById,
            Dictionary<string, ServiceSite> siteById,
            Dictionary<string, string> siteIdMap
        )
        {
            var outStops = new List<StopDto>();
            if (orderedStops.Count == 0)
                return outStops;

            techById.TryGetValue(techInternalId, out var tech);

            string? officeStartAddr = tech?.StartLocation?.FullAddress ?? tech?.OfficeAddressRaw ?? tech?.HomeAddressRaw;
            string? officeEndAddr = tech?.EndLocation?.FullAddress ?? tech?.OfficeAddressRaw ?? tech?.HomeAddressRaw;

            var first = orderedStops.First();
            var last = orderedStops.Last();

            // Start: use first arrival time (we don't have explicit departure time without touching solver)
            outStops.Add(new StopDto(
                type: "start",
                arrivalTime: first.ExpectedArrivalTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                durationMinutes: 0,
                address: officeStartAddr
            ));

            // Services (+ optional break after first service)
            for (var i = 0; i < orderedStops.Count; i++)
            {
                var s = orderedStops[i];
                siteById.TryGetValue(s.SiteId, out var site);

                var serviceId = siteIdMap.TryGetValue(s.SiteId, out var outSid) ? outSid : null;
                var duration = site?.VisitDuration ?? 0;
                var addr = site?.Address;

                outStops.Add(new StopDto(
                    type: "service",
                    arrivalTime: s.ExpectedArrivalTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                    durationMinutes: duration,
                    address: addr,
                    serviceId: serviceId
                ));

                // "Paint" a break after the first service if technician has it
                if (i == 0 && tech?.MinBreakMinutes is int breakMin && breakMin > 0)
                {
                    var breakArrival = s.ExpectedArrivalTime.AddMinutes(duration);
                    outStops.Add(new StopDto(
                        type: "break",
                        arrivalTime: breakArrival.ToString("HH:mm", CultureInfo.InvariantCulture),
                        durationMinutes: breakMin,
                        address: addr
                    ));
                }
            }

            // End: approximate as last arrival + last service duration
            var lastDuration = 0;
            if (siteById.TryGetValue(last.SiteId, out var lastSite))
                lastDuration = lastSite.VisitDuration;

            var endArrival = last.ExpectedArrivalTime.AddMinutes(lastDuration);

            outStops.Add(new StopDto(
                type: "end_point",
                arrivalTime: endArrival.ToString("HH:mm", CultureInfo.InvariantCulture),
                durationMinutes: 0,
                address: officeEndAddr
            ));

            return outStops;
        }

        private static List<WeekDto> BuildWeeks(List<DayDto> days)
        {
            var result = new List<WeekDto>();
            if (days.Count == 0)
                return result;

            // parse day strings (yyyy-MM-dd)
            var parsed = days
                .Select(d => (Day: d, Date: DateTime.ParseExact(d.day, "yyyy-MM-dd", CultureInfo.InvariantCulture)))
                .OrderBy(x => x.Date)
                .ToList();

            // group by Monday of that week
            foreach (var wg in parsed.GroupBy(x => WeekStartMonday(x.Date)))
            {
                var weekStart = wg.Key;
                var weekEnd = weekStart.AddDays(6);

                var weekDays = wg.Select(x => x.Day).ToList();

                // weekly summary: sum durations for each tech for all stops in that week
                var sums = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in weekDays)
                {
                    foreach (var r in d.routes)
                    {
                        if (!sums.ContainsKey(r.techId)) sums[r.techId] = 0;
                        sums[r.techId] += r.stops.Sum(s => s.durationMinutes);
                    }
                }

                var summary = sums
                    .OrderBy(kv => kv.Key)
                    .Select(kv => new TechnicianWeekSummaryItem(kv.Key, kv.Value, Math.Round(kv.Value / 60.0, 2)))
                    .ToList();

                result.Add(new WeekDto(
                    weekStartDate: weekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    weekEndDate: weekEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    technicianWeekSummary: summary,
                    days: weekDays
                ));
            }

            return result;
        }

        private static DateTime NextMonday(DateTime from)
        {
            // If today is Monday, we want next Monday (minimum 7 days ahead)
            var daysUntil = ((int)DayOfWeek.Monday - (int)from.DayOfWeek + 7) % 7;
            if (daysUntil == 0) daysUntil = 7;
            return from.Date.AddDays(daysUntil);
        }

        private static DateTime WeekStartMonday(DateTime date)
        {
            var diff = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            return date.Date.AddDays(-diff);
        }

        private static int ComputeHorizonDays(List<int> intervals)
        {
            // Must be at least 7
            if (intervals == null || intervals.Count == 0)
                return 7;

            // normalize 1..7 visits-per-week variants: treat as weekly (7)
            var normalized = intervals.Select(d => d <= 7 ? 7 : d).Distinct().ToList();

            var has10or35 = normalized.Contains(10) || normalized.Contains(35);

            var lcm = 1;
            foreach (var d in normalized)
            {
                lcm = Lcm(lcm, d);
                if (lcm > 10_000) break; // safety
            }

            // Cap at 168 by design
            if (!has10or35)
            {
                // LCM will be one of 7,14,21,42,56,84,168 for your supported set.
                // If something odd slips in, cap to 168.
                if (lcm < 7) lcm = 7;
                if (lcm > 168) lcm = 168;
                // ensure multiple of 7
                lcm = RoundUpToMultipleOf7(lcm);
                return Math.Min(lcm, 168);
            }

            // Rule for 10/35:
            // if LCM > 70 => 168, else => 70 (week-multiple horizon that still covers 10/35)
            if (lcm > 70) return 168;
            return 70;
        }

        private static int RoundUpToMultipleOf7(int n)
        {
            if (n <= 0) return 7;
            var rem = n % 7;
            return rem == 0 ? n : (n + (7 - rem));
        }

        private static int Gcd(int a, int b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);
            while (b != 0)
            {
                var t = a % b;
                a = b;
                b = t;
            }
            return a;
        }

        private static int Lcm(int a, int b)
        {
            if (a == 0 || b == 0) return 0;
            return checked(a / Gcd(a, b) * b);
        }

        private static string ResolveMasterCalendarDir(string? outputDirOverride)
        {
            if (!string.IsNullOrWhiteSpace(outputDirOverride))
                return outputDirOverride;

            var env = Environment.GetEnvironmentVariable("OBG_MASTER_CALENDAR_DIR");
            if (!string.IsNullOrWhiteSpace(env))
                return env;

            var cur = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (cur != null)
            {
                var input = Path.Combine(cur.FullName, "input");
                var output = Path.Combine(cur.FullName, "output");
                var lambda = Path.Combine(cur.FullName, "ObgLambda");
                var services = Path.Combine(cur.FullName, "ObgServices");

                if (Directory.Exists(input) && Directory.Exists(lambda) && Directory.Exists(services))
                    return Path.Combine(cur.FullName, "master-calendar");

                if (Directory.Exists(input) && Directory.Exists(output))
                    return Path.Combine(cur.FullName, "master-calendar");

                cur = cur.Parent;
            }

            return Path.Combine(Directory.GetCurrentDirectory(), "master-calendar");
        }

        private static string ResolveTimetableDir(string? outputDirOverride)
        {
            if (!string.IsNullOrWhiteSpace(outputDirOverride))
                return outputDirOverride;

            var env = Environment.GetEnvironmentVariable("OBG_TIMETABLE_DIR");
            if (!string.IsNullOrWhiteSpace(env))
                return env;

            var cur = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (cur != null)
            {
                var input = Path.Combine(cur.FullName, "input");
                var output = Path.Combine(cur.FullName, "output");
                var lambda = Path.Combine(cur.FullName, "ObgLambda");
                var services = Path.Combine(cur.FullName, "ObgServices");

                if (Directory.Exists(input) && Directory.Exists(lambda) && Directory.Exists(services))
                    return Path.Combine(cur.FullName, "timetable");

                if (Directory.Exists(input) && Directory.Exists(output))
                    return Path.Combine(cur.FullName, "timetable");

                cur = cur.Parent;
            }

            return Path.Combine(Directory.GetCurrentDirectory(), "timetable");
        }

        private static string ResolveOutputDir(string? outputDirOverride)
        {
            // 1) explicit parameter
            if (!string.IsNullOrWhiteSpace(outputDirOverride))
                return outputDirOverride;

            // 2) env var
            var env = Environment.GetEnvironmentVariable("OBG_OUTPUT_DIR");
            if (!string.IsNullOrWhiteSpace(env))
                return env;

            // 3) discover repo root
            var cur = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (cur != null)
            {
                var input = Path.Combine(cur.FullName, "input");
                var lambda = Path.Combine(cur.FullName, "ObgLambda");
                var services = Path.Combine(cur.FullName, "ObgServices");

                if (Directory.Exists(input) && Directory.Exists(lambda) && Directory.Exists(services))
                    return Path.Combine(cur.FullName, "output");

                cur = cur.Parent;
            }

            // fallback
            return Path.Combine(Directory.GetCurrentDirectory(), "output");
        }
    }
}
