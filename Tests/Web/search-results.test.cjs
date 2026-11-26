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
  assert.equal(f.cards[0].textContent, 'Other konnte nicht durchsucht werden, die übrigen Quellen werden angezeigt. (HTTP 500)');
});

test('a real match in a later source removes earlier filler and moves ahead', async () => {
  const f = fixture();
  const list = [];
  const make = (item, source) => { const card = { item, source, remove() { list.splice(list.indexOf(card), 1); } }; return card; };
  f.context.createMediaCard = (item, source) => make(item, source);
  const host = { insertBefore: (card, ref) => { const at = ref && list.includes(ref) ? list.indexOf(ref) : list.length; list.splice(at, 0, card); } };
  const hits = (source, titles) => ({ source, label: source, data: { results: titles.map(([title, relevance]) => ({ title, relevance })) } });
  await f.context.appendResults(host, [hits('mangafire', [['2x2xX', 0.333], ['We Never Broke Up', 0.333]])], {}, 1);
  await f.context.appendResults(host, [hits('localized', [['Ohne Bewertung', undefined]])], {}, 1);
  await f.context.appendResults(host, [hits('moflix', [['2 Broke Girls', 1], ['Alles steht Kopf 2', 0.333]])], {}, 1);
  await f.context.appendResults(host, [hits('sto', [['2 Broke Girls', 1], ['Broke', 0.333]])], {}, 1);
  assert.deepEqual(list.map((card) => card.source + ':' + card.item.title), ['moflix:2 Broke Girls', 'sto:2 Broke Girls', 'localized:Ohne Bewertung']);
});

test('a hit without poster borrows the poster of the same title from another source', async () => {
  const f = fixture();
  const covers = [];
  const makeCard = (item, source) => ({ item, source, firstChild: null, insertBefore(cover) { covers.push([source, cover]); } });
  f.context.createMediaCard = (item, source) => makeCard(item, source);
  f.context.document = { createElement: () => ({}) };
  vm.runInContext('function loadCover(image, poster) { image.poster = poster; }', f.context);
  const host = { insertBefore() {} };
  await f.context.appendResults(host, [{ source: 'sto', label: 'SerienStream', data: { results: [{ title: '2 Broke Girls', relevance: 1 }] } }], {}, 1);
  await f.context.appendResults(host, [{ source: 'moflix', label: 'Moflix', data: { results: [{ title: '2 Broke Girls', poster_url: 'https://img.test/2bg.jpg', relevance: 1 }] } }], {}, 1);
  assert.deepEqual(covers.map(([source, cover]) => [source, cover.poster]), [['sto', 'https://img.test/2bg.jpg']]);
});
