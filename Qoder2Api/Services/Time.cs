using Microsoft.Extensions.Options;
using Qoder2Api.Configuration;

namespace Qoder2Api.Services;

public sealed class Time
{
    private readonly ILogger<Time> _log;

    public TimeOptions Options { get; }

    public TimeZoneInfo TimeZone { get; }

    public Time(IOptions<TimeOptions> options, ILogger<Time> log)
    {
        Options = options.Value;
        _log = log;
        TimeZone = ResolveTimeZone(Options.TimeZone, _log);
    }

    public DateTime NowLocal => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZone).DateTime;

    public DateTime NowLocalAt(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, TimeZone).DateTime;

    private static TimeZoneInfo ResolveTimeZone(string? id, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Local;
        }

        foreach (var candidate in new[] { id, id.Replace('_', ' ') })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(candidate);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // ignore
            }
        }

        log.LogWarning("配置项 Time:TimeZone = {Id} 无法识别，已回退本机时区 {Local}", id, TimeZoneInfo.Local.Id);
        return TimeZoneInfo.Local;
    }
}


