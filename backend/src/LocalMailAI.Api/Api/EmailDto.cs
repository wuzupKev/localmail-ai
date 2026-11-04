using System.Globalization;

namespace LocalMailAI.Api.Api;

// These types are a literal copy of Email in src/lib/mock.ts. They are not changed
// without changing the frontend first: §3 of BACKEND.md states those types are the
// contract.
//
// They go out in camelCase because that is what System.Text.Json does by default, and
// optional properties are omitted when null rather than serialised as null: in
// TypeScript "extracted?: {...}" means "X | undefined", not "X | null".

public sealed record MailboxDto(string Name, string Email);

public sealed record SignalDto(string Label, string Sign);

public sealed record ExtractedDto(string Action, string Urgency, string? DueDate);

public sealed record EmailDto
{
    public required string Id { get; init; }
    public required MailboxDto From { get; init; }
    public required string Subject { get; init; }
    public required string Preview { get; init; }
    public required string[] Body { get; init; }
    public required string Time { get; init; }
    public required string Bucket { get; init; }
    public required double Confidence { get; init; }
    public required SignalDto[] Signals { get; init; }
    public required int ThreadCount { get; init; }
    public required bool Unread { get; init; }
    public ExtractedDto? Extracted { get; init; }
    public string? Summary { get; init; }
}

public sealed record EmailPageDto(EmailDto[] Items, string? NextCursor);

/// <summary>
/// Counts for the tiles on the main screen. "unsure" is the set of messages below
/// UMBRAL_CONFIANZA, which are not counted in their bucket.
/// </summary>
public sealed record CountsDto(
    int Reply,
    int Fyi,
    int Automated,
    int Noise,
    int Unsure,
    int Unread);

/// <summary>
/// The contract's <c>time</c> field: "9:14" if today, "Ayer", "Lun", or "12 oct".
///
/// The culture is pinned to es-ES on purpose. Using the system culture would make the
/// same API return "Mon" or "Mo" depending on the machine the backend runs on, and the
/// frontend would have no way to know what it is going to receive. The strings are
/// Spanish because they are user-facing copy for a Spanish interface.
/// </summary>
public static class RelativeTime
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    public static string Format(DateTimeOffset when, DateTimeOffset now)
    {
        var day = when.LocalDateTime.Date;
        var today = now.LocalDateTime.Date;
        var days = (today - day).Days;

        return days switch
        {
            <= 0 => when.LocalDateTime.ToString("H:mm", Es),
            1 => "Ayer",
            // Within the week, the weekday already locates the message; a date would
            // be more precise and less useful.
            < 7 => Capitalise(Es.DateTimeFormat.GetAbbreviatedDayName(day.DayOfWeek)),
            _ => when.LocalDateTime.ToString("d MMM", Es).Replace(".", string.Empty),
        };
    }

    private static string Capitalise(string s) =>
        s.Length == 0 ? s : char.ToUpper(s[0], Es) + s[1..];
}
