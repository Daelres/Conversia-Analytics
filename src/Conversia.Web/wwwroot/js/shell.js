// Page-shell behaviour that needs JavaScript: the theme toggle, and keeping the shell intact across Blazor's enhanced navigations.
// The saved theme is first applied before the first paint by the small inline script of App.razor; without JavaScript the page
// follows the system colour scheme and the toggle stays hidden (the "js" class on <html> reveals it).
(function () {
  var root = document.documentElement;
  var KEY = "dr-theme";

  function savedTheme() {
    try {
      var theme = localStorage.getItem(KEY);
      return theme === "light" || theme === "dark" ? theme : null;
    } catch (e) {
      return null;
    }
  }

  function current() {
    var forced = root.dataset.theme;
    if (forced === "light" || forced === "dark") {
      return forced;
    }
    return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  }

  function apply(theme) {
    root.dataset.theme = theme;
    try {
      localStorage.setItem(KEY, theme);
    } catch (e) {
      /* private mode: the choice just lasts for this page */
    }
  }

  // Blazor patches the <html> attributes with the ones of the server's page on an enhanced navigation, which drops
  // data-theme and the "js" class; put both back.
  function restoreShell() {
    root.classList.add("js");
    var theme = root.dataset.theme === "light" || root.dataset.theme === "dark" ? root.dataset.theme : savedTheme();
    if (theme) {
      root.dataset.theme = theme;
    }
  }

  // The page is not reloaded, so move focus to the new page's heading for keyboard and screen-reader users. On a full load the
  // first Tab stop must stay the skip link, which is why this is not done there (nor when the URL names a fragment to jump to).
  function focusHeading() {
    if (location.hash) {
      return;
    }
    var heading = document.querySelector("main h1");
    if (heading) {
      if (!heading.hasAttribute("tabindex")) {
        heading.setAttribute("tabindex", "-1");
      }
      heading.focus({ preventScroll: false });
    }
  }

  // Copy buttons ("data-copy-target" names the element whose text is copied). The text never leaves the page: it goes to the clipboard
  // only, and the result is announced in the polite live region of the same section.
  function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) {
      return navigator.clipboard.writeText(text);
    }
    return new Promise(function (resolve, reject) {
      var area = document.createElement("textarea");
      area.value = text;
      area.setAttribute("readonly", "");
      area.className = "sr-only";
      document.body.appendChild(area);
      area.select();
      var ok = false;
      try {
        ok = document.execCommand("copy");
      } catch (e) {
        ok = false;
      }
      document.body.removeChild(area);
      ok ? resolve() : reject(new Error("copy failed"));
    });
  }

  function announce(button, message) {
    var section = button.closest("section") || document;
    var status = section.querySelector("[data-copy-status]");
    if (status) {
      status.textContent = "";
      window.setTimeout(function () { status.textContent = message; }, 30);
    }
  }

  document.addEventListener("click", function (event) {
    var toggle = event.target.closest("[data-theme-toggle]");
    if (toggle) {
      apply(current() === "dark" ? "light" : "dark");
      return;
    }

    var copy = event.target.closest("[data-copy-target]");
    if (copy) {
      var source = document.querySelector(copy.getAttribute("data-copy-target"));
      if (source) {
        copyText(source.textContent.trim()).then(
          function () { announce(copy, copy.getAttribute("data-copy-done") || "Copiado"); },
          function () { announce(copy, "No se pudo copiar: selecciona el texto y cópialo a mano."); }
        );
      }
    }
  });

  if (window.Blazor && typeof window.Blazor.addEventListener === "function") {
    window.Blazor.addEventListener("enhancedload", function () {
      restoreShell();
      focusHeading();
    });
  }
})();
