using AngleSharp.Html.Dom;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>The name/value pairs a browser would submit for a form, plus where it would submit them.</summary>
internal sealed class FormModel
{
    public const string AntiforgeryField = "__RequestVerificationToken";

    private readonly List<KeyValuePair<string, string>> _fields;

    private FormModel(Uri actionUri, List<KeyValuePair<string, string>> fields)
    {
        ActionUri = actionUri;
        _fields = fields;
    }

    /// <summary>A form that is not on any page: the fields a client would send to <paramref name="actionUri"/> (the forged-request case).</summary>
    public FormModel(Uri actionUri, string field, string value, string antiforgeryToken)
        : this(actionUri, [new KeyValuePair<string, string>(field, value), new KeyValuePair<string, string>(AntiforgeryField, antiforgeryToken)])
    {
    }

    public Uri ActionUri { get; private set; }

    public IReadOnlyList<KeyValuePair<string, string>> Fields => _fields;

    public static FormModel From(PageResponse page, IHtmlFormElement form)
    {
        Assert.Equal("post", (form.GetAttribute("method") ?? "get").ToLowerInvariant());

        var action = form.GetAttribute("action");
        var actionUri = string.IsNullOrEmpty(action) ? page.RequestUri : new Uri(page.RequestUri, action);

        var fields = new List<KeyValuePair<string, string>>();
        foreach (var control in form.QuerySelectorAll("input, textarea, select"))
        {
            var name = control.GetAttribute("name");
            var type = control.GetAttribute("type")?.ToLowerInvariant();
            if (string.IsNullOrEmpty(name) || type is "submit" or "button" or "image" or "reset" or "file"
                || control.HasAttribute("disabled"))
            {
                continue;
            }

            if (type is "checkbox" or "radio" && !control.HasAttribute("checked"))
            {
                continue;
            }

            var value = control is IHtmlTextAreaElement textArea ? textArea.Value : control.GetAttribute("value") ?? string.Empty;
            fields.Add(new KeyValuePair<string, string>(name, value));
        }

        return new FormModel(actionUri, fields);
    }

    public string? Get(string field)
        => _fields.FirstOrDefault(f => Matches(f.Key, field)).Value;

    /// <summary>Sets the first field whose last name segment is <paramref name="field"/> (e.g. "email" matches "Input.Email").</summary>
    public FormModel Set(string field, string value)
    {
        var index = _fields.FindIndex(f => Matches(f.Key, field));
        Assert.True(index >= 0, $"Form has no '{field}' field; fields: {string.Join(", ", _fields.Select(f => f.Key))}");
        _fields[index] = new KeyValuePair<string, string>(_fields[index].Key, value);
        return this;
    }

    /// <summary>Removes the field entirely, as a client that never sent it would (static-SSR binding then sees no value at all).</summary>
    public FormModel Without(string field)
    {
        var removed = _fields.RemoveAll(f => Matches(f.Key, field));
        Assert.True(removed > 0, $"Form has no '{field}' field; fields: {string.Join(", ", _fields.Select(f => f.Key))}");
        return this;
    }

    /// <summary>Points the submission somewhere else (the forged-target case: the same fields posted to another resource).</summary>
    public FormModel WithAction(Uri actionUri)
    {
        ActionUri = actionUri;
        return this;
    }

    /// <summary>Appends a field the form does not have (a forged extra value).</summary>
    public FormModel Add(string name, string value)
    {
        _fields.Add(new KeyValuePair<string, string>(name, value));
        return this;
    }

    public FormModel WithoutAntiforgeryToken()
    {
        _fields.RemoveAll(f => f.Key == AntiforgeryField);
        return this;
    }

    public FormModel WithAntiforgeryToken(string token)
    {
        WithoutAntiforgeryToken();
        _fields.Add(new KeyValuePair<string, string>(AntiforgeryField, token));
        return this;
    }

    /// <summary>Rewrites every occurrence of <paramref name="from"/> in field values (the forged-identifier case).</summary>
    public FormModel ReplaceInValues(string from, string to)
    {
        for (var i = 0; i < _fields.Count; i++)
        {
            _fields[i] = new KeyValuePair<string, string>(_fields[i].Key, _fields[i].Value.Replace(from, to, StringComparison.OrdinalIgnoreCase));
        }

        return this;
    }

    private static bool Matches(string name, string field)
    {
        var lastSegment = name[(name.LastIndexOfAny(new[] { '.', ']' }) + 1)..];
        return string.Equals(lastSegment, field, StringComparison.OrdinalIgnoreCase);
    }
}
