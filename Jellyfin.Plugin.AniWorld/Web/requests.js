export default function (view, params) {
  if (view.dataset.mfInitialized === '1') return;
  view.dataset.mfInitialized = '1';
  const api = typeof ApiClient !== 'undefined' ? ApiClient : window.ApiClient;
  const q = (name) => view.querySelector('[data-mf="' + name + '"]');
  const state = { status: {}, sources: [], detail: null, source: '', tab: 'search' };
  let mineTimer = null;
  let mineLoading = false;
  let mineRendered = false;
  let mineVisible = true;
  let visibilityListenerAttached = false;
  let disposed = false;
  let searchGeneration = 0;
  let detailGeneration = 0;
  let discoverGeneration = 0;
  const isCatalog = (category) => ['ebook', 'audiobook', 'podcast'].includes(category);
  const categoryLabel = (category) => ((state.status.categories || []).find((item) => item.id === category) || {}).label || category;
  function availableSources() { return state.sources.filter((item) => (item.media_types || []).includes(q('category').value)); }
  function populateSources() {
    q('source').replaceChildren();
    const all = document.createElement('option'); all.value = 'all'; all.textContent = 'Alle passenden Quellen'; q('source').appendChild(all);
    availableSources().forEach((item) => {
      const option = document.createElement('option'); option.value = item.id; option.textContent = item.label; q('source').appendChild(option);
    });
  }

  function url(path, query) {
    let value = api.getUrl('AniWorldRequests/' + path);
    if (query) value += '?' + new URLSearchParams(query).toString();
    return value;
  }
  async function call(path, options) {
    const opts = options || {};
    const request = { url: url(path, opts.query), type: opts.method || 'GET', dataType: 'json' };
    if (opts.body !== undefined) {
      request.headers = { 'Content-Type': 'application/json' };
      request.data = JSON.stringify(opts.body);
    }
    try { return await api.fetch(request); }
    catch (error) { throw new Error(await readErrorMessage(error)); }
  }
  async function readErrorMessage(error) {
    const response = error && error.response ? error.response : error;
    const responseJson = error && error.responseJSON || response && response.responseJSON;
    if (responseJson && typeof responseJson.error === 'string' && responseJson.error.trim()) return responseJson.error;
    if (response && typeof response.clone === 'function') {
      try {
        const payload = await response.clone().json();
        if (payload && typeof payload.error === 'string' && payload.error.trim()) return payload.error;
      } catch (_) { /* the response was empty or not JSON */ }
    }
    return error && (error.message || error.statusText) || response && response.statusText || 'Anfrage fehlgeschlagen.';
  }
  function notice(message, error) {
    q('notice').innerHTML = '';
    if (!message) return;
    const box = document.createElement('div'); box.className = 'mf-notice' + (error ? ' mf-error' : ''); box.textContent = message; q('notice').appendChild(box);
  }
  function dialogNotice(message, error) {
    const host = q('dialog-notice'); host.innerHTML = '';
    if (!message) return;
    const box = document.createElement('div'); box.className = 'mf-notice' + (error ? ' mf-error' : ''); box.textContent = message; host.appendChild(box);
  }
  function switchTab(name) {
    state.tab = name;
    if (name !== 'mine') stopMinePolling();
    view.querySelectorAll('.mf-tab').forEach((b) => b.classList.toggle('active', b.dataset.tab === name));
    view.querySelectorAll('.mf-panel').forEach((p) => p.classList.toggle('active', p.dataset.panel === name));
    if (name === 'mine') loadMine({ initial: !mineRendered });
    if (name === 'admin') loadAdmin();
  }
  view.querySelectorAll('.mf-tab').forEach((b) => b.addEventListener('click', () => switchTab(b.dataset.tab)));

  async function boot() {
    try {
      state.status = await call('Status');
      if (disposed || !view.isConnected) return;
      q('mode').textContent = state.status.mode === 'automatic' ? 'Direkter Download' : 'Freigabe durch Admin';
      if (!state.status.configured) notice('Das Plugin ist noch nicht mit AniWorld verbunden.', true);
      else if (state.status.maintenance) notice(state.status.maintenanceMessage || 'Anfragen sind derzeit deaktiviert.', true);
      const data = await call('Sources');
      if (disposed || !view.isConnected) return;
      state.sources = Array.isArray(data.sources) ? data.sources : [];
      (state.status.categories || []).forEach((item) => {
        const option = document.createElement('option'); option.value = item.id; option.textContent = item.label; q('category').appendChild(option);
      });
      if (Array.from(q('category').options).some((item) => item.value === 'series')) q('category').value = 'series';
      populateSources();
      updatePlaceholder();
      await Promise.all([loadDiscover(), detectAdmin()]);
    } catch (error) { if (!disposed && view.isConnected) notice(error.message, true); }
  }
  async function loadDiscover() {
    if (disposed || !view.isConnected) return;
    const host = q('discover');
    const generation = ++discoverGeneration;
    const category = q('category').value;
    const source = q('source').value;
    const sources = availableSources().filter((item) => source === 'all' || item.id === source);
    host.replaceChildren();
    if (!sources.length) { host.innerHTML = '<div class="mf-empty">Keine freigegebene Quelle für diese Kategorie verfügbar.</div>'; return; }
    const rows = new Map();
    sources.forEach((item) => {
      const section = document.createElement('section'); section.className = 'mf-discoverrow';
      const heading = document.createElement('h3'); heading.className = 'mf-discoverhead'; heading.textContent = item.label;
      const grid = document.createElement('div'); grid.className = 'mf-discovergrid'; grid.textContent = 'Laden…';
      section.append(heading, grid); host.appendChild(section); rows.set(item.id, { section, grid });
    });
    let nextSource = 0;
    const maximum = Math.max(1, Math.min(4, Number(state.status.maxSearchSources) || 4));
    await Promise.all(Array.from({ length: Math.min(maximum, sources.length) }, async () => {
      while (nextSource < sources.length && !disposed && generation === discoverGeneration) {
        const item = sources[nextSource++];
        let definition;
        try {
          const data = await call('Discover', { query: { mediaType: category, source: item.id } });
          definition = (data.sections || []).find((section) => section.source === item.id) || { items: [] };
        } catch (error) { definition = { items: [], error: error.message }; }
        if (disposed || generation !== discoverGeneration || !view.isConnected) return;
        const row = rows.get(item.id); row.grid.replaceChildren();
        (definition.items || []).forEach((entry) => row.grid.appendChild(createMediaCard(entry, item.id, item.label)));
        if (definition.error) { const error = document.createElement('div'); error.className = 'mf-notice mf-error'; error.textContent = definition.error; row.section.appendChild(error); }
        if (!row.grid.children.length && !definition.error) row.section.remove();
      }
    }));
    if (!disposed && generation === discoverGeneration && view.isConnected && !host.children.length)
      host.innerHTML = '<div class="mf-empty">Zurzeit sind keine Empfehlungen verfügbar.</div>';
  }
  async function detectAdmin() {
    if (disposed || !view.isConnected) return;
    try {
      const items = await call('Admin/Requests');
      if (disposed || !view.isConnected) return;
      const tab = view.querySelector('[data-tab="admin"]'); tab.style.display = '';
      renderRequests(q('admin'), items, true);
    } catch (_) { /* normal users receive 403 */ }
  }

  function syncSearchMode() {
    const searching = q('query').value.trim().length > 0;
    q('discover').hidden = searching;
    q('results').hidden = !searching;
    if (!searching) q('results').innerHTML = '';
  }
  function updatePlaceholder() { q('query').placeholder = isCatalog(q('category').value) ? 'Titel oder Autor suchen' : 'Titel suchen, z. B. Gravity Falls'; }
  function resetSearch() { searchGeneration++; closeDetail(); q('results').replaceChildren(); q('query').value = ''; syncSearchMode(); notice(''); loadDiscover(); }
  q('category').addEventListener('change', () => { populateSources(); updatePlaceholder(); resetSearch(); });
  q('source').addEventListener('change', resetSearch);
  q('query').addEventListener('input', () => { searchGeneration++; closeDetail(); q('results').innerHTML = ''; syncSearchMode(); });
  q('search-form').addEventListener('submit', async (event) => {
    event.preventDefault();
    const generation = ++searchGeneration;
    const keyword = q('query').value.trim();
    const selectedSource = q('source').value;
    const maximum = Math.max(1, Math.min(32, Number(state.status.maxSearchSources) || 8));
    const sources = selectedSource === 'all'
      ? availableSources()
      : availableSources().filter((item) => item.id === selectedSource);
    syncSearchMode(); notice('');
    const host = q('results'); host.innerHTML = '';
    const pending = document.createElement('div'); pending.className = 'mf-empty'; pending.dataset.mfSearchPending = '1'; pending.textContent = 'Weitere Quellen werden durchsucht…'; host.appendChild(pending);
    if (!sources.length) { pending.remove(); notice('Keine freigegebene Quelle verfügbar.', true); return; }
    let resultCount = 0; let errorCount = 0;
    let nextSource = 0;
    await Promise.all(Array.from({ length: Math.min(maximum, sources.length) }, async () => {
      while (nextSource < sources.length && generation === searchGeneration && !disposed) {
      const item = sources[nextSource++];
      let groups;
      try {
        const data = await call('Search', { query: { query: keyword, source: item.id, mediaType: q('category').value } });
        groups = data.groups || [];
      } catch (error) {
        groups = [{ source: item.id, label: item.label, error: error.message }];
      }
      if (disposed || generation !== searchGeneration || !view.isConnected) return;
      const rendered = appendResults(host, groups, pending);
      resultCount += rendered.count; errorCount += rendered.errors;
      }
    }));
    if (disposed || generation !== searchGeneration || !view.isConnected) return;
    pending.remove();
    if (!resultCount && !errorCount) host.innerHTML = '<div class="mf-empty">Keine Treffer gefunden.</div>';
  });

  function appendResults(host, groups, before) {
    let count = 0; let errors = 0;
    groups.forEach((group) => {
      const results = group.data && Array.isArray(group.data.results) ? group.data.results : [];
      results.forEach((item) => {
        const card = createMediaCard(item, group.source, group.label);
        card.dataset.relevance = String(Number(item.relevance) || 0);
        card.dataset.sortTitle = item.title || item.name || '';
        card.dataset.sortSource = group.source;
        host.insertBefore(card, before); count++;
      });
      if (group.error) { errors++; const box = document.createElement('div'); box.className = 'mf-notice mf-error'; box.textContent = group.label + ': ' + group.error; host.insertBefore(box, before); }
    });
    Array.from(host.querySelectorAll('.mf-card')).sort((a, b) => Number(b.dataset.relevance) - Number(a.dataset.relevance)
      || a.dataset.sortTitle.localeCompare(b.dataset.sortTitle, 'de') || a.dataset.sortSource.localeCompare(b.dataset.sortSource))
      .forEach((card) => host.insertBefore(card, before));
    return { count, errors };
  }

  function createMediaCard(item, sourceId, sourceLabel) {
    const card = document.createElement('article'); card.className = 'mf-card'; card.tabIndex = 0;
    const rawUrl = item.url || item.link || item.series_url;
    const poster = item.poster_url || item.cover_url;
    if (poster) {
      const cover = document.createElement('img'); cover.className = 'mf-cover'; cover.loading = 'lazy'; cover.alt = '';
      card.appendChild(cover); loadCover(cover, poster, item, sourceId);
    } else if (rawUrl) {
      const cover = document.createElement('img'); cover.className = 'mf-cover'; cover.loading = 'lazy'; cover.alt = '';
      card.appendChild(cover);
      call('Series', { query: { url: rawUrl } })
        .then((detail) => {
          if (disposed || !cover.isConnected) return;
          if (detail.poster_url) loadCover(cover, detail.poster_url); else cover.remove();
        })
        .catch(() => { if (!disposed && cover.isConnected) cover.remove(); });
    }
    const body = document.createElement('div'); body.className = 'mf-cardbody';
    const title = document.createElement('div'); title.className = 'mf-cardtitle'; title.textContent = item.title || item.name || 'Unbekannter Titel';
    const source = document.createElement('div'); source.className = 'mf-source'; source.textContent = [sourceLabel || sourceId, item.author, item.year].filter(Boolean).join(' · ');
    body.append(title, source); card.appendChild(body);
    const open = () => openDetail(item, sourceId); card.addEventListener('click', open); card.addEventListener('keydown', (event) => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); open(); } });
    return card;
  }

  async function loadCover(image, posterUrl, item, sourceId, retried) {
    let objectUrl = '';
    try {
      const catalog = item && isCatalog(item.media_kind);
      const token = catalog ? new URL(posterUrl, window.location.origin).searchParams.get('token') : '';
      const imageUrl = catalog ? url('Catalog/Cover', { token: token || '', source: sourceId, mediaType: item.media_kind }) : url('Image', { url: posterUrl });
      const response = await api.fetch({ url: imageUrl, type: 'GET' });
      if (!response || typeof response.blob !== 'function') throw new Error('Ungültige Bildantwort');
      const blob = await response.blob();
      if (!blob.type.startsWith('image/')) throw new Error('Ungültiger Bildtyp');
      if (disposed || !image.isConnected) return;
      objectUrl = URL.createObjectURL(blob); image.src = objectUrl;
      image.addEventListener('load', () => URL.revokeObjectURL(objectUrl), { once: true });
      image.addEventListener('error', () => { URL.revokeObjectURL(objectUrl); image.remove(); }, { once: true });
    } catch (_) {
      if (objectUrl) URL.revokeObjectURL(objectUrl);
      if (!retried && item && isCatalog(item.media_kind) && !disposed && image.isConnected) {
        try {
          const detail = await call('Catalog/Details', { query: { source: sourceId, id: item.id, mediaType: item.media_kind } });
          if (detail.cover_url) return loadCover(image, detail.cover_url, item, sourceId, true);
        } catch (_) { /* cover is optional */ }
      }
      image.remove();
    }
  }

  async function openDetail(item, source) {
    if (isCatalog(item.media_kind || q('category').value)) return openCatalogDetail(item, source);
    const rawUrl = item.url || item.link || item.series_url;
    if (!rawUrl) return notice('Der Treffer enthält keine AniWorld-URL.', true);
    const generation = ++detailGeneration;
    q('catalog-options').hidden = true; view.querySelector('.mf-options').hidden = false;
    state.source = source; state.detail = null; q('overlay').style.display = 'flex'; q('detail-title').textContent = item.title || item.name || 'Laden…'; q('description').textContent = 'Vorhandene Staffeln und Episoden werden geprüft…'; q('plan').innerHTML = '<div class="mf-empty">Bestand in Jellyfin wird geprüft…</div>'; dialogNotice(''); q('request').textContent = 'Anfragen'; q('request').disabled = true;
    setOptions(q('language'), [state.status.defaultLanguage || 'German Dub'], state.status.defaultLanguage);
    setOptions(q('provider'), [state.status.defaultProvider || 'VOE'], state.status.defaultProvider);
    try {
      let type = item.media_type || q('category').value;
      if (!item.media_type) {
        const sourceObj = state.sources.find((s) => s.id === source);
        if (sourceObj && Array.isArray(sourceObj.media_types) && sourceObj.media_types.length === 1 && sourceObj.media_types[0] === 'movie') {
          type = 'movie';
        }
      }
      const payload = { title: item.title || item.name || 'Unbekannter Titel', seriesUrl: rawUrl, source, mediaType: type };
      const plan = await call('Requests/Plan', { method: 'POST', body: payload });
      if (disposed || generation !== detailGeneration || !view.isConnected) return;
      state.detail = Object.assign(payload, { title: plan.title || payload.title, plan });
      q('detail-title').textContent = state.detail.title;
      q('description').textContent = plan.description || 'Keine Beschreibung verfügbar.';
      const advertisedProviderLanguages = plan.providers && typeof plan.providers === 'object'
        ? Object.keys(plan.providers).filter((language) => Array.isArray(plan.providers[language]) && plan.providers[language].length)
        : [];
      const plannedLanguages = Array.isArray(plan.languages) ? plan.languages : [];
      const providerLanguages = plannedLanguages.length
        ? advertisedProviderLanguages.filter((language) => plannedLanguages.some((candidate) => String(candidate).toLowerCase() === language.toLowerCase()))
        : advertisedProviderLanguages;
      const languages = providerLanguages.slice();
      setOptions(q('language'), languages, state.status.defaultLanguage);
      const syncProviders = () => {
        const available = plan.providers && Array.isArray(plan.providers[q('language').value]) ? plan.providers[q('language').value] : [];
        setOptions(q('provider'), available, state.status.defaultProvider);
        q('request').disabled = !plan.missing_count || available.length === 0;
      };
      q('language').onchange = syncProviders; syncProviders();
      q('plan').innerHTML = '';
      const summary = document.createElement('div'); summary.className = 'mf-plan ' + (plan.missing_count ? '' : 'complete');
      if (plan.missing_count) {
        const missingText = plan.is_movie
          ? 'Der Film fehlt und kann angefragt werden.'
          : plan.missing_count + ' von ' + plan.total_count + ' Episoden fehlen. Es werden ausschließlich diese fehlenden Episoden angefragt.';
        summary.textContent = providerLanguages.length
          ? missingText
          : missingText + ' AniWorld meldet dafür derzeit keinen verfügbaren Download-Provider.';
        q('request').disabled = providerLanguages.length === 0;
      } else {
        summary.textContent = plan.is_movie
          ? 'Der Film ist bereits vorhanden und wird nicht erneut eingereiht.'
          : 'Alle ' + plan.total_count + ' Episoden sind bereits vorhanden. Es wird nichts eingereiht.';
      }
      q('plan').appendChild(summary);
    } catch (error) { if (!disposed && generation === detailGeneration && view.isConnected) { q('description').textContent = error.message; q('plan').innerHTML = ''; dialogNotice(error.message, true); } }
  }
  function setOptions(select, values, preferred) { const clean = Array.from(new Set(values.filter(Boolean))); select.innerHTML = ''; clean.forEach((value) => { const option = document.createElement('option'); option.value = value; option.textContent = value; select.appendChild(option); }); if (clean.includes(preferred)) select.value = preferred; }
  q('request').addEventListener('click', async () => {
    if (state.detail && state.detail.backend === 'catalog') return submitCatalog();
    if (!state.detail || !state.detail.plan || !state.detail.plan.missing_count || !q('language').value || !q('provider').value) {
      dialogNotice('Die Anfrage ist noch nicht vollständig vorbereitet. Bitte den Titel schließen und erneut öffnen.', true);
      return;
    }
    const detail = state.detail;
    const generation = detailGeneration;
    dialogNotice('Anfrage wird übermittelt…');
    q('request').textContent = 'Wird übermittelt…';
    q('request').disabled = true;
    try {
      // upscale entfernt
      const payload = { planToken: detail.plan.plan_token || '', title: detail.title, seriesUrl: detail.seriesUrl, source: detail.source, mediaType: detail.mediaType, language: q('language').value, provider: q('provider').value };
      const result = await call('Requests/Automatic', { method: 'POST', body: payload });
      if (disposed || generation !== detailGeneration || !view.isConnected) return;
      const message = result.status === 'queued' ? 'Nur die fehlenden Inhalte wurden direkt an AniWorld übergeben.' : 'Die Anfrage für die fehlenden Inhalte wurde an den Administrator gesendet.';
      closeDetail(); notice(message); switchTab('mine');
    } catch (error) { if (!disposed && generation === detailGeneration && view.isConnected) dialogNotice(error.message, true); } finally { if (!disposed && generation === detailGeneration && view.isConnected) { q('request').textContent = 'Anfragen'; q('request').disabled = !q('language').value || !q('provider').value; } }
  });
  async function openCatalogDetail(item, source) {
    const generation = ++detailGeneration;
    state.detail = null;
    q('overlay').style.display = 'flex'; q('detail-title').textContent = item.title || 'Laden…'; q('description').textContent = 'Katalogdetails werden geladen…';
    q('plan').replaceChildren(); dialogNotice(''); q('request').disabled = true; q('request').textContent = 'Anfragen';
    view.querySelector('.mf-options').hidden = true; q('catalog-options').hidden = false; q('format').replaceChildren(); q('catalog-target').textContent = '';
    try {
      const detail = await call('Catalog/Details', { query: { source, id: item.id, mediaType: item.media_kind || q('category').value } });
      if (disposed || generation !== detailGeneration || !view.isConnected) return;
      state.detail = Object.assign({}, detail, { backend: 'catalog' });
      q('detail-title').textContent = detail.title;
      q('description').textContent = [detail.author, detail.year, detail.language, detail.description].filter(Boolean).join(' · ');
      (detail.assets || []).forEach((asset) => { const option = document.createElement('option'); option.value = asset.id; option.textContent = (asset.label || asset.extension.toUpperCase()) + (asset.downloaded ? ' – bereits heruntergeladen' : ''); q('format').appendChild(option); });
      q('format').onchange = () => {
        if (!state.detail || state.detail.backend !== 'catalog') return;
        const asset = state.detail.assets.find((asset) => asset.id === q('format').value);
        q('request').disabled = !asset || asset.downloaded;
        q('catalog-target').textContent = asset ? (asset.downloaded ? 'Diese Datei ist bereits im Downloader vorhanden.' : asset.target_path || '') : 'Keine herunterladbare Datei verfügbar.';
      };
      q('format').onchange();
    } catch (error) { if (!disposed && generation === detailGeneration && view.isConnected) dialogNotice(error.message, true); }
  }
  async function submitCatalog() {
    const detail = state.detail;
    const generation = detailGeneration;
    const asset = (detail.assets || []).find((asset) => asset.id === q('format').value);
    if (!asset || asset.downloaded) return;
    q('request').disabled = true; dialogNotice('Anfrage wird übermittelt…');
    try {
      const result = await call('Requests/Catalog', { method: 'POST', body: { source: detail.source, itemId: detail.id, assetId: asset.id, mediaType: detail.media_kind } });
      if (disposed || generation !== detailGeneration || !view.isConnected) return;
      closeDetail(); notice(result.status === 'completed' ? 'Die ausgewählte Datei ist bereits heruntergeladen.' : result.status === 'queued' ? 'Die ausgewählte Datei wurde an den Downloader übergeben.' : 'Die Anfrage wurde an den Administrator gesendet.'); switchTab('mine');
    } catch (error) { if (!disposed && generation === detailGeneration && view.isConnected) dialogNotice(error.message, true); }
    finally { if (!disposed && generation === detailGeneration && view.isConnected) q('request').disabled = false; }
  }
  function closeDetail() { detailGeneration++; state.detail = null; dialogNotice(''); q('overlay').style.display = 'none'; }
  q('close').addEventListener('click', closeDetail); q('cancel').addEventListener('click', closeDetail); q('overlay').addEventListener('click', (e) => { if (e.target === q('overlay')) closeDetail(); });

  function stopMinePolling() {
    if (mineTimer) clearTimeout(mineTimer);
    mineTimer = null;
  }
  function scheduleMineRefresh(hasActiveDownload) {
    stopMinePolling();
    if (disposed || !hasActiveDownload || !view.isConnected || !mineVisible || state.tab !== 'mine' || document.visibilityState === 'hidden') return;
    mineTimer = setTimeout(() => loadMine({ initial: false }), 5000);
  }
  function onVisibilityChange() {
    if (disposed) return;
    if (document.visibilityState === 'hidden') stopMinePolling();
    else if (state.tab === 'mine' && mineVisible) loadMine({ initial: !mineRendered });
  }
  function setVisibilityListener(enabled) {
    if (disposed) enabled = false;
    if (enabled && !visibilityListenerAttached) {
      document.addEventListener('visibilitychange', onVisibilityChange);
      visibilityListenerAttached = true;
    } else if (!enabled && visibilityListenerAttached) {
      document.removeEventListener('visibilitychange', onVisibilityChange);
      visibilityListenerAttached = false;
    }
  }
  async function loadMine(options) {
    if (disposed || mineLoading || !view.isConnected || !mineVisible) return;
    mineLoading = true;
    const initial = Boolean(options && options.initial) || !mineRendered;
    if (initial) q('mine').innerHTML = '<div class="mf-empty">Laden…</div>';
    try {
      const items = await call('Requests/Mine');
      let progress = [];
      if (items.some((item) => item.status === 'queued' && item.aniWorldQueueId)) {
        try { progress = (await call('Requests/Progress')).items || []; } catch (_) { /* request list remains available */ }
      }
      if (disposed || !view.isConnected || !mineVisible) return;
      const byQueue = new Map(progress.map((item) => [item.queue_id, item]));
      renderRequests(q('mine'), items, false, byQueue);
      mineRendered = true;
      const hasActiveDownload = items.some((item) => item.status === 'queued')
        || progress.some((item) => item.status === 'queued' || item.status === 'running');
      scheduleMineRefresh(hasActiveDownload);
    } catch (error) {
      if (disposed || !view.isConnected) return;
      if (!mineRendered) q('mine').textContent = error.message;
      else notice('Meine Anfragen konnten nicht aktualisiert werden: ' + error.message, true);
      scheduleMineRefresh(true);
    } finally { mineLoading = false; }
  }
  async function loadAdmin() {
    if (disposed || !view.isConnected) return;
    q('admin').innerHTML = '<div class="mf-empty">Laden…</div>';
    try {
      const items = await call('Admin/Requests');
      if (!disposed && view.isConnected) renderRequests(q('admin'), items, true);
    } catch (error) {
      if (!disposed && view.isConnected) q('admin').textContent = error.message;
    }
  }
  function renderRequests(host, items, admin, progressByQueue) {
    const values = Array.isArray(items) ? items : [];
    if (!values.length) {
      if (host.children.length !== 1 || !host.firstElementChild.classList.contains('mf-empty')) host.innerHTML = '<div class="mf-empty">Keine Anfragen vorhanden.</div>';
      return;
    }

    const firstVisible = Array.from(host.querySelectorAll('.mf-request')).find((card) => card.getBoundingClientRect().bottom >= 0);
    const anchorId = firstVisible && firstVisible.dataset.requestId;
    const anchorTop = firstVisible ? firstVisible.getBoundingClientRect().top : null;
    const existing = new Map(Array.from(host.querySelectorAll('.mf-request')).map((card) => [card.dataset.requestId, card]));
    host.querySelectorAll('.mf-empty').forEach((node) => node.remove());
    values.forEach((item, index) => {
      const key = String(item.id);
      const card = existing.get(key) || createRequestCard(key);
      existing.delete(key);
      updateRequestCard(card, item, admin, progressByQueue && progressByQueue.get(item.aniWorldQueueId));
      const atIndex = host.children[index];
      if (atIndex !== card) host.insertBefore(card, atIndex || null);
    });
    existing.forEach((card) => card.remove());

    if (anchorId && anchorTop !== null && typeof window.scrollBy === 'function') {
      const anchor = Array.from(host.querySelectorAll('.mf-request')).find((card) => card.dataset.requestId === anchorId);
      if (anchor) {
        const delta = anchor.getBoundingClientRect().top - anchorTop;
        if (Math.abs(delta) > 0.5) window.scrollBy(0, delta);
      }
    }
  }
  function createRequestCard(key) {
    const card = document.createElement('article'); card.className = 'mf-request'; card.dataset.requestId = key;
    const top = document.createElement('div'); top.className = 'mf-requesttop';
    const left = document.createElement('div');
    const title = document.createElement('div'); title.className = 'mf-requesttitle'; title.dataset.role = 'title';
    const meta = document.createElement('div'); meta.className = 'mf-meta'; meta.dataset.role = 'meta';
    const pill = document.createElement('span'); pill.dataset.role = 'pill';
    left.append(title, meta); top.append(left, pill);
    const progressBox = document.createElement('div'); progressBox.className = 'mf-requestprogress'; progressBox.dataset.role = 'progress';
    const track = document.createElement('div'); track.className = 'mf-requestprogresstrack';
    const fill = document.createElement('div'); fill.className = 'mf-requestprogressfill'; fill.dataset.role = 'progress-fill'; track.appendChild(fill);
    const detail = document.createElement('div'); detail.className = 'mf-meta'; detail.dataset.role = 'progress-detail'; progressBox.append(track, detail);
    const error = document.createElement('div'); error.className = 'mf-notice mf-error'; error.dataset.role = 'error';
    const actions = document.createElement('div'); actions.className = 'mf-actions'; actions.dataset.role = 'actions';
    card.append(top, progressBox, error, actions);
    return card;
  }
  function updateRequestCard(card, item, admin, progress) {
    card.querySelector('[data-role="title"]').textContent = item.title;
    card.querySelector('[data-role="meta"]').textContent = (admin ? item.username + ' · ' : '') + categoryLabel(item.mediaType) + ' · ' + (item.selectionLabel || ((item.episodes || []).length + ' Episoden')) + ' · ' + item.language + ' · ' + new Date(item.createdUtc).toLocaleString();
    const pill = card.querySelector('[data-role="pill"]'); pill.className = 'mf-pill ' + item.status; pill.textContent = progressLabel(progress) || statusLabel(item.status);
    const progressBox = card.querySelector('[data-role="progress"]'); progressBox.hidden = !progress;
    if (progress) {
      card.querySelector('[data-role="progress-fill"]').style.width = Math.max(0, Math.min(100, Number(progress.percent) || 0)) + '%';
      card.querySelector('[data-role="progress-detail"]').textContent = progressDetail(progress);
    }
    const error = card.querySelector('[data-role="error"]'); error.hidden = !item.error; error.textContent = item.error || '';
    const actions = card.querySelector('[data-role="actions"]'); actions.innerHTML = '';
    if (admin && (item.status === 'pending' || item.status === 'failed')) {
      const approve = document.createElement('button'); approve.className = 'mf-btn'; approve.textContent = item.status === 'failed' ? 'Erneut versuchen' : 'Freigeben'; approve.onclick = () => decide(item.id, 'Approve');
      const reject = document.createElement('button'); reject.className = 'mf-btn danger'; reject.textContent = 'Ablehnen'; reject.onclick = () => decide(item.id, 'Reject'); actions.append(approve, reject);
    } else if (!admin && item.status === 'pending') {
      const withdraw = document.createElement('button'); withdraw.className = 'mf-btn danger'; withdraw.textContent = 'Anfrage zurückziehen'; withdraw.onclick = () => withdrawRequest(item.id); actions.appendChild(withdraw);
    }
    actions.hidden = !actions.children.length;
  }
  async function decide(id, action) { try { await call('Admin/Requests/' + id + '/' + action, { method: 'POST', body: action === 'Reject' ? { reason: 'Vom Administrator abgelehnt.' } : {} }); await loadAdmin(); } catch (error) { if (!disposed && view.isConnected) notice(error.message, true); } }
  async function withdrawRequest(id) { if (!window.confirm('Diese noch nicht freigegebene Anfrage zurückziehen?')) return; try { await call('Requests/' + id, { method: 'DELETE' }); await loadMine(); } catch (error) { if (!disposed && view.isConnected) notice(error.message, true); } }
  function progressLabel(progress) { 
    if (!progress) return ''; 
    if (progress.status === 'running') {
      return progress.total_episodes > 1 
        ? progress.current_episode + ' von ' + progress.total_episodes + ' gedownloaded'
        : 'Wird gedownloaded';
    }
    return ({ queued: 'Wartet auf Download', completed: 'Fertig', partial: 'Teilweise fertig', failed: 'Fehlgeschlagen', cancelled: 'Abgebrochen' })[progress.status] || ''; 
  }
  function progressDetail(progress) { const phase = ({ download: 'Download', ffmpeg: 'Verarbeitung' })[progress.phase] || 'Download'; const episodes = progress.total_episodes > 1 ? ' · ' + progress.current_episode + '/' + progress.total_episodes + ' Episoden' : ''; return phase + ': ' + Math.round(Number(progress.percent) || 0) + '%' + episodes; }
  function statusLabel(status) { return ({ pending: 'Ausstehend', processing: 'Wird übergeben', queued: 'Wartet auf Download', completed: 'Fertig', available: 'Bereits in Jellyfin vorhanden', partial: 'Teilweise fertig', cancelled: 'Abgebrochen', rejected: 'Abgelehnt', withdrawn: 'Zurückgezogen', failed: 'Fehlgeschlagen' })[status] || status; }
  q('refresh-mine').addEventListener('click', () => loadMine({ initial: !mineRendered })); q('refresh-admin').addEventListener('click', loadAdmin);
  setVisibilityListener(true);
  view.addEventListener('viewshow', () => { if (disposed) return; mineVisible = true; setVisibilityListener(true); if (state.tab === 'mine') loadMine({ initial: !mineRendered }); });
  view.addEventListener('viewhide', () => { mineVisible = false; stopMinePolling(); setVisibilityListener(false); detailGeneration++; });
  boot();
  return function dispose() {
    if (disposed) return;
    disposed = true;
    mineVisible = false;
    stopMinePolling();
    setVisibilityListener(false);
    searchGeneration++;
    detailGeneration++;
    discoverGeneration++;
  };
}
