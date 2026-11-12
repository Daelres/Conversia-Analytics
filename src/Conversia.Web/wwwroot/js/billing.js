// Tokenization of a card or a Nequi number in the browser (docs/BILLING.md, "Adding a payment method"). The card number, the CVC and the
// Nequi number go straight from this page to the gateway with its public key; the inputs have no name, so they are never posted to Conversia.
// Only the token the gateway returns (and, for a card, its brand and last four digits) is put in the form and sent.
(function () {
  'use strict';

  // The only API bases card data may go to (docs.wompi.co, "Ambientes y llaves"); anything else in the page is refused.
  var WOMPI_BASES = ['https://sandbox.wompi.co/v1', 'https://production.wompi.co/v1'];

  function field(form, name) { return form.querySelector('[data-billing="' + name + '"]'); }

  function say(form, text) {
    var box = field(form, 'status');
    if (box) { box.textContent = text; box.hidden = !text; }
  }

  function wait(ms) { return new Promise(function (resolve) { setTimeout(resolve, ms); }); }

  async function call(form, method, path, body) {
    if (WOMPI_BASES.indexOf(form.dataset.apiBase) < 0) { throw new Error('host'); }
    var response = await fetch(form.dataset.apiBase + path, {
      method: method,
      headers: { 'Authorization': 'Bearer ' + form.dataset.publicKey, 'Content-Type': 'application/json' },
      body: body ? JSON.stringify(body) : undefined,
      credentials: 'omit',
    });
    var json = null;
    try { json = await response.json(); } catch (e) { json = null; }
    if (!response.ok || !json || !json.data) { throw new Error('gateway'); }
    return json.data;
  }

  async function tokenizeCard(form) {
    var data = await call(form, 'POST', '/tokens/cards', {
      number: field(form, 'number').value.replace(/\s+/g, ''),
      cvc: field(form, 'cvc').value.trim(),
      exp_month: field(form, 'exp-month').value.trim(),
      exp_year: field(form, 'exp-year').value.trim(),
      card_holder: field(form, 'holder').value.trim(),
    });
    field(form, 'brand').value = (data.brand || '').replace(/[^A-Za-z]/g, '').slice(0, 20);
    field(form, 'last-four').value = /^[0-9]{4}$/.test(data.last_four || '') ? data.last_four : '';
    return data.id;
  }

  async function tokenizeNequi(form) {
    var data = await call(form, 'POST', '/tokens/nequi', { phone_number: field(form, 'phone').value.replace(/\D+/g, '') });
    say(form, 'Abre tu aplicación Nequi y acepta la suscripción de Conversia. Esperando tu aprobación...');
    for (var i = 0; i < 60; i++) {
      await wait(3000);
      var state = await call(form, 'GET', '/tokens/nequi/' + encodeURIComponent(data.id));
      if (state.status === 'APPROVED') { return data.id; }
      if (state.status === 'DECLINED' || state.status === 'ERROR') { throw new Error('declined'); }
    }
    throw new Error('timeout');
  }

  function wire(form) {
    if (form.dataset.billingWired) { return; }
    if (WOMPI_BASES.indexOf(form.dataset.apiBase) < 0) { return; }
    form.dataset.billingWired = '1';
    var type = field(form, 'type');
    var groups = form.querySelectorAll('[data-billing-group]');
    function show() {
      groups.forEach(function (g) { g.hidden = g.dataset.billingGroup !== type.value; });
    }
    type.addEventListener('change', show);
    show();
    field(form, 'no-js').hidden = true;

    form.addEventListener('submit', async function (event) {
      if (field(form, 'token').value) { return; }
      event.preventDefault();
      var button = form.querySelector('button[type="submit"]');
      button.disabled = true;
      say(form, 'Validando el medio de pago con Wompi...');
      try {
        field(form, 'token').value = type.value === 'Nequi' ? await tokenizeNequi(form) : await tokenizeCard(form);
        say(form, 'Guardando...');
        form.submit();
      } catch (e) {
        button.disabled = false;
        say(form, e.message === 'declined' ? 'Nequi rechazó la suscripción.'
          : e.message === 'timeout' ? 'No llegó la aprobación de Nequi. Inténtalo de nuevo.'
          : 'Wompi no aceptó los datos. Revísalos e inténtalo de nuevo.');
      }
    });
  }

  function init() { document.querySelectorAll('form[data-billing-form]').forEach(wire); }

  if (document.readyState === 'loading') { document.addEventListener('DOMContentLoaded', init); } else { init(); }
  if (window.Blazor && window.Blazor.addEventListener) { window.Blazor.addEventListener('enhancedload', init); }
})();
