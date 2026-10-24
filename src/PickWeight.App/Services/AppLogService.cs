using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PickWeight.Core.Models;

namespace PickWeight_App.Services;

/// <summary>
/// 本地诊断日志：按日滚动、分级记录、自动清理过期文件，并可导出
/// 「日志 + 脱敏设置」的诊断包供问题反馈。日志内容刻意不包含成员
/// 姓名或名单数据；诊断包中的设置也会去除本地 API 令牌。
/// </summary>
public sealed class AppLogService
{
    private const int RetentionDays = 30;
    private readonly string _logDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateOnly _lastRetentionSweep;

    public AppLogService(string? logDirectory = null) =>
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PickWeight", "logs");

    /// <summary>今天的日志文件路径。</summary>
    public string CurrentLogPath => FilePathFor(DateOnly.FromDateTime(DateTime.Now));

    public Task InfoAsync(string message, CancellationToken cancellationToken = default) =>
        WriteAsync("INFO", message, cancellationToken);

    public Task WarningAsync(string message, CancellationToken cancellationToken = default) =>
        WriteAsync("WARN", message, cancellationToken);

    public Task ErrorAsync(string message, CancellationToken cancellationToken = default) =>
        WriteAsync("ERROR", message, cancellationToken);

    /// <summary>读取今天的日志内容；无文件时返回空字符串。</summary>
    public async Task<string> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(CurrentLogPath))
        {
            return string.Empty;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await File.ReadAllTextAsync(CurrentLogPath, Encoding.UTF8, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 导出诊断包：全部滚动日志 + 脱敏后的全局设置（去除本地 API 令牌），
    /// 打包为 zip 写入指定路径。
    /// </summary>
    public async Task ExportDiagnosticsAsync(
        AppSettings settings,
        string destinationZipPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZipPath);

        var sanitized = settings.Clone();
        sanitized.LocalApiToken = string.Empty;

        var destinationDirectory = Path.GetDirectoryName(destinationZipPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        var temporaryPath = destinationZipPath + ".tmp";
        await using (var archiveStream = File.Create(temporaryPath))
        {
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create);
            foreach (var logPath in EnumerateLogFiles())
            {
                var entry = archive.CreateEntry(Path.GetFileName(logPath), CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                await using var source = File.OpenRead(logPath);
                await source.CopyToAsync(entryStream, cancellationToken);
            }

            var settingsEntry = archive.CreateEntry("settings.json", CompressionLevel.Optimal);
            using var settingsEntryStream = settingsEntry.Open();
            await JsonSerializer.SerializeAsync(
                settingsEntryStream,
                sanitized,
                DiagnosticsJsonContext.Default.AppSettings,
                cancellationToken);
        }

        File.Move(temporaryPath, destinationZipPath, true);
    }

    private IEnumerable<string> EnumerateLogFiles()
    {
        if (!Directory.Exists(_logDirectory))
        {
            return [];
        }

        return Directory.GetFiles(_logDirectory, "pickweight-*.log")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private async Task WriteAsync(string level, string message, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_logDirectory);
            SweepRetentionIfNeeded();
            var line = $"{DateTimeOffset.Now:O} {level} {message}{Environment.NewLine}";
            await File.AppendAllTextAsync(CurrentLogPath, line, Encoding.UTF8, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>每天首次写入时清理超过保留期的旧日志；失败不阻塞记录。</summary>
    private void SweepRetentionIfNeeded()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_lastRetentionSweep == today || !Directory.Exists(_logDirectory))
        {
            return;
        }

        _lastRetentionSweep = today;
        try
        {
            var cutoff = today.AddDays(-RetentionDays);
            foreach (var path in EnumerateLogFiles())
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name.Length >= "pickweight-yyyyMMdd".Length
                    && DateOnly.TryParseExact(
                        name["pickweight-".Length..],
                        "yyyyMMdd",
                        out var date)
                    && date < cutoff)
                {
                    File.Delete(path);
                }
            }
        }
        catch
        {
            // 清理失败不影响当天日志记录。
        }
    }

    private string FilePathFor(DateOnly date) => Path.Combine(_logDirectory, $"pickweight-{date:yyyyMMdd}.log");
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class DiagnosticsJsonContext : JsonSerializerContext;
