using ObgServices.Models;

namespace ObgServices.Services
{
    public class MasterSchedulerService
    {
        private const double CapacityBuffer = 0.85;
        private const double NearFullThreshold = 0.85;
        private const double NearFullPenalty = 250.0;
        private const double WeeklyUtilizationWeight = 1000.0;
        private const double GeoBonusPerMatch = 30.0;
        private const double GeoOutlierPenalty = 40.0;

        public static Dictionary<int, List<ServiceSite>> GenerateMasterSchedule(List<ServiceSite> sites, List<Technician> techs)
        {
            if (sites.Count == 0)
                return new Dictionary<int, List<ServiceSite>>();

            int maxInterval = sites.Max(s => Math.Max(1, s.VisitIntervalDays));
            int horizon = BusinessDayHelper.ToBusinessDays(maxInterval);

            var schedule = new Dictionary<int, List<ServiceSite>>();
            for (int i = 0; i < horizon; i++)
                schedule[i] = [];

            var dayCapacity = BuildDayCapacity(horizon, techs);
            var remainingCapacity = dayCapacity.ToArray();
            var dailyLoad = new double[horizon];
            var weeklyLoad = new double[(int)Math.Ceiling(horizon / 7.0)];
            var weeklyCapacity = BuildWeeklyCapacity(dayCapacity);

            Console.WriteLine($"[MASTER] HorizonDays={horizon}, WeeklyBuckets={weeklyLoad.Length}, TotalDayCapacity={(int)dayCapacity.Sum()}, Sites={sites.Count}, GeoScoring=enabled");

            var siteContexts = sites
                .Select(site => BuildSiteContext(site, techs, horizon))
                .OrderBy(ctx => ctx.FeasibleDayCount)
                .ThenBy(ctx => ctx.MinEligibleTechCount)
                .ThenByDescending(ctx => ctx.WeeklyLoadMinutes)
                .ThenByDescending(ctx => ctx.Site.VisitDuration)
                .ToList();

            int noFeasibleCount = 0;
            int capacityRejectedCount = 0;
            int placedVisits = 0;

            foreach (var ctx in siteContexts)
            {
                if (ctx.FeasibleDayCount == 0)
                {
                    noFeasibleCount++;
                    Console.WriteLine($"[MASTER][WARN] Site '{ctx.Site.Id}' has no feasible days in the planning horizon. Allowed weekdays={ctx.FeasibleDayCount}, min eligible techs={(ctx.MinEligibleTechCount == int.MaxValue ? "n/a" : ctx.MinEligibleTechCount)}.");
                    continue;
                }

                int before = CountScheduledVisits(schedule, ctx.Site);

                if (ctx.Site.VisitIntervalDays <= 7 && ctx.Site.VisitsPerInterval > 1)
                    ScheduleWeeklyRecurringSite(ctx, schedule, dailyLoad, weeklyLoad, remainingCapacity, weeklyCapacity, horizon);
                else
                    ScheduleByInterval(ctx, schedule, dailyLoad, weeklyLoad, remainingCapacity, weeklyCapacity, horizon);

                int after = CountScheduledVisits(schedule, ctx.Site);
                if (after == before)
                    capacityRejectedCount++;
                else
                    placedVisits += after - before;
            }

            RebalanceFlexibleSites(schedule, dailyLoad, weeklyLoad, remainingCapacity, weeklyCapacity, horizon);

            Console.WriteLine($"[MASTER] PlacementSummary: placedVisits={placedVisits}, noFeasibleSites={noFeasibleCount}, capacityRejectedSites={capacityRejectedCount}");
            for (int dayIndex = 0; dayIndex < horizon; dayIndex++)
            {
                double cap = Math.Max(1.0, dayCapacity[dayIndex]);
                double utilization = dailyLoad[dayIndex] / cap;
                Console.WriteLine($"[MASTER] Day {dayIndex + 1} ({DayOfWeekFromIndex(dayIndex)}): visits={schedule[dayIndex].Count}, load={(int)dailyLoad[dayIndex]}/{(int)cap}, utilization={utilization:P0}");
            }

            return schedule;
        }

        private static SitePlanningContext BuildSiteContext(ServiceSite site, List<Technician> techs, int horizon)
        {
            var feasibleDays = new bool[horizon];
            var eligibleTechCount = new int[horizon];
            var loadMinutes = Math.Max(1, site.VisitDuration) * Math.Max(1, site.TechsNeeded);
            int feasibleCount = 0;
            int minEligible = int.MaxValue;

            for (int dayIndex = 0; dayIndex < horizon; dayIndex++)
            {
                var dayOfWeek = DayOfWeekFromIndex(dayIndex);
                var eligibleTechs = techs.Where(t => CanTechServeOnDay(t, site, dayOfWeek)).ToList();
                int count = eligibleTechs.Count;
                eligibleTechCount[dayIndex] = count;

                bool feasible = count >= Math.Max(1, site.TechsNeeded) && SkillsCovered(site, eligibleTechs);
                feasibleDays[dayIndex] = feasible;

                if (feasible)
                {
                    feasibleCount++;
                    minEligible = Math.Min(minEligible, count);
                }
            }

            return new SitePlanningContext(
                site,
                feasibleDays,
                eligibleTechCount,
                feasibleCount,
                minEligible == int.MaxValue ? int.MaxValue : minEligible,
                loadMinutes,
                CalculateWeeklyLoadMinutes(site, loadMinutes));
        }

        private static void ScheduleByInterval(
            SitePlanningContext ctx,
            Dictionary<int, List<ServiceSite>> schedule,
            double[] dailyLoad,
            double[] weeklyLoad,
            double[] remainingCapacity,
            double[] weeklyCapacity,
            int horizon)
        {
            int intervalDays = Math.Max(1, ctx.Site.VisitIntervalDays);
            int maxStartDay = Math.Min(intervalDays, horizon);
            int bestStartDay = -1;
            double bestScore = double.MaxValue;

            for (int startDay = 0; startDay < maxStartDay; startDay++)
            {
                bool valid = true;
                double optionScore = 0;

                for (int dayIndex = startDay; dayIndex < horizon; dayIndex += intervalDays)
                {
                    if (!CanPlaceVisit(ctx, dayIndex, remainingCapacity, weeklyLoad, weeklyCapacity))
                    {
                        valid = false;
                        break;
                    }

                    optionScore += ScoreDayPlacement(ctx.Site, schedule, dailyLoad, weeklyLoad, weeklyCapacity, dayIndex, ctx.LoadMinutes);
                }

                if (valid && optionScore < bestScore)
                {
                    bestScore = optionScore;
                    bestStartDay = startDay;
                }
            }

            if (bestStartDay == -1)
            {
                Console.WriteLine($"[MASTER][WARN] Site '{ctx.Site.Id}' could not be placed without breaking day/weekly capacity.");
                return;
            }

            for (int dayIndex = bestStartDay; dayIndex < horizon; dayIndex += intervalDays)
                PlaceVisit(ctx.Site, dayIndex, ctx.LoadMinutes, schedule, dailyLoad, weeklyLoad, remainingCapacity);
        }

        private static void ScheduleWeeklyRecurringSite(
            SitePlanningContext ctx,
            Dictionary<int, List<ServiceSite>> schedule,
            double[] dailyLoad,
            double[] weeklyLoad,
            double[] remainingCapacity,
            double[] weeklyCapacity,
            int horizon)
        {
            int visits = Math.Clamp(Convert.ToInt32(ctx.Site.VisitsPerInterval), 1, 7);
            int weeks = (int)Math.Ceiling(horizon / 7.0);
            var selectedDays = SelectWeeklyPattern(ctx, schedule, dailyLoad, weeklyLoad, remainingCapacity, weeklyCapacity, horizon, visits);

            if (selectedDays.Count == 0)
            {
                Console.WriteLine($"[MASTER][WARN] Weekly site '{ctx.Site.Id}' has no valid weekly pattern.");
                return;
            }

            foreach (int dowIndex in selectedDays)
            {
                for (int week = 0; week < weeks; week++)
                {
                    int dayIndex = week * 7 + dowIndex;
                    if (dayIndex >= horizon)
                        break;

                    if (!CanPlaceVisit(ctx, dayIndex, remainingCapacity, weeklyLoad, weeklyCapacity))
                        continue;

                    PlaceVisit(ctx.Site, dayIndex, ctx.LoadMinutes, schedule, dailyLoad, weeklyLoad, remainingCapacity);
                }
            }
        }

        private static List<int> SelectWeeklyPattern(
            SitePlanningContext ctx,
            Dictionary<int, List<ServiceSite>> schedule,
            double[] dailyLoad,
            double[] weeklyLoad,
            double[] remainingCapacity,
            double[] weeklyCapacity,
            int horizon,
            int visitsPerWeek)
        {
            var candidateDays = Enumerable.Range(0, Math.Min(7, horizon))
                .Where(dayIndex => ctx.FeasibleDays[dayIndex])
                .ToList();

            if (candidateDays.Count < visitsPerWeek)
                return [];

            // For dense 6/7 patterns prefer "all feasible days except the worst".
            if (visitsPerWeek == 6 && candidateDays.Count == 7)
            {
                var worstDay = candidateDays
                    .OrderByDescending(d => ScoreDayPlacement(ctx.Site, schedule, dailyLoad, weeklyLoad, weeklyCapacity, d, ctx.LoadMinutes))
                    .First();
                return candidateDays.Where(d => d != worstDay).OrderBy(d => d).ToList();
            }

            List<int>? best = null;
            double bestScore = double.MaxValue;

            foreach (var combo in GetCombinations(candidateDays, visitsPerWeek))
            {
                double score = 0;
                bool valid = true;

                foreach (int dowIndex in combo)
                {
                    for (int dayIndex = dowIndex; dayIndex < horizon; dayIndex += 7)
                    {
                        if (!CanPlaceVisit(ctx, dayIndex, remainingCapacity, weeklyLoad, weeklyCapacity))
                        {
                            valid = false;
                            break;
                        }

                        score += ScoreDayPlacement(ctx.Site, schedule, dailyLoad, weeklyLoad, weeklyCapacity, dayIndex, ctx.LoadMinutes);
                    }

                    if (!valid)
                        break;
                }

                if (!valid)
                    continue;

                score += CalculateSpacingPenalty(combo);

                if (score < bestScore)
                {
                    bestScore = score;
                    best = combo;
                }
            }

            return best ?? [];
        }

        private static bool CanPlaceVisit(SitePlanningContext ctx, int dayIndex, double[] remainingCapacity, double[] weeklyLoad, double[] weeklyCapacity)
        {
            if (dayIndex < 0 || dayIndex >= ctx.FeasibleDays.Length)
                return false;

            if (!ctx.FeasibleDays[dayIndex])
                return false;

            if (remainingCapacity[dayIndex] + 0.001 < ctx.LoadMinutes)
                return false;

            int weekIndex = dayIndex / 7;
            return weekIndex < weeklyCapacity.Length && weeklyLoad[weekIndex] + ctx.LoadMinutes <= weeklyCapacity[weekIndex] + 0.001;
        }

        private static double ScoreDayPlacement(
            ServiceSite site,
            Dictionary<int, List<ServiceSite>> schedule,
            double[] dailyLoad,
            double[] weeklyLoad,
            double[] weeklyCapacity,
            int dayIndex,
            double loadMinutes)
        {
            int weekIndex = dayIndex / 7;
            double dayLoadAfter = dailyLoad[dayIndex] + loadMinutes;
            double dailyCapacity = Math.Max(1.0, dailyLoad.Length > dayIndex ? dailyLoad[dayIndex] + 1 : 1);
            double utilizationPenalty = dayLoadAfter;

            double weeklyUtilizationPenalty = weekIndex < weeklyCapacity.Length && weeklyCapacity[weekIndex] > 0
                ? ((weeklyLoad[weekIndex] + loadMinutes) / weeklyCapacity[weekIndex]) * WeeklyUtilizationWeight
                : 0;

            double nearFullPenalty = 0;
            if (weekIndex < weeklyCapacity.Length && weeklyCapacity[weekIndex] > 0)
            {
                double weeklyRatio = (weeklyLoad[weekIndex] + loadMinutes) / weeklyCapacity[weekIndex];
                if (weeklyRatio >= NearFullThreshold)
                    nearFullPenalty += NearFullPenalty * (weeklyRatio - NearFullThreshold) / (1.0 - NearFullThreshold);
            }

            int geoBonus = schedule[dayIndex].Count(existing => SameArea(existing.Address, site.Address));
            int geoOutliers = schedule[dayIndex].Count > 0 && geoBonus == 0 ? 1 : 0;
            return utilizationPenalty + weeklyUtilizationPenalty + nearFullPenalty - (geoBonus * GeoBonusPerMatch) + (geoOutliers * GeoOutlierPenalty);
        }

        private static void PlaceVisit(
            ServiceSite site,
            int dayIndex,
            double loadMinutes,
            Dictionary<int, List<ServiceSite>> schedule,
            double[] dailyLoad,
            double[] weeklyLoad,
            double[] remainingCapacity)
        {
            schedule[dayIndex].Add(site);
            dailyLoad[dayIndex] += loadMinutes;
            remainingCapacity[dayIndex] -= loadMinutes;
            weeklyLoad[dayIndex / 7] += loadMinutes;
        }

        private static void RebalanceFlexibleSites(
            Dictionary<int, List<ServiceSite>> schedule,
            double[] dailyLoad,
            double[] weeklyLoad,
            double[] remainingCapacity,
            double[] weeklyCapacity,
            int horizon)
        {
            int moves = 0;
            for (int dayIndex = 0; dayIndex < horizon; dayIndex++)
            {
                if (remainingCapacity[dayIndex] >= 0)
                    continue;

                while (remainingCapacity[dayIndex] < 0)
                {
                    var siteToMove = schedule[dayIndex]
                        .Where(s => s.VisitIntervalDays >= 14)
                        .OrderByDescending(s => s.VisitDuration * Math.Max(1, s.TechsNeeded))
                        .FirstOrDefault();

                    if (siteToMove is null)
                        break;

                    double loadMinutes = Math.Max(1, siteToMove.VisitDuration) * Math.Max(1, siteToMove.TechsNeeded);
                    int targetDay = FindBetterDayForFlexibleSite(siteToMove, dayIndex, schedule, remainingCapacity, dailyLoad, weeklyLoad, weeklyCapacity, horizon, loadMinutes);

                    if (targetDay == dayIndex)
                        break;

                    schedule[dayIndex].Remove(siteToMove);
                    schedule[targetDay].Add(siteToMove);

                    dailyLoad[dayIndex] -= loadMinutes;
                    dailyLoad[targetDay] += loadMinutes;
                    remainingCapacity[dayIndex] += loadMinutes;
                    remainingCapacity[targetDay] -= loadMinutes;
                    weeklyLoad[dayIndex / 7] -= loadMinutes;
                    weeklyLoad[targetDay / 7] += loadMinutes;
                    moves++;
                }
            }

            if (moves > 0)
                Console.WriteLine($"[MASTER] Rebalance moved {moves} flexible visits.");
        }

        private static int FindBetterDayForFlexibleSite(
            ServiceSite site,
            int currentDay,
            Dictionary<int, List<ServiceSite>> schedule,
            double[] remainingCapacity,
            double[] dailyLoad,
            double[] weeklyLoad,
            double[] weeklyCapacity,
            int horizon,
            double loadMinutes)
        {
            int currentWeek = currentDay / 7;
            int weekStart = currentWeek * 7;
            int weekEnd = Math.Min(weekStart + 6, horizon - 1);
            int bestDay = currentDay;
            double currentScore = ScoreDayPlacement(site, schedule, dailyLoad, weeklyLoad, weeklyCapacity, currentDay, 0);
            double bestScore = currentScore;

            for (int dayIndex = weekStart; dayIndex <= weekEnd; dayIndex++)
            {
                if (dayIndex == currentDay)
                    continue;

                if (remainingCapacity[dayIndex] + 0.001 < loadMinutes)
                    continue;

                int weekIndex = dayIndex / 7;
                if (weeklyLoad[weekIndex] + loadMinutes > weeklyCapacity[weekIndex] + 0.001)
                    continue;

                double score = ScoreDayPlacement(site, schedule, dailyLoad, weeklyLoad, weeklyCapacity, dayIndex, loadMinutes);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestDay = dayIndex;
                }
            }

            return bestDay;
        }

        private static double[] BuildDayCapacity(int horizon, List<Technician> techs)
        {
            var capacities = new double[horizon];
            for (int dayIndex = 0; dayIndex < horizon; dayIndex++)
            {
                var dayOfWeek = DayOfWeekFromIndex(dayIndex);
                capacities[dayIndex] = techs.Sum(tech => GetTechnicianAvailableMinutes(tech, dayOfWeek)) * CapacityBuffer;
            }
            return capacities;
        }

        private static double[] BuildWeeklyCapacity(double[] dayCapacity)
        {
            int weeks = (int)Math.Ceiling(dayCapacity.Length / 7.0);
            var result = new double[weeks];
            for (int dayIndex = 0; dayIndex < dayCapacity.Length; dayIndex++)
                result[dayIndex / 7] += dayCapacity[dayIndex];
            return result;
        }

        private static double GetTechnicianAvailableMinutes(Technician tech, DayOfWeek dayOfWeek)
        {
            var workingWindow = tech.WorkingHours.FirstOrDefault(w => w.Day == dayOfWeek);
            if (workingWindow is not null)
            {
                double minutes = Math.Max(0, (workingWindow.CloseTime - workingWindow.OpenTime).TotalMinutes);
                if (tech.MinBreakMinutes is > 0)
                    minutes = Math.Max(0, minutes - tech.MinBreakMinutes.Value);
                return minutes;
            }

            if (tech.MaxDailyServiceHours is > 0)
                return tech.MaxDailyServiceHours.Value * 60.0;

            return 0;
        }

        private static bool CanTechServeOnDay(Technician tech, ServiceSite site, DayOfWeek dayOfWeek)
        {
            if (!TechnicianFilterService.ValidateHardConstraints(tech, site))
                return false;

            bool techAvailable = tech.WorkingHours.Count == 0 || tech.WorkingHours.Any(w => w.Day == dayOfWeek);
            if (!techAvailable)
                return false;

            bool siteAvailable = site.AccessWindows.Count == 0 || site.AccessWindows.Any(w => w.Day == dayOfWeek);
            return siteAvailable;
        }

        private static bool SkillsCovered(ServiceSite site, List<Technician> techs)
        {
            if (techs.Count < Math.Max(1, site.TechsNeeded))
                return false;

            // single-tech: the technician must cover the whole requirement alone
            if (site.TechsNeeded <= 1)
            {
                return techs.Any(tech => CoversAllSiteRequirements(site, tech));
            }

            // multi-tech: greedy team coverage
            var remaining = new List<Technician>(techs);
            int teamSize = Math.Max(1, site.TechsNeeded);
            bool needBaseSkill = true;
            bool needGreenWall = site.RequiresGreenWallSkills;
            bool needPesticide = site.RequiresPesticide;
            bool needHeights = site.RequiresWorkAtHeights;
            bool needLift = site.RequiresUsingLift;
            bool needPhysical = site.RequiresPhysicallyDemandingJob;
            bool needCitizen = site.RequiresCitizenship;

            for (int i = 0; i < teamSize && remaining.Count > 0; i++)
            {
                var best = remaining
                    .OrderByDescending(t => CoverageScore(site, t, needBaseSkill, needGreenWall, needPesticide, needHeights, needLift, needPhysical, needCitizen))
                    .First();

                if (CoverageScore(site, best, needBaseSkill, needGreenWall, needPesticide, needHeights, needLift, needPhysical, needCitizen) == 0)
                    break;

                remaining.Remove(best);

                if (needBaseSkill && HasBaseSkill(site, best)) needBaseSkill = false;
                if (needGreenWall && best.HasLivingWallsSkills) needGreenWall = false;
                if (needPesticide && best.PesticideCertificated) needPesticide = false;
                if (needHeights && best.CanWorkAtHeights) needHeights = false;
                if (needLift && best.CertifiedUsingLift) needLift = false;
                if (needPhysical && best.CanPhysicallyDemandingJob) needPhysical = false;
                if (needCitizen && best.HasCitizenship) needCitizen = false;
            }

            return !(needBaseSkill || needGreenWall || needPesticide || needHeights || needLift || needPhysical || needCitizen);
        }

        private static bool CoversAllSiteRequirements(ServiceSite site, Technician tech)
        {
            if (!HasBaseSkill(site, tech)) return false;
            if (site.RequiresGreenWallSkills && !tech.HasLivingWallsSkills) return false;
            if (site.RequiresPesticide && !tech.PesticideCertificated) return false;
            if (site.RequiresWorkAtHeights && !tech.CanWorkAtHeights) return false;
            if (site.RequiresUsingLift && !tech.CertifiedUsingLift) return false;
            if (site.RequiresPhysicallyDemandingJob && !tech.CanPhysicallyDemandingJob) return false;
            if (site.RequiresCitizenship && !tech.HasCitizenship) return false;
            return true;
        }

        private static bool HasBaseSkill(ServiceSite site, Technician tech)
        {
            return site.RequiredSkill switch
            {
                Skill.Interior => tech.InteriorLevel >= site.RequiredSkillLevel,
                Skill.Exterior => tech.ExteriorLevel >= site.RequiredSkillLevel,
                Skill.Floral => tech.FloralLevel >= site.RequiredSkillLevel,
                _ => false
            };
        }

        private static int CoverageScore(ServiceSite site, Technician tech, bool needBaseSkill, bool needGreenWall, bool needPesticide, bool needHeights, bool needLift, bool needPhysical, bool needCitizen)
        {
            int score = 0;
            if (needBaseSkill && HasBaseSkill(site, tech)) score++;
            if (needGreenWall && tech.HasLivingWallsSkills) score++;
            if (needPesticide && tech.PesticideCertificated) score++;
            if (needHeights && tech.CanWorkAtHeights) score++;
            if (needLift && tech.CertifiedUsingLift) score++;
            if (needPhysical && tech.CanPhysicallyDemandingJob) score++;
            if (needCitizen && tech.HasCitizenship) score++;
            return score;
        }

        private static int CalculateWeeklyLoadMinutes(ServiceSite site, double loadMinutes)
        {
            if (site.VisitIntervalDays <= 7)
            {
                int visits = Math.Clamp(Convert.ToInt32(site.VisitsPerInterval), 1, 7);
                return (int)(loadMinutes * visits);
            }

            return (int)Math.Round(loadMinutes * (7.0 / Math.Max(1, site.VisitIntervalDays)));
        }

        private static double CalculateSpacingPenalty(List<int> selectedDays)
        {
            if (selectedDays.Count <= 1)
                return 0;

            var ordered = selectedDays.OrderBy(x => x).ToList();
            var gaps = new List<int>();
            for (int i = 1; i < ordered.Count; i++)
                gaps.Add(ordered[i] - ordered[i - 1]);
            gaps.Add((ordered[0] + 7) - ordered[^1]);

            double idealGap = 7.0 / selectedDays.Count;
            return gaps.Sum(g => Math.Abs(g - idealGap)) * 100.0;
        }

        private static IEnumerable<List<int>> GetCombinations(List<int> source, int count)
        {
            return GetCombinationsInternal(source, count, 0, []);
        }

        private static IEnumerable<List<int>> GetCombinationsInternal(List<int> source, int count, int index, List<int> current)
        {
            if (current.Count == count)
            {
                yield return [.. current];
                yield break;
            }

            for (int i = index; i < source.Count; i++)
            {
                current.Add(source[i]);
                foreach (var combo in GetCombinationsInternal(source, count, i + 1, current))
                    yield return combo;
                current.RemoveAt(current.Count - 1);
            }
        }

        private static bool SameArea(string? a, string? b)
        {
            var partA = a?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var partB = b?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return !string.IsNullOrWhiteSpace(partA) && string.Equals(partA, partB, StringComparison.OrdinalIgnoreCase);
        }

        private static int CountScheduledVisits(Dictionary<int, List<ServiceSite>> schedule, ServiceSite site)
        {
            return schedule.Sum(kvp => kvp.Value.Count(s => ReferenceEquals(s, site) || s.Id == site.Id));
        }

        private static DayOfWeek DayOfWeekFromIndex(int dayIndex)
        {
            return (DayOfWeek)(((int)DayOfWeek.Monday + (dayIndex % 7)) % 7);
        }

        private sealed record SitePlanningContext(
            ServiceSite Site,
            bool[] FeasibleDays,
            int[] EligibleTechCount,
            int FeasibleDayCount,
            int MinEligibleTechCount,
            double LoadMinutes,
            int WeeklyLoadMinutes);
    }
}
