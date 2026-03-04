using Amazon.Lambda.Core;
using ClosedXML.Excel;
using ObgLambda;
using ObgServices.Models;
using System.Globalization;

namespace ObgLambda.Excel;

public static class ExcelRoutingRequestParser
{
    public static RoutingRequest Parse(byte[] excelBytes, string? sourceName, ILambdaLogger? logger = null)
    {
        using var ms = new MemoryStream(excelBytes);
        using var wb = new XLWorkbook(ms);

        var techniciansSheet = FindSheet(wb, "Technicians");
        var sitesSheet = FindSheet(wb, "Service sites");
        var targetDates = new List<DateTime>();

        if (techniciansSheet is null)
            throw new InvalidOperationException("Excel: не знайдено лист 'Technicians'.");

        if (sitesSheet is null)
            throw new InvalidOperationException("Excel: не знайдено лист 'Service sites ...'.");

        var warnings = new List<string>();
        void Warn(string msg)
        {
            warnings.Add(msg);
            logger?.LogLine("[ExcelParser][WARN] " + msg);
        }

        var technicians = ParseTechnicians(techniciansSheet, targetDates, Warn);

        var nameToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in technicians)
        {
            if (!nameToId.TryAdd(t.Name, t.Id))
            {
                Warn($"Дубль імені техніка '{t.Name}' у Technicians. Використовую перший знайдений Id='{nameToId[t.Name]}'.");
            }
        }

        var sites = ParseSites(sitesSheet, nameToId, Warn);

        logger?.LogLine($"[ExcelParser] Джерело: {sourceName ?? "excel"}. Technicians={technicians.Count}, Sites={sites.Count}, Warnings={warnings.Count}");

        return new RoutingRequest
        {
            Technicians = technicians,
            Sites = sites,
            TargetDates = targetDates
        };
    }

    private static IXLWorksheet? FindSheet(XLWorkbook wb, string nameContains)
        => wb.Worksheets.FirstOrDefault(ws =>
            ws.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));

    private static List<Technician> ParseTechnicians(IXLWorksheet ws, List<DateTime> targetDatesOutput, Action<string> warn)
    {
        // Header rows in template: 2 (main), 3 (sub)
        var dayCols = ExcelParsingHelpers.GetDayTimeColumnPairs(ws, 2, 3);

        // Target days for schedule creating
        int colTargetDays = FindColContains(ws, 2, "target days for schedule");

        int colName = FindColContains(ws, 2, "name");
        int colHome = FindColContains(ws, 2, "home address");
        int colOffice = FindColContains(ws, 2, "office address");
        int colStarts = FindColContains(ws, 2, "starts from");
        int colFinishes = FindColContains(ws, 2, "finishes at");

        int colMinBreak = FindColContains(ws, 2, "min break");
        int colBreakNotEarlier = FindColContains(ws, 2, "break should be taken not earlier");
        int colBreakNotLater = FindColContains(ws, 2, "break should be taken not later");
        int colMaxDaily = FindColContains(ws, 2, "maximum hours of work per day");
        int colMaxWeekly = FindColContains(ws, 2, "maximum hours of work per week");

        int colServiceSkills = FindColContains(ws, 2, "service skills");
        int colPhys = FindColContains(ws, 2, "physically demanding");
        int colLivingWalls = FindColContains(ws, 2, "living walls");
        int colHeights = FindColContains(ws, 2, "work at heights");
        int colLift = FindColContains(ws, 2, "lift");
        int colPesticide = FindColContains(ws, 2, "pesticide");
        int colCitizen = FindColContains(ws, 2, "citizen");

        var technicians = new List<Technician>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int row = 4;
        while (true)
        {
            var name = ExcelParsingHelpers.GetString(ws.Cell(row, colName));
            if (string.IsNullOrWhiteSpace(name))
                break;

            var targetDayRaw = ExcelParsingHelpers.GetString(ws.Cell(row, colTargetDays));
            if (!string.IsNullOrWhiteSpace(targetDayRaw))
            {
                if (DateTime.TryParseExact(targetDayRaw, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedDate))
                {
                    if (!targetDatesOutput.Contains(parsedDate))
                        targetDatesOutput.Add(parsedDate);
                }
                else
                {
                    warn($"Рядок {row}: невірний формат дати '{targetDayRaw}'. Очікується dd.mm.yyyy");
                }
            }

            var idBase = ExcelParsingHelpers.Slugify(name);
            var id = idBase;
            int suffix = 2;
            while (!usedIds.Add(id))
            {
                id = $"{idBase}-{suffix}";
                suffix++;
            }

            var homeAddr = ExcelParsingHelpers.GetString(ws.Cell(row, colHome));
            var officeAddr = ExcelParsingHelpers.GetString(ws.Cell(row, colOffice));

            var startsRaw = ExcelParsingHelpers.GetString(ws.Cell(row, colStarts));
            var finishesRaw = ExcelParsingHelpers.GetString(ws.Cell(row, colFinishes));
            var startsFrom = ExcelParsingHelpers.ParseStartFinishPoint(startsRaw);
            var finishesAt = ExcelParsingHelpers.ParseStartFinishPoint(finishesRaw);

            var minBreak = ExcelParsingHelpers.ParseInt(ws.Cell(row, colMinBreak));
            var breakNotEarlier = ExcelParsingHelpers.ParseTime(ws.Cell(row, colBreakNotEarlier));
            var breakNotLater = ExcelParsingHelpers.ParseTime(ws.Cell(row, colBreakNotLater));
            var maxDaily = ExcelParsingHelpers.ParseInt(ws.Cell(row, colMaxDaily));
            var maxWeekly = ExcelParsingHelpers.ParseInt(ws.Cell(row, colMaxWeekly)) ?? 0;

            var skillRaw = ExcelParsingHelpers.GetString(ws.Cell(row, colServiceSkills));

            // Default: no skills
            var interior = SkillLevel.None;
            var exterior = SkillLevel.None;
            var floral = SkillLevel.None;

            if (!string.IsNullOrWhiteSpace(skillRaw))
            {
                var skillParts = skillRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                bool anyParsed = false;

                foreach (var part in skillParts)
                {
                    var parsed = ExcelParsingHelpers.ParseSkillAndLevel(part);
                    if (parsed is not null)
                    {
                        var (sk, lvl) = parsed.Value;
                        switch (sk)
                        {
                            case Skill.Interior:
                                interior = lvl;
                                anyParsed = true;
                                break;
                            case Skill.Exterior:
                                exterior = lvl;
                                anyParsed = true;
                                break;
                            case Skill.Floral:
                                floral = lvl;
                                anyParsed = true;
                                break;
                        }
                    }
                    else
                    {
                        warn($"Technician '{name}': не вдалося розпізнати частину скілла '{part}' у рядку '{skillRaw}'.");
                    }
                }

                if (!anyParsed)
                {
                    warn($"Technician '{name}': жоден зі скіллів у рядку '{skillRaw}' не був розпізнаний.");
                }
            }
            else
            {
                warn($"Technician '{name}': колонка Service skills порожня.");
            }

            var workingHours = new List<TimeWindow>();
            foreach (var (day, (fromCol, toCol)) in dayCols)
            {
                var from = ExcelParsingHelpers.ParseTime(ws.Cell(row, fromCol));
                var to = ExcelParsingHelpers.ParseTime(ws.Cell(row, toCol));
                if (from is null || to is null) continue;

                workingHours.Add(new TimeWindow
                {
                    Day = day,
                    OpenTime = from.Value,
                    CloseTime = to.Value
                });
            }

            // Decide start/end addresses for future geocoding
            string? startAddr = startsFrom switch
            {
                StartFinishPoint.Home => homeAddr,
                StartFinishPoint.Office => officeAddr,
                _ => officeAddr ?? homeAddr
            };

            string? endAddr = finishesAt switch
            {
                StartFinishPoint.Home => homeAddr,
                StartFinishPoint.Office => officeAddr,
                _ => officeAddr ?? homeAddr
            };

            if (string.IsNullOrWhiteSpace(startAddr))
                warn($"Technician '{name}': порожня адреса старту (Starts from='{startsRaw}'). Office/Home address порожні.");

            var tech = new Technician
            {
                Id = id,
                Name = name,
                HomeAddressRaw = homeAddr,
                OfficeAddressRaw = officeAddr,
                StartsFrom = startsFrom,
                FinishesAt = finishesAt,
                MinBreakMinutes = minBreak,
                BreakNotEarlierThan = breakNotEarlier,
                BreakNotLaterThan = breakNotLater,
                MaxDailyServiceHours = maxDaily,
                MaxWeeklyHours = maxWeekly,
                CurrentScheduledHours = 0,
                WorkingHours = workingHours,

                InteriorLevel = interior,
                ExteriorLevel = exterior,
                FloralLevel = floral,

                CanPhysicallyDemandingJob = ExcelParsingHelpers.ParseBool(ws.Cell(row, colPhys)),
                HasLivingWallsSkills = ExcelParsingHelpers.ParseBool(ws.Cell(row, colLivingWalls)),
                CanWorkAtHeights = ExcelParsingHelpers.ParseBool(ws.Cell(row, colHeights)),
                CertifiedUsingLift = ExcelParsingHelpers.ParseBool(ws.Cell(row, colLift)),
                PesticideCertificated = ExcelParsingHelpers.ParseBool(ws.Cell(row, colPesticide)),
                HasCitizenship = ExcelParsingHelpers.ParseBool(ws.Cell(row, colCitizen)),

                // Coordinates will be resolved later if needed.
                StartLocation = new AddressInfo { Latitude = 0, Longitude = 0, FullAddress = startAddr },
                EndLocation = new AddressInfo { Latitude = 0, Longitude = 0, FullAddress = endAddr }
            };

            technicians.Add(tech);
            row++;
        }

        return technicians;
    }

    private static List<ServiceSite> ParseSites(IXLWorksheet ws, Dictionary<string, string> nameToId, Action<string> warn)
    {
        var dayCols = ExcelParsingHelpers.GetDayTimeColumnPairs(ws, 2, 3);

        int colLocationName = FindColContains(ws, 2, "location name");
        int colAddress = FindColContains(ws, 2, "site address");
        int colCurrentTech = FindColContains(ws, 2, "current technician");
        int colBestAccess = FindColContains(ws, 2, "site best accessed");
        int colTechsNeeded = FindColContains(ws, 2, "how many techs needed");

        // Entrance permit sub-headers are on row 3
        int colPermitRequired = FindColContains(ws, 3, "permit required");
        int colPermitDifficulty = FindColContains(ws, 3, "how difficult");
        int colTechsWithPermit = FindColContains(ws, 3, "techs with permit");

        int colVisitFreq = FindColContains(ws, 2, "visit freqency");
        int colVisitDur = FindColContains(ws, 2, "est duration");

        int colSkillReq = FindColContains(ws, 2, "service skill requirement");

        int colPhys = FindColContains(ws, 2, "physically demanding");
        int colLivingWalls = FindColContains(ws, 2, "living walls");
        int colHeights = FindColContains(ws, 2, "work at heights");
        int colLift = FindColContains(ws, 2, "lift");
        int colPesticide = FindColContains(ws, 2, "pesticides");
        int colCitizen = FindColContains(ws, 2, "citizen");

        int colPreferred = FindColContains(ws, 2, "should be serviced by");
        int colProhibited = FindColContains(ws, 2, "should not be serviced");

        var sites = new List<ServiceSite>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int row = 4;
        while (true)
        {
            var locName = ExcelParsingHelpers.GetString(ws.Cell(row, colLocationName));
            if (string.IsNullOrWhiteSpace(locName))
                break;

            var address = ExcelParsingHelpers.GetString(ws.Cell(row, colAddress));
            if (string.IsNullOrWhiteSpace(address))
            {
                warn($"Service site '{locName}': порожня адреса — пропускаю рядок.");
                row++;
                continue;
            }

            var idBase = ExcelParsingHelpers.Slugify(locName);
            var id = idBase;
            int suffix = 2;
            while (!usedIds.Add(id))
            {
                id = $"{idBase}-{suffix}";
                suffix++;
            }

            var currentTechRaw = ExcelParsingHelpers.GetString(ws.Cell(row, colCurrentTech));
            var currentTechIds = ExcelParsingHelpers.ParseTechList(currentTechRaw, nameToId, warn);

            var bestAccessRaw = ExcelParsingHelpers.GetString(ws.Cell(row, colBestAccess));
            var bestAccess = ExcelParsingHelpers.ParseBestAccessMode(bestAccessRaw);

            var techsNeeded = ExcelParsingHelpers.ParseInt(ws.Cell(row, colTechsNeeded)) ?? 1;

            var permitRequired = ExcelParsingHelpers.ParseBool(ws.Cell(row, colPermitRequired));
            var permitDifficultyRaw = ExcelParsingHelpers.GetString(ws.Cell(row, colPermitDifficulty));
            var permitDifficulty = ExcelParsingHelpers.ParsePermitDifficulty(permitDifficultyRaw);
            var techsWithPermitRaw = ExcelParsingHelpers.GetString(ws.Cell(row, colTechsWithPermit));
            var permittedTechIds = permitRequired
                ? ExcelParsingHelpers.ParseTechList(techsWithPermitRaw, nameToId, warn)
                : [];

            var access = new List<TimeWindow>();
            foreach (var (day, (fromCol, toCol)) in dayCols)
            {
                var from = ExcelParsingHelpers.ParseTime(ws.Cell(row, fromCol));
                var to = ExcelParsingHelpers.ParseTime(ws.Cell(row, toCol));
                if (from is null || to is null) continue;

                access.Add(new TimeWindow { Day = day, OpenTime = from.Value, CloseTime = to.Value });
            }

            var visitFreqRaw = ExcelParsingHelpers.GetString(ws.Cell(row, colVisitFreq));
            var (visitsPerInterval, intervalDays, lockAfterFirst) = ExcelParsingHelpers.ParseVisitFrequency(visitFreqRaw, warn);

            // Compatibility field for old algorithm: only if weekly interval
            var visitFreqency = intervalDays == 7 ? visitsPerInterval : (byte)0;

            var visitDurRaw = ExcelParsingHelpers.GetString(ws.Cell(row, colVisitDur));
            var visitDuration = ExcelParsingHelpers.ParseMinutes(ws.Cell(row, colVisitDur)) ?? 0;

            var skillReqRaw = ExcelParsingHelpers.GetString(ws.Cell(row, colSkillReq));
            var req = ExcelParsingHelpers.ParseSkillAndLevel(skillReqRaw);
            if (req is null)
            {
                warn($"Service site '{locName}': не вдалося розпізнати Service skill requirement = '{skillReqRaw}'. Ставлю Exterior/None.");
            }

            var preferredRaw = ExcelParsingHelpers.GetString(ws.Cell(row, colPreferred));
            var prohibitedRaw = ExcelParsingHelpers.GetString(ws.Cell(row, colProhibited));

            var site = new ServiceSite
            {
                Id = id,
                Name = locName,
                Address = address,

                CurrentTechIds = currentTechIds,
                BestAccessedBy = bestAccess,
                BestAccessedByRaw = bestAccessRaw,
                TechsNeeded = techsNeeded,

                PermitRequired = permitRequired,
                PermitDifficulty = permitDifficulty,

                AccessWindows = access,

                VisitFrequencyRaw = visitFreqRaw,
                VisitsPerInterval = visitsPerInterval,
                VisitIntervalDays = intervalDays,
                LockWeekdayAfterFirst = lockAfterFirst,

                VisitDurationRaw = visitDurRaw,
                VisitFreqency = visitFreqency,
                VisitDuration = visitDuration,

                RequiredSkill = req?.skill ?? Skill.Exterior,
                RequiredSkillLevel = req?.level ?? SkillLevel.None,

                RequiresPhysicallyDemandingJob = ExcelParsingHelpers.ParseBool(ws.Cell(row, colPhys)),
                RequiresGreenWallSkills = ExcelParsingHelpers.ParseBool(ws.Cell(row, colLivingWalls)),
                RequiresWorkAtHeights = ExcelParsingHelpers.ParseBool(ws.Cell(row, colHeights)),
                RequiresUsingLift = ExcelParsingHelpers.ParseBool(ws.Cell(row, colLift)),
                RequiresPesticide = ExcelParsingHelpers.ParseBool(ws.Cell(row, colPesticide)),
                RequiresCitizenship = ExcelParsingHelpers.ParseBool(ws.Cell(row, colCitizen)),

                PreferredTechIds = ExcelParsingHelpers.ParseTechList(preferredRaw, nameToId, warn),
                ProhibitedTechIds = ExcelParsingHelpers.ParseTechList(prohibitedRaw, nameToId, warn),
                PermittedTechIds = permittedTechIds
            };

            sites.Add(site);
            row++;
        }

        return sites;
    }

    private static int FindColContains(IXLWorksheet ws, int headerRow, string contains)
    {
        contains = contains.Trim().ToLowerInvariant();

        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 1;

        for (int col = 1; col <= lastCol; col++)
        {
            var v = ExcelParsingHelpers.GetString(ws.Cell(headerRow, col));
            if (string.IsNullOrWhiteSpace(v)) continue;

            var norm = ExcelParsingHelpers.NormalizeSpaces(v).ToLowerInvariant();
            if (norm.Contains(contains, StringComparison.OrdinalIgnoreCase))
                return col;
        }

        throw new InvalidOperationException($"Excel: не знайдено колонку (row = {headerRow}) що містить '{contains}' на листі '{ws.Name}'.");
    }
}
