using Conversia.Application.Reports;

namespace Conversia.Application.UnitTests.Reports;

/// <summary>Synthetic data packs: one fixed and readable, and random ones for the property tests.</summary>
internal static class ReportPacks
{
    public static ReportDataPack Sample() => new(
        ReportDataPack.CurrentVersion,
        "Láser Piloto",
        "semanal",
        new ReportPackPeriod(new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 15), 7, "semana del 9 al 15 de noviembre de 2026"),
        "Todas las sucursales",
        ["Headline", "Conversion", "DropOff", "LossReasons", "Response", "AiCoverage"],
        new ReportHeadline(Conversations: 1234, Won: 18, PreviousConversations: 110, MaturityDays: 7, MinSample: 20,
            new ReportConversionRate(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 8), "del 2 al 8 de noviembre", 80, 18, 23.5),
            new ReportConversionRate(new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1), "del 26 de octubre al 1 de noviembre", 75, 21, 28.0),
            ConversionChangePoints: -4.5),
        new ReportConversion(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 8), "del 2 al 8 de noviembre", 23.5, 20,
            [new("Centro", 70, 45, 13, 28.9), new("Norte", 50, 35, 5, 14.3)],
            [new("Laura Gómez", 60, 40, 11, 27.5), new("Sin asesor", 10, 5, 0, 0.0)],
            [new("Anuncios", 90, 60, 12, 20.0), new("Orgánico", 30, 20, 6, 30.0)]),
        new ReportDropOff(Stopped: 62, Read: 50, MainStage: "Se compartió el precio", MainStageCount: 21, MainStageSharePercent: 42.0, MovedOffChannel: 9),
        new ReportLosses(TotalLost: 40, NoResponse: 25, NoResponseSharePercent: 62.5,
            [new("Precio", 9, 60.0), new("Ubicación", 3, 20.0)],
            [new("Precio", 30, 25.0), new("Momento", 12, 10.0)]),
        new ReportResponseTimes(SlaMinutes: 30, MedianMinutes: 12.25, WithinSlaPercent: 81.3, Answered: 110, OpenUnansweredNow: 6, OpenOverSlaNow: 4,
            OldestWaitingHours: 3.2, [new("Norte", 35.5, 52.0, 45)]),
        new ReportCoverage(AiEnabled: true, Stopped: 62, NotRead: 12, ReadSharePercent: 80.6, LastUpdatedOn: new DateOnly(2026, 11, 16)));

    private static readonly string[] BranchNames = ["Centro", "Norte", "Sur", "Chapinero", "Laureles", "El Poblado"];
    private static readonly string[] AdvisorNames = ["Laura Gómez", "Andrés Ruiz", "María José Peña", "Sin asesor", "Camilo Díaz"];
    private static readonly string[] Stages = ["Primer contacto", "Necesidad identificada", "Se compartió el precio", "Planteó una objeción", "Se propuso una cita"];
    private static readonly string[] Reasons = ["Precio", "Ubicación", "Disponibilidad", "No era el momento", "Eligió a la competencia"];

    /// <summary>A pack with random numbers and a random subset of sections (each may be missing, empty or null-rated).</summary>
    public static ReportDataPack Random(Random random)
    {
        double? Rate() => random.Next(5) == 0 ? null : Math.Round(random.NextDouble() * 100, 1);
        int Count(int max = 500) => random.Next(0, max);
        bool Has() => random.Next(4) != 0;

        var current = Rate();
        var previous = Rate();
        var conversations = Count(2000);
        var windowFrom = new DateOnly(2026, 1, 1).AddDays(random.Next(0, 700));
        ReportConversionRate Window(DateOnly from, double? rate)
            => new(from, from.AddDays(6), ReportText.RangeLabel(from, from.AddDays(7)), Count(300), Count(100), rate);
        var headline = Has()
            ? new ReportHeadline(conversations, Count(), Count(2000), 7, 20, Window(windowFrom, current), Window(windowFrom.AddDays(-7), previous),
                current is { } c && previous is { } p ? Math.Round(c - p, 1) : null)
            : null;
        var conversion = Has()
            ? new ReportConversion(windowFrom, windowFrom.AddDays(6), ReportText.RangeLabel(windowFrom, windowFrom.AddDays(7)), current, 20,
                BranchNames.Take(random.Next(0, 4)).Select(n => new ReportGroupRate(n, Count(), Count(100), Count(50), Rate())).ToList(),
                AdvisorNames.Take(random.Next(0, 5)).Select(n => new ReportGroupRate(n, Count(), Count(100), Count(50), Rate())).ToList(),
                new[] { "Anuncios", "Orgánico", "Origen desconocido" }.Take(random.Next(0, 4)).Select(n => new ReportGroupRate(n, Count(), Count(100), Count(50), Rate())).ToList())
            : null;
        var stopped = Count();
        var dropOff = Has()
            ? (random.Next(4) == 0
                ? new ReportDropOff(stopped, 0, null, null, null, Count(50))
                : new ReportDropOff(stopped, Count(), Stages[random.Next(Stages.Length)], Count(), Rate(), Count(50)))
            : null;
        var losses = Has()
            ? new ReportLosses(Count(), Count(), Rate(),
                Reasons.Take(random.Next(0, 4)).Select(r => new ReportShare(r, Count(100), Rate())).ToList(),
                new[] { "Precio", "Momento", "Confianza" }.Take(random.Next(0, 3)).Select(r => new ReportShare(r, Count(100), Rate())).ToList())
            : null;
        var oldest = random.Next(3) == 0 ? (double?)null : Math.Round(random.NextDouble() * 80, 1);
        var response = Has()
            ? new ReportResponseTimes(30, random.Next(4) == 0 ? null : Math.Round(random.NextDouble() * 300, 2), Rate(), Count(), Count(30), Count(30), oldest,
                BranchNames.Take(random.Next(0, 3)).Select(n => new ReportGroupResponse(n, Math.Round(random.NextDouble() * 200, 2), Rate(), Count())).ToList())
            : null;
        var coverage = Has() ? new ReportCoverage(random.Next(2) == 0, stopped, Count(), Rate(), random.Next(2) == 0 ? null : new DateOnly(2026, 11, 16)) : null;
        var from = new DateOnly(2026, 1, 1).AddDays(random.Next(0, 700));
        return new ReportDataPack(ReportDataPack.CurrentVersion, "Empresa de Prueba", "semanal",
            new ReportPackPeriod(from, from.AddDays(6), 7, ReportText.PeriodLabel(Conversia.Domain.Enums.ReportFrequency.Weekly, from, from.AddDays(7))),
            random.Next(2) == 0 ? "Todas las sucursales" : "Sucursales: Centro y Norte",
            ["Headline"], headline, conversion, dropOff, losses, response, coverage);
    }
}
