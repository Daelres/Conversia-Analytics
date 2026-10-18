namespace Conversia.Web.Services;

/// <summary>
/// Outcome of a product write, carried across the post/redirect/get as a fixed code so that no
/// posted value is ever echoed back through the query string.
/// </summary>
public static class ProductsNotice
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Deleted = "deleted";

    public static string Url(string code) => $"/products?notice={code}";

    public static string? Describe(string? code) => code switch
    {
        Created => "Producto creado.",
        Updated => "Producto guardado.",
        Deleted => "Producto eliminado.",
        _ => null
    };
}
