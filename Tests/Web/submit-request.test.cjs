const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../../Jellyfin.Plugin.AniWorld/Web/requests.js'), 'utf8');
const handler = source.slice(source.indexOf("  q('request').addEventListener('click', async () => {"), source.indexOf('  async function openCatalogDetail('));

function fixture(subtitleLanguage = 'none') {
  let click, resolve, reject;
  const events = [];
  const fields = { request: { addEventListener: (_, fn) => { click = fn; } }, language: { value: 'German Dub' }, provider: { value: 'Automatic' } };
  const context = vm.createContext({
    state: { detail: { title: 'Testserie', seriesUrl: 'https://example.test/show', source: 'sto', mediaType: 'series', subtitleLanguage, plan: { missing_count: 2, plan_token: 'preview' } } },
    q: (name) => fields[name], detailGeneration: 1, disposed: false, view: { isConnected: true },
    dialogNotice: (message, error) => events.push({ type: 'dialog', message, error }),
    notice: (message) => events.push({ type: 'notice', message }),
    closeDetail: () => events.push({ type: 'close' }),
    refreshStatus: () => events.push({ type: 'status' }),
    guardRequestButton: () => { if (context.state.status && context.state.status.maintenance) { fields.request.disabled = true; fields.request.textContent = 'Anfragen deaktiviert'; } },
    switchTab: () => { throw new Error('Submitting must not force a tab switch'); },
    call: (endpoint, options) => { events.push({ type: 'request', endpoint, options }); return new Promise((ok, fail) => { resolve = ok; reject = fail; }); },
  });
  vm.runInContext(handler, context);
  return { click: () => click(), context, fields, events, resolve: (v) => resolve(v), reject: (v) => reject(v) };
}

test('accepted background request confirms title without forcing navigation', async () => {
  const f = fixture('deu');
  const pending = f.click();
  assert.equal(f.fields.request.disabled, true);
  const sent = f.events.find((event) => event.type === 'request');
  assert.equal(sent.endpoint, 'Requests/Automatic');
  assert.equal(sent.options.body.subtitleLanguage, 'deu');
  assert.equal(sent.options.body.language, 'German Dub');
  f.resolve({ status: 'ready' });
  await pending;
  assert.ok(f.events.some((event) => event.type === 'notice' && event.message === 'Testserie wurde erfolgreich angefragt. Die Verarbeitung läuft im Hintergrund.'));
});

test('approval-required request confirms pending status and explicit no-subtitle default', async () => {
  const f = fixture();
  const pending = f.click();
  assert.equal(f.events.find((event) => event.type === 'request').options.body.subtitleLanguage, 'none');
  f.resolve({ status: 'pending' });
  await pending;
  assert.ok(f.events.some((event) => event.type === 'notice' && event.message.includes('wartet auf Freigabe')));
});

test('late submission response does not close a newly opened title', async () => {
  const f = fixture();
  const pending = f.click();
  f.context.detailGeneration++;
  f.resolve({ status: 'ready' });
  await pending;
  assert.equal(f.events.filter((event) => event.type === 'close').length, 0);
});

test('failed admission keeps dialog open and restores request button', async () => {
  const f = fixture();
  const pending = f.click();
  f.reject(new Error('Speicherung fehlgeschlagen'));
  await pending;
  assert.equal(f.fields.request.disabled, false);
  assert.equal(f.events.filter((event) => event.type === 'close').length, 0);
  assert.ok(f.events.some((event) => event.error && event.message === 'Speicherung fehlgeschlagen'));
});

test('maintenance keeps the request button disabled after a refused submission', async () => {
  const f = fixture();
  f.context.state.status = { maintenance: true };
  const pending = f.click();
  f.reject(new Error('Anfragen sind derzeit deaktiviert.'));
  await pending;
  assert.equal(f.fields.request.disabled, true);
  assert.equal(f.fields.request.textContent, 'Anfragen deaktiviert');
  // A refused submission re-reads the status so the banner appears immediately.
  assert.ok(f.events.some((event) => event.type === 'status'));
});
