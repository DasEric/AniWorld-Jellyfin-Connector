const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

// Exercise the real Escape handler of the Jellyfin Web injection without a browser.
const source = fs.readFileSync(path.join(__dirname, '../../Jellyfin.Plugin.AniWorld/Web/injection.js'), 'utf8');
const handler = source.slice(source.indexOf('  function handleKeydown(event) {'), source.indexOf('  function handleViewShow(event) {'));

function fixture({ detail = '', deletion = '' } = {}) {
  const clicks = [];
  let closedPage = 0;
  const node = (name, display) => ({ style: { display }, click: () => clicks.push(name) });
  const nodes = {
    '[data-mf="overlay"]': node('detail', detail),
    '[data-mf="delete-overlay"]': node('deletion', deletion),
    '[data-mf="close"]': node('detail-close', ''),
    '[data-mf="delete-cancel"]': node('delete-cancel', ''),
  };
  const overlay = { querySelector: (selector) => nodes[selector] || null };
  const context = vm.createContext({ getOverlay: () => overlay, closeRequests: () => { closedPage++; } });
  vm.runInContext(handler, context);
  const press = () => context.handleKeydown({ key: 'Escape', preventDefault() {}, stopPropagation() {} });
  return { press, clicks, closedPage: () => closedPage };
}

test('Escape closes the requests page when no dialog was ever opened', () => {
  // Dialogs start hidden by CSS, so their inline display is still empty.
  const f = fixture();
  f.press();
  assert.equal(f.closedPage(), 1);
  assert.deepEqual(f.clicks, []);
});

test('Escape closes an open detail dialog but keeps the page', () => {
  const f = fixture({ detail: 'flex' });
  f.press();
  assert.equal(f.closedPage(), 0);
  assert.deepEqual(f.clicks, ['detail-close']);
});

test('Escape cancels the delete preview before anything else', () => {
  const f = fixture({ detail: 'none', deletion: 'flex' });
  f.press();
  assert.equal(f.closedPage(), 0);
  assert.deepEqual(f.clicks, ['delete-cancel']);
});
