using System.Globalization;

namespace LocalMailAI.Api.Api;

// Estos tipos son la copia literal de Email en src/lib/mock.ts. No se tocan sin
// cambiar antes el front: §3 de BACKEND.md dice que esos tipos son el contrato.
//
// Van en camelCase porque es lo que hace System.Text.Json por defecto, y las
// propiedades opcionales se omiten cuando son null en lugar de serializarse como
// null: en TypeScript "extracted?: {...}" es "X | undefined", no "X | null".

public sealed record MailboxDto(string Name, string Email);

public sealed record SignalDto(string Label, string Sign);

public sealed record ExtractedDto(string Accion, string Urgencia, string? FechaLimite);

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
/// Recuentos de los tiles de la pantalla principal. "duda" son los correos por
/// debajo de UMBRAL_CONFIANZA, que no se cuentan en su bucket.
/// </summary>
public sealed record CountsDto(
    int Responder,
    int Informativo,
    int Automatizado,
    int Ruido,
    int Duda,
    int Unread);

/// <summary>
/// El campo <c>time</c> del contrato: "9:14" si es hoy, "Ayer", "Lun", o "12 oct".
///
/// La cultura esta fijada a es-ES a proposito. Si se usara la del sistema, la misma
/// API devolveria "Mon" o "Mo" segun la maquina donde corre el backend, y el front
/// no tiene forma de saber que le va a llegar.
/// </summary>
public static class RelativeTime
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    public static string Format(DateTimeOffset cuando, DateTimeOffset ahora)
    {
        var dia = cuando.LocalDateTime.Date;
        var hoy = ahora.LocalDateTime.Date;
        var dias = (hoy - dia).Days;

        return dias switch
        {
            <= 0 => cuando.LocalDateTime.ToString("H:mm", Es),
            1 => "Ayer",
            // Dentro de la semana, el dia de la semana ya situa el correo; poner la
            // fecha seria mas preciso y menos util.
            < 7 => Capitalizar(Es.DateTimeFormat.GetAbbreviatedDayName(dia.DayOfWeek)),
            _ => cuando.LocalDateTime.ToString("d MMM", Es).Replace(".", string.Empty),
        };
    }

    private static string Capitalizar(string s) =>
        s.Length == 0 ? s : char.ToUpper(s[0], Es) + s[1..];
}
