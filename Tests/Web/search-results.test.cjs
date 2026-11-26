const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

// Exercise the actual rendering function without adding a browser framework dependency.
const source = fs.readFileSync(path.join(__dirname, '../../Jellyfin.Plugin.AniWorld/Web/requests.js'), 'utf8');
const renderer = source.slice(source.indexOf('  async function appendResults('), source.indexOf('  function createMediaCard('));
function fixture(onYield = () => {}) {
  const cards = [];
  const context = vm.createContext({
    disposed: false, searchGeneration: 1, view: { isConnected: true },
    document: { createElement: () => ({}) },
    createMediaCard: (item, source, label) => ({ item, source, label }),
    setTimeout: (callback) => { onYield(context); callback(); },
  });
  vm.runInContext(renderer, context);
  return { context, cards, host: { insertBefore: (card) => cards.push(card) } };
}
const group = (count) => ({ source: 'sto', label: 'SerienStream', data: { results: Array.from({ length: count }, (_, i) => ({ title: `Titel ${i}` })) } });

test('renders every upstream hit in order and yields for large lists', async () => {
  let yields = 0;
  const f = fixture(() => yields++);
  const result = await f.context.appendResults(f.host, [group(150)], {}, 1);
  assert.equal(result.count, 150);
  assert.equal(result.errors, 0);
  assert.equal(yields, 6);
  assert.deepEqual(f.cards.map((card) => card.item.title), Array.from({ length: 150 }, (_, i) => `Titel ${i}`));
});

test('a newer search stops an older render after yielding', async () => {
  const f = fixture((context) => { context.searchGeneration++; });
  await f.context.appendResults(f.host, [group(150)], {}, 1);
  assert.equal(f.cards.length, 24);
});

test('disposing the view stops rendering', async () => {
  const f = fixture((context) => { context.disposed = true; });
  await f.context.appendResults(f.host, [group(150)], {}, 1);
  assert.equal(f.cards.length, 24);
});

test('one failing source does not suppress successful hits', async () => {
  const f = fixture();
  const result = await f.context.appendResults(f.host, [{ source: 'other', label: 'Other', error: 'HTTP 500' }, group(2)], {}, 1);
  assert.equal(result.errors, 1);
  assert.equal(result.count, 2);
  assert.equal(f.cards[0].textContent, 'Other: HTTP 500');
});
