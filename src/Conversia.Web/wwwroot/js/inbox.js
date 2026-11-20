// Near-real-time refresh by polling (docs/MESSAGING.md, "Inbox"). Without JavaScript nothing here runs and the pages stay as drawn
// (each has an "Actualizar" link or a reload, and no read receipt is sent). Driven by data attributes the server writes:
//  - [data-poll-url]: the inbox list. Asks for the rows with the last ETag; a 304 changes nothing, a 200 replaces the block's content.
//  - [data-version-url][data-version]: the chat of an open conversation. When the version differs (a new message, a status change, the
//    24-hour window opening or closing), the page is fetched again and the chat and both composers are replaced; text being typed is kept.
//  - [data-read-url]: posted once, when the page is visible, to mark the customer's messages as read (only drawn when the company has it on).
//  - textarea[data-typing-url]: "the advisor is typing", posted at most once every 20 seconds while the person types.
// Lifecycle: every element gets a generation token, so Blazor's enhanced navigation never starts a second loop or a second listener on it;
// a navigation aborts the requests in flight; errors back off exponentially (capped at 60 s); a 401 or 403 stops the loop and shows a hint
// to sign in again; polling pauses while the tab is hidden. Manual checklist: docs/MESSAGING.md, "Inbox".
(function () {
  'use strict';

  var TYPING_INTERVAL_MS = 20000;
  var MAX_BACKOFF_MS = 60000;
  var generation = 0;
  var controller = null;
  var timers = [];

  function stopAll() {
    timers.forEach(function (t) { clearTimeout(t); });
    timers = [];
    if (controller) {
      controller.abort();
    }
    controller = typeof AbortController === 'function' ? new AbortController() : null;
  }

  function signal() {
    return controller ? controller.signal : undefined;
  }

  function schedule(fn, ms) {
    timers.push(setTimeout(fn, ms));
  }

  function seconds(el) {
    var value = parseInt(el.getAttribute('data-poll-seconds') || '5', 10);
    return isNaN(value) || value < 2 ? 5 : value;
  }

  // Listeners and the read receipt are bound once per element for good (an element Blazor keeps across navigations is not bound twice).
  function bindOnce(el, kind) {
    var key = 'data-' + kind + '-bound';
    if (el.hasAttribute(key)) {
      return false;
    }
    el.setAttribute(key, '');
    return true;
  }

  // Loops are claimed per generation: init() of a new generation restarts them (the old ones see they are stale and stop).
  function claim(el, kind) {
    var key = 'data-' + kind + '-gen';
    if (el.getAttribute(key) === String(generation)) {
      return false;
    }
    el.setAttribute(key, String(generation));
    return true;
  }

  function antiforgery() {
    var token = document.querySelector('input[name="__RequestVerificationToken"]');
    var body = new FormData();
    if (token) {
      body.append('__RequestVerificationToken', token.value);
    }
    return body;
  }

  function signedOut(block) {
    if (block.querySelector('[data-testid="poll-signed-out"]')) {
      return;
    }
    var hint = document.createElement('p');
    hint.className = 'muted';
    hint.setAttribute('data-testid', 'poll-signed-out');
    hint.textContent = 'Tu sesión terminó o ya no tienes acceso: la lista dejó de actualizarse. Vuelve a iniciar sesión y recarga la página.';
    block.appendChild(hint);
  }

  // One polling loop: tick() returns a promise of the next delay (or null to stop). Pauses while hidden; backs off on errors.
  function loop(el, interval, tick, mine) {
    var failures = 0;

    function run() {
      if (mine !== generation || !document.body.contains(el)) {
        return;
      }
      if (document.hidden) {
        schedule(run, interval);
        return;
      }
      tick().then(function (outcome) {
        if (outcome === 'stop') {
          return;
        }
        failures = outcome === 'error' ? failures + 1 : 0;
        schedule(run, failures === 0 ? interval : Math.min(interval * Math.pow(2, failures), MAX_BACKOFF_MS));
      });
    }

    schedule(run, interval);
  }

  function outcomeOf(response, block) {
    if (response.status === 401 || response.status === 403) {
      signedOut(block);
      return 'stop';
    }
    return response.ok || response.status === 304 ? 'ok' : 'error';
  }

  function pollList(block, mine) {
    var etag = null;
    loop(block, seconds(block) * 1000, function () {
      var headers = { 'Accept': 'text/html' };
      if (etag) {
        headers['If-None-Match'] = etag;
      }
      return fetch(block.getAttribute('data-poll-url'), { headers: headers, credentials: 'same-origin', cache: 'no-cache', signal: signal() })
        .then(function (response) {
          var outcome = outcomeOf(response, block);
          if (response.status === 200) {
            etag = response.headers.get('ETag');
            return response.text().then(function (html) {
              if (mine === generation) {
                block.innerHTML = html;
              }
              return outcome;
            });
          }
          return outcome;
        })
        .catch(function () { return 'error'; });
    }, mine);
  }

  // Replaces the chat and the composers with the fresh page's, keeping what the person was typing (by element id).
  function refreshConversation(card, fresh) {
    var typed = {};
    document.querySelectorAll('[data-testid$="composer"] textarea, [data-testid="template-composer"] input[type="text"]').forEach(function (field) {
      if (field.id && field.value) {
        typed[field.id] = field.value;
      }
    });

    ['[data-testid="composer"]', '[data-testid="template-composer"]'].forEach(function (selector) {
      var current = document.querySelector(selector);
      var next = fresh.querySelector(selector);
      if (current && next) {
        current.replaceWith(document.importNode(next, true));
      }
    });

    var freshCard = fresh.querySelector('[data-testid="chat-card"]');
    card.innerHTML = freshCard.innerHTML;
    card.setAttribute('data-version', freshCard.getAttribute('data-version') || '');

    Object.keys(typed).forEach(function (id) {
      var field = document.getElementById(id);
      if (field && !field.value) {
        field.value = typed[id];
      }
    });
    document.querySelectorAll('textarea[data-typing-url]').forEach(typing);
  }

  function pollConversation(card, mine) {
    var interval = seconds(card) * 1000;
    loop(card, interval, function () {
      return fetch(card.getAttribute('data-version-url'), { headers: { 'Accept': 'application/json' }, credentials: 'same-origin', cache: 'no-cache', signal: signal() })
        .then(function (response) {
          var outcome = outcomeOf(response, card);
          if (!response.ok) {
            return outcome;
          }
          return response.json().then(function (body) {
            if (!body || body.version === card.getAttribute('data-version')) {
              return outcome;
            }
            return fetch(location.href, { credentials: 'same-origin', cache: 'no-cache', signal: signal() })
              .then(function (page) { return page.ok ? page.text() : null; })
              .then(function (html) {
                var fresh = html ? new DOMParser().parseFromString(html, 'text/html') : null;
                if (fresh && fresh.querySelector('[data-testid="chat-card"]') && mine === generation) {
                  refreshConversation(card, fresh);
                }
                return outcome;
              });
          });
        })
        .catch(function () { return 'error'; });
    }, mine);
  }

  function markRead(card) {
    var post = function () {
      document.removeEventListener('visibilitychange', post);
      if (document.hidden) {
        document.addEventListener('visibilitychange', post);
        return;
      }
      // Fire and forget: whatever happens, nothing changes for the advisor (the API logs a failure and waits before trying again).
      fetch(card.getAttribute('data-read-url'), { method: 'POST', body: antiforgery(), credentials: 'same-origin', signal: signal() }).catch(function () { });
    };
    post();
  }

  function typing(area) {
    if (!bindOnce(area, 'typing')) {
      return;
    }
    var last = 0;
    area.addEventListener('input', function () {
      var now = Date.now();
      if (now - last < TYPING_INTERVAL_MS || !area.value.trim()) {
        return;
      }
      last = now;
      fetch(area.getAttribute('data-typing-url'), { method: 'POST', body: antiforgery(), credentials: 'same-origin', signal: signal() }).catch(function () { });
    });
  }

  function init() {
    generation += 1;
    var mine = generation;
    stopAll();
    document.querySelectorAll('[data-poll-url]').forEach(function (block) {
      if (claim(block, 'poll')) {
        pollList(block, mine);
      }
    });
    document.querySelectorAll('[data-version-url][data-version]').forEach(function (card) {
      if (claim(card, 'poll')) {
        pollConversation(card, mine);
      }
      if (card.getAttribute('data-read-url') && bindOnce(card, 'read')) {
        markRead(card);
      }
    });
    document.querySelectorAll('textarea[data-typing-url]').forEach(typing);
  }

  if (document.readyState === 'loading') { document.addEventListener('DOMContentLoaded', init); } else { init(); }
  if (window.Blazor && window.Blazor.addEventListener) { window.Blazor.addEventListener('enhancedload', init); }
})();
