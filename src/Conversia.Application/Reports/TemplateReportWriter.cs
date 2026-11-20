using System.Text;

namespace Conversia.Application.Reports;

/// <summary>
/// The deterministic Spanish report (docs/REPORTS.md, "Fallback"): written from the data pack alone, with fixed sentences and the pack's own
/// numbers and names, so it is grounded by construction (a property test runs the validator over thousands of random packs). It is what the
/// managers get whenever the model is not used or its answer is refused: AI switched off, quota used, provider down, or two invalid answers.
/// </summary>
public static class TemplateReportWriter
{
    public static ReportContent Write(ReportDataPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var indicators = Indicators(pack).Take(ReportContent.MaxIndicators).ToList();
        if (indicators.Count == 0)
        {
            indicators.Add(new ReportIndicator("Periodo", pack.Period.Days == 1 ? "Un día" : $"{pack.Period.Days} días", "Los datos de este informe cubren ese periodo."));
        }

        var actions = Actions(pack).Distinct().Take(ReportContent.MaxActions).ToList();
        if (actions.Count == 0)
        {
            actions.Add("Mantener el ritmo del equipo y revisar el próximo informe.");
        }

        return new ReportContent(
            Fit($"Resumen {pack.Frequency} · {pack.Period.Label}", ReportContent.TitleMaxLength),
            indicators,
            Diagnosis(pack),
            actions,
            Coverage(pack.Coverage));
    }

    private static IEnumerable<ReportIndicator> Indicators(ReportDataPack pack)
    {
        if (pack.Headline is { } headline)
        {
            yield return Conversion(headline);
        }

        if (pack.Response is { } response)
        {
            var value = response.MedianMinutes is { } median ? $"{ReportText.Decimal(median)} min (mediana)" : "Sin respuestas";
            var note = new StringBuilder();
            if (response.WithinSlaPercent is { } within)
            {
                note.Append($"El {ReportText.Percent(within)} se respondió en menos de {response.SlaMinutes} minutos.");
            }

            note.Append(response.OpenUnansweredNow switch
            {
                0 => " Hoy ninguna conversación abierta espera respuesta.",
                1 => " Hoy hay una conversación abierta sin respuesta.",
                var n => $" Hoy hay {ReportText.Count(n)} conversaciones abiertas sin respuesta.",
            });
            yield return new ReportIndicator("Primera respuesta", value, Fit(note.ToString().Trim(), ReportContent.NoteMaxLength));
        }

        if (pack.Headline is { } volume)
        {
            yield return new ReportIndicator(
                "Conversaciones", ReportText.Count(volume.Conversations),
                $"Ventas cerradas hasta ahora: {ReportText.Count(volume.Won)}. El periodo anterior tuvo {ReportText.Count(volume.PreviousConversations)} conversaciones.");
        }

        if (pack.DropOff is { MainStage: { } stage, MainStageCount: { } count })
        {
            yield return new ReportIndicator("Se detienen en", Fit(stage, ReportContent.ValueMaxLength),
                $"{ReportText.Count(count)} de {ReportText.Count(pack.DropOff.Read)} conversaciones sin venta se detuvieron en ese paso.");
        }

        if (pack.Losses is { TopReasons: [var reason, ..] })
        {
            yield return new ReportIndicator("Motivo de pérdida principal", Fit(reason.Label, ReportContent.ValueMaxLength),
                $"Mencionado en {ReportText.Count(reason.Count)} ventas perdidas.");
        }
    }

    // The conversion of the latest window whose conversations are all decided, labelled with its dates: never a rate of a young period.
    private static ReportIndicator Conversion(ReportHeadline headline)
    {
        var current = headline.Conversion;
        var label = Fit($"Conversión {current.Label}", ReportContent.LabelMaxLength);
        if (current.Rate is not { } rate)
        {
            return new ReportIndicator(label, "Sin datos suficientes",
                $"Hubo {ReportText.Count(current.Decided)} conversaciones decididas; para medir la conversión se necesitan al menos {headline.MinSample}.");
        }

        var note = $"{ReportText.Count(current.Won)} ventas de {ReportText.Count(current.Decided)} conversaciones decididas.";
        if (headline.ConversionChangePoints is { } change && headline.PreviousConversion.Rate is { } previous)
        {
            var direction = change switch
            {
                < 0 => $"la conversión bajó {ReportText.Points(change)}",
                > 0 => $"la conversión subió {ReportText.Points(change)}",
                _ => "la conversión quedó igual",
            };
            note += $" Frente al periodo {headline.PreviousConversion.Label} ({ReportText.Percent(previous)}), {direction}.";
        }

        return new ReportIndicator(label, ReportText.Percent(rate), Fit(note, ReportContent.NoteMaxLength));
    }

    private static string Diagnosis(ReportDataPack pack)
    {
        var sentences = new List<string>();
        if (pack.Headline is { Conversations: 0 })
        {
            sentences.Add("En este periodo no hubo conversaciones con clientes en el alcance del informe.");
        }

        if (Branches(pack) is ({ } worst, { } best))
        {
            sentences.Add($"En las conversaciones ya decididas {pack.Conversion!.WindowLabel}, la sucursal {worst.Name} tuvo la conversión más baja ({ReportText.Percent(worst.ConversionRate!.Value)}) y {best.Name} la más alta ({ReportText.Percent(best.ConversionRate!.Value)}).");
        }

        if (pack.DropOff is { MainStage: { } stage, MainStageCount: { } count } dropOff)
        {
            var share = dropOff.MainStageSharePercent is { } s ? $", el {ReportText.Percent(s)}" : string.Empty;
            // "La mayoría" only when it is one: above half of the conversations whose step was read.
            sentences.Add(dropOff.MainStageSharePercent > 50
                ? $"De las conversaciones que terminaron sin venta, la mayoría se detuvo en «{stage}» ({ReportText.Count(count)}{share})."
                : $"De las conversaciones que terminaron sin venta, el paso donde más se detuvieron fue «{stage}» ({ReportText.Count(count)}{share}).");
        }

        if (pack.DropOff is { MovedOffChannel: > 0 and var moved })
        {
            sentences.Add($"{ReportText.Count(moved)} conversaciones siguieron por llamada, en la sede o por otro canal.");
        }

        if (pack.Losses is { } losses)
        {
            if (losses.TopReasons is [var reason, ..])
            {
                sentences.Add($"El motivo de pérdida más mencionado fue «{reason.Label}» ({ReportText.Count(reason.Count)} ventas).");
            }

            if (losses.NoResponse > 0)
            {
                sentences.Add($"{ReportText.Count(losses.NoResponse)} ventas se perdieron porque el cliente dejó de responder.");
            }

            if (losses.TopObjections.FirstOrDefault(o => o.Count > 0) is { } objection)
            {
                sentences.Add($"La objeción más frecuente fue «{objection.Label}» ({ReportText.Count(objection.Count)} conversaciones).");
            }
        }

        if (pack.Response is { OpenOverSlaNow: > 0 } response)
        {
            var oldest = response.OldestWaitingHours is { } hours ? $"; la más antigua espera {ReportText.Decimal(hours)} horas" : string.Empty;
            sentences.Add($"Hoy {ReportText.Count(response.OpenOverSlaNow)} conversaciones abiertas llevan más de {response.SlaMinutes} minutos sin respuesta{oldest}.");
        }

        if (sentences.Count == 0)
        {
            sentences.Add("No hay alertas en este periodo.");
        }

        var text = new StringBuilder();
        foreach (var sentence in sentences)
        {
            if (text.Length + sentence.Length + 1 > ReportContent.DiagnosisMaxLength)
            {
                break;
            }

            text.Append(text.Length == 0 ? string.Empty : " ").Append(sentence);
        }

        return text.ToString();
    }

    private static IEnumerable<string> Actions(ReportDataPack pack)
    {
        if (pack.Response is { OpenUnansweredNow: > 0 and var open })
        {
            yield return open == 1
                ? "Responder hoy la conversación abierta que espera respuesta."
                : $"Responder hoy las {ReportText.Count(open)} conversaciones abiertas que esperan respuesta.";
        }

        if (Branches(pack) is ({ } worst, _))
        {
            yield return $"Revisar con la sucursal {worst.Name} cómo atiende las conversaciones y acompañar a su equipo.";
        }

        if (pack.DropOff?.MainStage is { } stage && StageAction(stage) is { } stageAction)
        {
            yield return stageAction;
        }

        if (pack.Losses is { TopReasons: [var reason, ..] })
        {
            yield return reason.Label == ReportText.LossReason("Price")
                ? "Cuando el precio sea la duda, ofrecer opciones de pago, paquetes o la promoción vigente."
                : $"Preparar una respuesta para el motivo «{reason.Label}» y compartirla con el equipo.";
        }

        if (pack.Losses is { NoResponse: > 0 })
        {
            yield return "Escribir al día siguiente a quienes dejan de responder, con una pregunta concreta.";
        }

        if (pack.Response is { OpenOverSlaNow: > 0 })
        {
            yield return "Reforzar la atención en las horas con más mensajes para responder más rápido.";
        }
    }

    private static string? StageAction(string stage)
    {
        if (stage == ReportText.ReachedStage("PriceShared"))
        {
            return "Revisar cómo se presenta el precio: acompañarlo del beneficio y de una opción de pago.";
        }

        if (stage == ReportText.ReachedStage("ObjectionRaised"))
        {
            return "Preparar respuestas claras a las objeciones más comunes y compartirlas con el equipo.";
        }

        if (stage == ReportText.ReachedStage("FirstContact") || stage == ReportText.ReachedStage("NeedsIdentified"))
        {
            return "Hacer una pregunta en el primer mensaje para entender qué busca el cliente y proponer el siguiente paso.";
        }

        if (stage == ReportText.ReachedStage("AppointmentProposed"))
        {
            return "Proponer la cita con fecha y hora concretas y confirmarla en el mismo chat.";
        }

        if (stage == ReportText.ReachedStage("AppointmentBooked"))
        {
            return "Confirmar las citas agendadas el día anterior y enviar la ubicación de la sede.";
        }

        return stage == ReportText.ReachedStage("PaymentOrDeposit") ? "Facilitar el pago del abono con datos claros en el mismo chat." : null;
    }

    // The branches with the lowest and the highest rate, among those with enough decided conversations to be compared; null below two.
    private static (ReportGroupRate? Worst, ReportGroupRate? Best) Branches(ReportDataPack pack)
    {
        if (pack.Conversion is not { } conversion)
        {
            return (null, null);
        }

        var rated = conversion.ByBranch.Where(b => b.ConversionRate is not null && b.Decided >= conversion.MinSample).OrderBy(b => b.ConversionRate).ToList();
        return rated.Count >= 2 && rated[0].ConversionRate < rated[^1].ConversionRate ? (rated[0], rated[^1]) : (null, null);
    }

    private static string? Coverage(ReportCoverage? coverage)
    {
        if (coverage is null)
        {
            return null;
        }

        if (!coverage.AiEnabled)
        {
            return "La lectura con IA no está activa: los pasos y motivos se leen con reglas automáticas, que encuentran menos detalles.";
        }

        var text = coverage.ReadSharePercent is { } read
            ? $"La IA leyó el {ReportText.Percent(read)} de las conversaciones sin venta del periodo; el resto se completa a medida que las lee."
            : "La IA está activa y completa los pasos y motivos a medida que lee las conversaciones.";
        if (coverage.LastUpdatedOn is { } last)
        {
            text += $" Su última lectura fue el {ReportText.Date(last)}.";
        }

        return Fit(text, ReportContent.CoverageNoteMaxLength);
    }

    private static string Fit(string text, int maxLength) => text.Length <= maxLength ? text : text[..(maxLength - 1)].TrimEnd() + "…";
}
