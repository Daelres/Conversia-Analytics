using Conversia.API.Localization;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Conversia.API.ModelBinding;

/// <summary>
/// Binds a <see cref="Permission"/> from a name (case-insensitive) or a
/// number. Enum parsing alone silently accepts out-of-range numbers and comma
/// lists (which it combines as flags, so "1,2" would even equal a defined
/// value), so anything that is not a single catalog value is a model-state
/// error, which <c>[ApiController]</c> turns into a 400 ValidationProblemDetails.
/// </summary>
public class DefinedPermissionModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var raw = bindingContext.ValueProvider.GetValue(bindingContext.ModelName).FirstValue;

        if (TryParseSingleValue(raw, out var permission))
        {
            bindingContext.Result = ModelBindingResult.Success(permission);
        }
        else
        {
            bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, ProblemTexts.PermissionNotRecognized);
        }

        return Task.CompletedTask;
    }

    private static bool TryParseSingleValue(string? raw, out Permission permission)
    {
        permission = default;
        return raw is not null
            && !raw.Contains(',')
            && Enum.TryParse(raw, ignoreCase: true, out permission)
            && Enum.IsDefined(permission);
    }
}
