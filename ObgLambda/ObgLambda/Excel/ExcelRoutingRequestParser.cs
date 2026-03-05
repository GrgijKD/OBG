using ClosedXML.Excel;
using ObgLambda;
using ObgServices.Models;
using Amazon.Lambda.Core;

namespace ObgLambda.Excel;

public static class ExcelRoutingRequestParser
{
    public static RoutingRequest Parse(byte[] excelBytes, string? sourceName, ILambdaLogger? logger = null)
    {
        using var ms = new MemoryStream(excelBytes);
        using var wb = new XLWorkbook(ms);

        var techniciansSheet = FindSheetAny(wb, "Technicians", "Tech");
        var sitesSheet = FindSheetAny(wb, "Service sites", "Sites", "Locations");

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

        var technicians = ParseTechnicians(techniciansSheet, Warn);

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
            Sites = sites
        };
    }

    private static IXLWorksheet? FindSheetAny(XLWorkbook wb, params string[] nameContainsAny)
        => wb.Worksheets.FirstOrDefault(ws =>
            nameContainsAny.Any(n => ws.Name.Contains(n, StringComparison.OrdinalIgnoreCase)));

    private static IXLWorksheet? FindSheet(XLWorkbook wb, string nameContains)
        => FindSheetAny(wb, nameContains);
private static List<Technician> ParseTechnicians(IXLWorksheet ws, Action<string> warn)
    {
        // Header rows in template: 2 (main), 3 (sub)
        var dayCols = ExcelParsingHelpers.GetDayTimeColumnPairs(ws, 2, 3);

        int colName = FindColAny(ws, 2, warn, required: true, "name", "technician name");
        int colHome = FindColAny(ws, 2, warn, required: true, "home address", "home");
        int colOffice = FindColAny(ws, 2, warn, required: false, "office address", "office");
        int colStarts = FindColAny(ws, 2, warn, required: false, "starts from", "start");
        int colFinishes = FindColAny(ws, 2, warn, required: false, "finishes at", "finish");

        int colMinBreak = FindColAny(ws, 2, warn, required: false, "min break", "min break per day");
        int colBreakNotEarlier = FindColAny(ws, 2, warn, required: false, "break should be taken not earlier", "break should be taken not earlier than");
        int colBreakNotLater = FindColAny(ws, 2, warn, required: false, "break should be taken not later", "break should be taken not later than");
        int colMaxDaily = FindColAny(ws, 2, warn, required: false, "maximum hours of work per day", "maximum hours of work per day for service");
        int colMaxWeekly = FindColAny(ws, 2, warn, required: false, "maximum hours of work per week", "maximum hours of work per week for service");

        int colServiceSkills = FindColAny(ws, 2, warn, required: true, "service skills", "skills");
        int colPhys = FindColAny(ws, 2, warn, required: false, "physically demanding", "physically demanding job");
        int colLivingWalls = FindColAny(ws, 2, warn, required: false, "living walls", "has living walls");
        int colHeights = FindColAny(ws, 2, warn, required: false, "work at heights");
        int colLift = FindColAny(ws, 2, warn, required: false, "lift", "requires using the lift");
        int colPesticide = FindColAny(ws, 2, warn, required: false, "pesticide", "pesticides");
        int colCitizen = FindColAny(ws, 2, warn, required: false, "citizen", "citizen technician");

        var technicians = new List<Technician>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int row = 4;
        while (true)
        {
            var name = ExcelParsingHelpers.GetString(ws.Cell(row, colName));
            if (string.IsNullOrWhiteSpace(name))
                break;

            var idBase = ExcelParsingHelpers.Slugify(name);
            var id = idBase;
            int suffix = 2;
            while (!usedIds.Add(id))
            {
                id = $"{idBase}-{suffix}";
                suffix++;
            }

            var homeAddr = colHome > 0 ? ExcelParsingHelpers.GetString(ws.Cell(row, colHome)) : null;
            var officeAddr = colOffice > 0 ? ExcelParsingHelpers.GetString(ws.Cell(row, colOffice)) : null;

            var startsRaw = colStarts > 0 ? ExcelParsingHelpers.GetString(ws.Cell(row, colStarts)) : null;
            var finishesRaw = colFinishes > 0 ? ExcelParsingHelpers.GetString(ws.Cell(row, colFinishes)) : null;
            var startsFrom = ExcelParsingHelpers.ParseStartFinishPoint(startsRaw);
            var finishesAt = ExcelParsingHelpers.ParseStartFinishPoint(finishesRaw);

            var minBreak = colMinBreak > 0 ? ExcelParsingHelpers.ParseInt(ws.Cell(row, colMinBreak)) : null;
            var breakNotEarlier = colBreakNotEarlier > 0 ? ExcelParsingHelpers.ParseTime(ws.Cell(row, colBreakNotEarlier)) : null;
            var breakNotLater = colBreakNotLater > 0 ? ExcelParsingHelpers.ParseTime(ws.Cell(row, colBreakNotLater)) : null;
            var maxDaily = colMaxDaily > 0 ? ExcelParsingHelpers.ParseInt(ws.Cell(row, colMaxDaily)) : null;
            var maxWeekly = (colMaxWeekly > 0 ? ExcelParsingHelpers.ParseInt(ws.Cell(row, colMaxWeekly)) : null) ?? 0;

            var skillRaw = ExcelParsingHelpers.GetString(ws.Cell(row, colServiceSkills));
            var skill = ExcelParsingHelpers.ParseSkillAndLevel(skillRaw);

            // Default: no skills
            var interior = SkillLevel.None;
            var exterior = SkillLevel.None;
            var floral = SkillLevel.None;

            if (skill is not null)
            {
                var (sk, lvl) = skill.Value;
                switch (sk)
                {
                    case Skill.Interior: interior = lvl; break;
                    case Skill.Exterior: exterior = lvl; break;
                    case Skill.Floral: floral = lvl; break;
                }
            }
            else
            {
                warn($"Technician '{name}': не вдалося розпізнати Service skills='{skillRaw}'.");
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

                CanPhysicallyDemandingJob = (colPhys > 0 && ExcelParsingHelpers.ParseBool(ws.Cell(row, colPhys))),
                HasLivingWallsSkills = (colLivingWalls > 0 && ExcelParsingHelpers.ParseBool(ws.Cell(row, colLivingWalls))),
                CanWorkAtHeights = (colHeights > 0 && ExcelParsingHelpers.ParseBool(ws.Cell(row, colHeights))),
                CertifiedUsingLift = (colLift > 0 && ExcelParsingHelpers.ParseBool(ws.Cell(row, colLift))),
                PesticideCertificated = (colPesticide > 0 && ExcelParsingHelpers.ParseBool(ws.Cell(row, colPesticide))),
                HasCitizenship = (colCitizen > 0 && ExcelParsingHelpers.ParseBool(ws.Cell(row, colCitizen))),

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

        int colLocationName = FindColAny(ws, 2, warn, required: true, "location name", "site name", "site name or code", "site code", "location");
        int colAddress = FindColAny(ws, 2, warn, required: true, "site address", "address");
        int colCurrentTech = FindColAny(ws, 2, warn, required: false, "current technician", "technician");
        int colBestAccess = FindColAny(ws, 2, warn, required: false, "site best accessed", "best accessed");
        int colTechsNeeded = FindColAny(ws, 2, warn, required: false, "how many techs needed", "techs needed");

        // Entrance permit sub-headers are on row 3
        int colPermitRequired = FindColAny(ws, 3, warn, required: false, "permit required");
        int colPermitDifficulty = FindColAny(ws, 3, warn, required: false, "how difficult", "how difficult to get a permit");
        int colTechsWithPermit = FindColAny(ws, 3, warn, required: false, "techs with permit");

        int colVisitFreq = FindColAny(ws, 2, warn, required: false, "visit freqency", "visit frequency", "frequency");
        int colVisitDur = FindColAny(ws, 2, warn, required: false, "est duration", "duration", "est duration of the visit");

        int colSkillReq = FindColAny(ws, 2, warn, required: false, "service skill requirement", "skill requirement");

        int colPhys = FindColAny(ws, 2, warn, required: false, "physically demanding", "physically demanding job");
        int colLivingWalls = FindColAny(ws, 2, warn, required: false, "living walls", "has living walls");
        int colHeights = FindColAny(ws, 2, warn, required: false, "work at heights");
        int colLift = FindColAny(ws, 2, warn, required: false, "lift", "requires using the lift");
        int colPesticide = FindColAny(ws, 2, warn, required: false, "pesticide", "pesticides");
        int colCitizen = FindColAny(ws, 2, warn, required: false, "citizen", "citizen technician");

        int colPreferred = FindColAny(ws, 2, warn, required: false, "should be serviced by", "should be serviced by specific");
        int colProhibited = FindColAny(ws, 2, warn, required: false, "should not be serviced", "should not");

        var sites = new List<ServiceSite>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int row = 4;
        while (true)
        {
            var locName = colLocationName > 0 ? ExcelParsingHelpers.GetString(ws.Cell(row, colLocationName)) : null;
            if (string.IsNullOrWhiteSpace(locName))
                break;

            var address = colAddress > 0 ? ExcelParsingHelpers.GetString(ws.Cell(row, colAddress)) : null;
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

            var currentTechRaw = colCurrentTech > 0 ? ExcelParsingHelpers.GetString(ws.Cell(row, colCurrentTech)) : null;
            var currentTechIds = ExcelParsingHelpers.ParseTechList(currentTechRaw, nameToId, warn);

            var bestAccessRaw = colBestAccess > 0 ? ExcelParsingHelpers.GetString(ws.Cell(row, colBestAccess)) : null;
            var bestAccess = ExcelParsingHelpers.ParseBestAccessMode(bestAccessRaw);

            var techsNeeded = (colTechsNeeded > 0 ? ExcelParsingHelpers.ParseInt(ws.Cell(row, colTechsNeeded)) : null) ?? 1;

            var permitRequired = colPermitRequired > 0 && ExcelParsingHelpers.ParseBool(ws.Cell(row, colPermitRequired));
            var permitDifficultyRaw = colPermitDifficulty > 0 ? ExcelParsingHelpers.GetString(ws.Cell(row, colPermitDifficulty)) : null;
            var permitDifficulty = ExcelParsingHelpers.ParsePermitDifficulty(permitDifficultyRaw);
            var techsWithPermitRaw = colTechsWithPermit > 0 ? ExcelParsingHelpers.GetString(ws.Cell(row, colTechsWithPermit)) : null;
            var permittedTechIds = permitRequired
                ? ExcelParsingHelpers.ParseTechList(techsWithPermitRaw, nameToId, warn)
                : new List<string>();

            var access = new List<TimeWindow>();
            foreach (var (day, (fromCol, toCol)) in dayCols)
            {
                var from = ExcelParsingHelpers.ParseTime(ws.Cell(row, fromCol));
                var to = ExcelParsingHelpers.ParseTime(ws.Cell(row, toCol));
                if (from is null || to is null) continue;

                access.Add(new TimeWindow { Day = day, OpenTime = from.Value, CloseTime = to.Value });
            }

            var visitFreqRaw = colVisitFreq > 0 ? ExcelParsingHelpers.GetString(ws.Cell(row, colVisitFreq)) : null;
            var (visitsPerInterval, intervalDays, lockAfterFirst) = ExcelParsingHelpers.ParseVisitFrequency(visitFreqRaw, warn);

            // Compatibility field for old algorithm: only if weekly interval
            var visitFreqency = intervalDays == 7 ? visitsPerInterval : (byte)0;

            var visitDurRaw = colVisitDur > 0 ? ExcelParsingHelpers.GetString(ws.Cell(row, colVisitDur)) : null;
            var visitDuration = (colVisitDur > 0 ? ExcelParsingHelpers.ParseMinutes(ws.Cell(row, colVisitDur)) : null) ?? 0;

            var skillReqRaw = colSkillReq > 0 ? ExcelParsingHelpers.GetString(ws.Cell(row, colSkillReq)) : null;
            var req = ExcelParsingHelpers.ParseSkillAndLevel(skillReqRaw);
            if (req is null)
            {
                warn($"Service site '{locName}': не вдалося розпізнати Service skill requirement='{skillReqRaw}'. Ставлю Exterior/None.");
            }

            var preferredRaw = colPreferred > 0 ? ExcelParsingHelpers.GetString(ws.Cell(row, colPreferred)) : null;
            var prohibitedRaw = colProhibited > 0 ? ExcelParsingHelpers.GetString(ws.Cell(row, colProhibited)) : null;

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

                RequiresPhysicallyDemandingJob = (colPhys > 0 && ExcelParsingHelpers.ParseBool(ws.Cell(row, colPhys))),
                RequiresGreenWallSkills = (colLivingWalls > 0 && ExcelParsingHelpers.ParseBool(ws.Cell(row, colLivingWalls))),
                RequiresWorkAtHeights = (colHeights > 0 && ExcelParsingHelpers.ParseBool(ws.Cell(row, colHeights))),
                RequiresUsingLift = (colLift > 0 && ExcelParsingHelpers.ParseBool(ws.Cell(row, colLift))),
                RequiresPesticide = (colPesticide > 0 && ExcelParsingHelpers.ParseBool(ws.Cell(row, colPesticide))),
                RequiresCitizenship = (colCitizen > 0 && ExcelParsingHelpers.ParseBool(ws.Cell(row, colCitizen))),

                PreferredTechIds = ExcelParsingHelpers.ParseTechList(preferredRaw, nameToId, warn),
                ProhibitedTechIds = ExcelParsingHelpers.ParseTechList(prohibitedRaw, nameToId, warn),
                PermittedTechIds = permittedTechIds
            };

            sites.Add(site);
            row++;
        }

        return sites;
    }

    private static int FindColAny(IXLWorksheet ws, int headerRow, Action<string>? warn, bool required, params string[] candidates)
    {
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 1;

        // Pre-normalize candidates
        var normalized = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => (raw: c, norm: ExcelParsingHelpers.NormalizeHeader(c)))
            .Where(x => !string.IsNullOrWhiteSpace(x.norm))
            .ToList();

        for (int col = 1; col <= lastCol; col++)
        {
            var v = ExcelParsingHelpers.GetString(ws.Cell(headerRow, col));
            if (string.IsNullOrWhiteSpace(v)) continue;

            var h = ExcelParsingHelpers.NormalizeHeader(v);
            if (string.IsNullOrWhiteSpace(h)) continue;

            foreach (var c in normalized)
            {
                if (h.Contains(c.norm, StringComparison.OrdinalIgnoreCase))
                    return col;

                // Token-based match: all tokens must be present
                var tokens = c.norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length > 1 && tokens.All(t => h.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    return col;
            }
        }

        var msg = $"Excel: не знайдено колонку (row={headerRow}) на листі '{ws.Name}'. Шукав: {string.Join(", ", candidates)}";
        if (required) throw new InvalidOperationException(msg);

        warn?.Invoke(msg);
        return -1;
    }

    private static int FindColContains(IXLWorksheet ws, int headerRow, string contains)
        => FindColAny(ws, headerRow, warn: null, required: true, contains);

    private static int FindColOptional(IXLWorksheet ws, int headerRow, Action<string> warn, params string[] candidates)
        => FindColAny(ws, headerRow, warn, required: false, candidates);
}

