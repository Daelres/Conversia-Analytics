using Conversia.Domain.Common;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace Conversia.API.Localization;

/// <summary>
/// Everything that makes the API answer in Spanish whatever the caller asks for: one fixed culture (no negotiation, so
/// <c>Accept-Language</c> changes nothing), the model-binding messages, the titles of status-code problem documents and the
/// validation problem, whose texts that come from exceptions (malformed JSON, wrong value types) are replaced by Spanish ones.
/// </summary>
public static class SpanishApiExtensions
{
    private static readonly Dictionary<int, string> ClientErrorTitles = new()
    {
        [StatusCodes.Status400BadRequest] = ProblemTexts.InvalidRequestTitle,
        [StatusCodes.Status401Unauthorized] = "No autenticado",
        [StatusCodes.Status403Forbidden] = "Acceso denegado",
        [StatusCodes.Status404NotFound] = "No encontrado",
        [StatusCodes.Status405MethodNotAllowed] = "Método no permitido",
        [StatusCodes.Status406NotAcceptable] = "Formato no aceptable",
        [StatusCodes.Status409Conflict] = "Conflicto",
        [StatusCodes.Status415UnsupportedMediaType] = "Tipo de contenido no admitido",
        [StatusCodes.Status422UnprocessableEntity] = "No se pudo procesar la solicitud",
        [StatusCodes.Status500InternalServerError] = ProblemTexts.UnexpectedTitle,
    };

    public static IMvcBuilder AddSpanishMessages(this IMvcBuilder mvc)
    {
        mvc.AddMvcOptions(options =>
        {
            // Every string property of a request carries an explicit rule with a Spanish message; the implicit
            // "The X field is required." that nullable annotations would add is English and would repeat it.
            options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
            ConfigureBindingMessages(options.ModelBindingMessageProvider);
        });
        mvc.ConfigureApiBehaviorOptions(options =>
        {
            foreach (var (status, title) in ClientErrorTitles)
            {
                if (options.ClientErrorMapping.TryGetValue(status, out var data))
                {
                    data.Title = title;
                }
            }

            options.InvalidModelStateResponseFactory = ValidationProblem;
        });
        return mvc;
    }

    /// <summary>Always Spanish: the only supported culture, and no provider that could pick another.</summary>
    public static IApplicationBuilder UseSpanishCulture(this IApplicationBuilder app)
        => app.UseRequestLocalization(new RequestLocalizationOptions
        {
            DefaultRequestCulture = new RequestCulture(SpanishCulture.Info),
            SupportedCultures = [SpanishCulture.Info],
            SupportedUICultures = [SpanishCulture.Info],
            RequestCultureProviders = [],
        });

    private static IActionResult ValidationProblem(ActionContext context)
    {
        var factory = context.HttpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();
        var problem = factory.CreateValidationProblemDetails(
            context.HttpContext, SpanishModelState.Localize(context.ModelState), StatusCodes.Status400BadRequest, ProblemTexts.ValidationTitle);

        return new BadRequestObjectResult(problem)
        {
            ContentTypes = { "application/problem+json", "application/problem+xml" },
        };
    }

    private static void ConfigureBindingMessages(DefaultModelBindingMessageProvider messages)
    {
        messages.SetMissingBindRequiredValueAccessor(_ => ProblemTexts.ValueRequired);
        messages.SetMissingKeyOrValueAccessor(() => ProblemTexts.ValueRequired);
        messages.SetMissingRequestBodyRequiredValueAccessor(() => ProblemTexts.BodyRequired);
        messages.SetValueMustNotBeNullAccessor(_ => ProblemTexts.ValueRequired);
        messages.SetAttemptedValueIsInvalidAccessor((_, _) => ProblemTexts.ValueInvalid);
        messages.SetNonPropertyAttemptedValueIsInvalidAccessor(_ => ProblemTexts.ValueInvalid);
        messages.SetUnknownValueIsInvalidAccessor(_ => ProblemTexts.ValueInvalid);
        messages.SetNonPropertyUnknownValueIsInvalidAccessor(() => ProblemTexts.ValueInvalid);
        messages.SetValueIsInvalidAccessor(_ => ProblemTexts.ValueInvalid);
        messages.SetValueMustBeANumberAccessor(_ => ProblemTexts.ValueNotNumber);
        messages.SetNonPropertyValueMustBeANumberAccessor(() => ProblemTexts.ValueNotNumber);
    }
}
