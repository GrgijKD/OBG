using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Amazon.Lambda.Core;

namespace ObgLambda;

/// <summary>
/// Тимчасовий loader для Excel як джерела вхідних даних.
/// На цьому етапі ми лише знаходимо *.xlsx у папці input і зчитуємо байти файлу.
/// Парсинг Excel у бізнес-моделі та конвертація в JSON буде додана пізніше.
/// </summary>
public static class ExcelInputLoader
{
    /// <summary>
    /// Повертає перший *.xlsx файл з папки input.
    /// Порядок пошуку директорій:
    /// 1) ENV: OBG_INPUT_DIR (якщо задано)
    /// 2) {AppContext.BaseDirectory}/input (актуально для Lambda /var/task)
    /// 3) {Directory.GetCurrentDirectory()}/input (зручно для локального запуску)
    /// </summary>
    public static ExcelInputFile? TryLoadFirstExcel(ILambdaLogger? logger = null)
    {
        var candidateDirs = new List<string>();

        var envDir = Environment.GetEnvironmentVariable("OBG_INPUT_DIR");
        if (!string.IsNullOrWhiteSpace(envDir))
        {
            candidateDirs.Add(envDir);
        }

        candidateDirs.Add(Path.Combine(AppContext.BaseDirectory, "input"));
        candidateDirs.Add(Path.Combine(Directory.GetCurrentDirectory(), "input"));

        foreach (var dir in candidateDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    logger?.LogLine($"[ExcelInputLoader] Папка не знайдена: {dir}");
                    continue;
                }

                var excelFiles = Directory.GetFiles(dir, "*.xlsx", SearchOption.TopDirectoryOnly)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (excelFiles.Count == 0)
                {
                    logger?.LogLine($"[ExcelInputLoader] У папці немає *.xlsx: {dir}");
                    continue;
                }

                var fullPath = excelFiles[0];
                var bytes = File.ReadAllBytes(fullPath);

                return new ExcelInputFile(fullPath, Path.GetFileName(fullPath), bytes);
            }
            catch (Exception ex)
            {
                logger?.LogLine($"[ExcelInputLoader] Помилка читання Excel з папки '{dir}': {ex}");
            }
        }

        return null;
    }
}

public record ExcelInputFile(string FullPath, string FileName, byte[] Content);
