using System.Net;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>
/// Requirement "ui-structure": the structural and accessibility quality bar of the redesigned Web, checked over the whole page/state
/// matrix of <see cref="UiStructureMatrix"/> (fetched once, shared by every test of this class). One test per rule group; each reports
/// every page that breaks the rule, with the offending markup. The rules live in <see cref="UiStructureAssertions"/> (its page-type
/// table says which rule applies to which kind of page); the helper itself is exercised in <see cref="UiStructureAssertionsTests"/>.
///
/// <para>Markup hooks these tests pin (everything else — class names, layout, colours, wording — is free):
/// <c>data-page</c> (login, register, home, users, not-found, error, forbidden; an HTML 400 is an "error" page),
/// <c>data-chip</c> (owner/permission) and <c>data-theme-toggle</c>.</para>
/// </summary>
[Collection(nameof(WebIntegrationCollection))]
public class UiStructureTests(TestApiFactory api)
{
    private async Task<UiStructureMatrix> MatrixAsync() => await UiStructureMatrix.GetAsync(api);

    private async Task AssertRuleAsync(Func<UiPageCase, UiAssets, IEnumerable<string>> rule)
    {
        var matrix = await MatrixAsync();
        var problems = matrix.Cases.SelectMany(page => rule(page, matrix.Assets)).ToList();
        AssertNone(problems, matrix.Cases.Count);
    }

    private Task AssertRuleAsync(Func<UiPageCase, IEnumerable<string>> rule) => AssertRuleAsync((page, _) => rule(page));

    private static void AssertNone(IReadOnlyList<string> problems, int inspected, string unit = "pages")
        => Assert.True(problems.Count == 0, $"{problems.Count} problem(s) over {inspected} {unit}:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");

    /// <summary>
    /// Runs a CSS rule over the CSS that applies to each page (its linked sheets, the sheets they import and its inline styles), never over
    /// the concatenation of every page's CSS. Pages with the very same CSS are reported once.
    /// </summary>
    private async Task AssertCssRuleAsync(Func<string, IEnumerable<string>> rule)
    {
        var matrix = await MatrixAsync();
        var problems = matrix.Cases
            .GroupBy(page => matrix.Assets.CssOf(page.Page))
            .SelectMany(group =>
            {
                var others = group.Count() - 1;
                var owner = others == 0 ? $"[{group.First().Label}]" : $"[{group.First().Label} and {others} more page(s) with the same CSS]";
                return rule(group.Key).Select(problem => $"{owner} {problem}");
            })
            .ToList();
        AssertNone(problems, matrix.Cases.Count);
    }

    // ---- the matrix itself ----

    [Fact]
    public async Task The_matrix_covers_every_page_type_anonymous_and_signed_in()
    {
        var matrix = await MatrixAsync();

        foreach (var type in Enum.GetValues<UiPageType>())
        {
            Assert.Contains(matrix.Cases, c => c.Type == type);
        }

        Assert.Contains(matrix.Cases, c => c.Authenticated);
        Assert.Contains(matrix.Cases, c => !c.Authenticated);
        Assert.Contains(matrix.Cases, c => c.ExpectAlert);
        Assert.Contains(matrix.Cases, c => c.ExpectStatus);
        Assert.Contains(matrix.Cases, c => c.InvalidFields.Length > 0);
        Assert.Equal(matrix.Cases.Count, matrix.Cases.Select(c => c.Label).Distinct().Count());

        // The status codes the error pages are pinned to: the 403 page of /users, the throttled sign-in (re-rendered with 200) and the antiforgery 400.
        foreach (var route in new[] { "/users" })
        {
            Assert.Contains(matrix.Cases, c => c.Type == UiPageType.Forbidden && c.Page.RequestUri.AbsolutePath == route && c.Page.Status == 403);
        }

        Assert.Contains(matrix.Cases, c => c.Type == UiPageType.Login && c.Label.Contains("429", StringComparison.Ordinal) && c.Page.Status == 200 && c.ExpectAlert);
        Assert.Contains(matrix.Cases, c => c.Page.Status == 400 && c.Type == UiPageType.Error && !c.Authenticated);
        Assert.Contains(matrix.Cases, c => c.Page.Status == 400 && c.Type == UiPageType.Error && c.Authenticated);

        // A 403/404 answer is the forbidden/not-found page and nothing else; the other types are real pages (a 5xx is only the "backend down" state).
        Assert.All(matrix.Cases.Where(c => c.Type is UiPageType.Forbidden), c => Assert.Equal(403, c.Page.Status));
        Assert.All(matrix.Cases.Where(c => c.Type is UiPageType.NotFound), c => Assert.True(c.Page.Status is 404 || c.Page.RequestUri.AbsolutePath == "/not-found", c.Label));
        Assert.All(matrix.Cases.Where(c => c.Type is not (UiPageType.Forbidden or UiPageType.NotFound)), c => Assert.True(c.Page.Status is 200 or 503 or 500 or 409 or 400, $"{c.Label}: {c.Page.Status}"));
    }

    // ---- 1. document ----

    [Fact]
    public Task Every_page_declares_language_charset_viewport_title_color_scheme_a_description_on_public_pages_and_a_working_non_default_favicon()
        => AssertRuleAsync(UiStructureAssertions.Document);

    // ---- 2. landmarks and headings ----

    [Fact]
    public Task Every_page_has_one_main_with_an_id_a_skip_link_first_a_banner_and_footer_on_the_shell_and_labelled_navigation()
        => AssertRuleAsync(UiStructureAssertions.Landmarks);

    [Fact]
    public async Task Every_in_page_link_resolves_against_the_base_url_to_the_page_being_viewed_plus_a_fragment_naming_an_element_of_the_page()
    {
        var matrix = await MatrixAsync();

        // Every page carries at least its skip link, so a rule that quietly finds nothing to judge is caught here.
        var withoutAnchors = matrix.Cases.Where(c => !c.Document.QuerySelectorAll("a[href], area[href]").Any(a => UiStructureAssertions.IsInPageAnchor(c.Page, a))).Select(c => c.Label).ToList();
        AssertNone(withoutAnchors.Select(label => $"[{label}] has no in-page link (not even the skip link).").ToList(), matrix.Cases.Count);

        await AssertRuleAsync(UiStructureAssertions.InPageLinks);
    }

    [Fact]
    public Task Every_page_has_exactly_one_h1_and_never_skips_a_heading_level()
        => AssertRuleAsync(UiStructureAssertions.Headings);

    [Fact]
    public Task The_navigation_marks_exactly_the_current_section_with_aria_current_page()
        => AssertRuleAsync(UiStructureAssertions.Navigation);

    // ---- 3. own design system ----

    [Fact]
    public Task No_page_references_bootstrap_and_every_stylesheet_is_served_by_the_site_as_cacheable_text_css()
        => AssertRuleAsync(UiStructureAssertions.StylesheetLinks);

    [Fact]
    public async Task The_bootstrap_files_are_no_longer_served()
    {
        var matrix = await MatrixAsync();
        Assert.NotEmpty(matrix.LegacyBootstrap);
        var served = matrix.LegacyBootstrap.Where(f => f.Value.Status != (int)HttpStatusCode.NotFound).Select(f => $"{f.Key} answers {f.Value.Status}").ToList();
        AssertNone(served, matrix.LegacyBootstrap.Count, "files");
    }

    [Fact]
    public Task The_css_of_every_page_has_dark_scheme_reduced_motion_focus_visible_and_a_root_block_of_design_tokens()
        => AssertCssRuleAsync(UiStructureAssertions.DesignSystemCss);

    // ---- 4. forms and controls ----

    [Fact]
    public Task Every_control_button_link_and_summary_has_an_accessible_name_ids_are_unique_images_have_alt_and_svg_are_hidden_or_labelled()
        => AssertRuleAsync(UiStructureAssertions.ControlNames);

    [Fact]
    public Task Errors_are_announced_as_alerts_and_confirmations_as_status()
        => AssertRuleAsync(UiStructureAssertions.LiveRegions);

    [Fact]
    public Task Rejected_fields_expose_their_message_with_aria_invalid_and_aria_describedby()
        => AssertRuleAsync(UiStructureAssertions.InvalidFields);

    [Fact]
    public Task Password_email_and_numeric_inputs_have_type_autocomplete_and_inputmode()
        => AssertRuleAsync(UiStructureAssertions.InputHints);

    // ---- 5. tables ----

    [Fact]
    public Task Tables_have_a_caption_scoped_headers_end_aligned_numbers_and_a_narrow_screen_strategy()
        => AssertRuleAsync(UiStructureAssertions.Tables);

    // ---- 6. app-shell integrity ----

    [Fact]
    public Task There_is_exactly_one_logout_form_on_signed_in_pages_and_none_on_anonymous_ones()
        => AssertRuleAsync(UiStructureAssertions.LogoutForm);

    [Fact]
    public Task The_navigation_is_one_element_without_duplicated_links()
        => AssertRuleAsync(UiStructureAssertions.NavigationNotDuplicated);

    [Fact]
    public Task The_mobile_menu_toggle_works_without_javascript()
        => AssertRuleAsync(UiStructureAssertions.MobileMenuToggle);

    [Fact]
    public Task No_page_uses_inline_style_attributes()
        => AssertRuleAsync(UiStructureAssertions.NoInlineStyles);

    [Fact]
    public Task At_most_one_tiny_inline_script_and_every_script_file_comes_from_the_site()
        => AssertRuleAsync(UiStructureAssertions.Scripts);

    // ---- 7. theme toggle ----

    [Fact]
    public Task Every_page_offers_a_named_theme_toggle_button_that_never_submits_a_form()
        => AssertRuleAsync(UiStructureAssertions.ThemeToggle);

    [Fact]
    public Task The_css_of_every_page_supports_forced_light_and_dark_themes()
        => AssertCssRuleAsync(UiStructureAssertions.ThemeOverridesCss);

    // ---- 8. error answers ----

    [Fact]
    public Task An_html_400_answer_is_an_error_page_about_an_invalid_or_expired_request_and_never_a_not_found_page()
        => AssertRuleAsync(UiStructureAssertions.RejectedRequestPage);

    // ---- 9. content hooks ----

    [Fact]
    public Task Every_page_wraps_its_main_content_in_the_data_page_region_of_its_own_type()
        => AssertRuleAsync(UiStructureAssertions.PageRegion);

    [Fact]
    public Task The_profile_card_marks_the_owner_and_permission_chips()
        => AssertRuleAsync(UiStructureAssertions.ProfileChipHooks);
}
