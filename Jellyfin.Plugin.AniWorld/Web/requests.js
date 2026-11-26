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
  const categoryLabel = (category) => ({ movie: 'Film', series: 'Serie / Anime', manga: 'Manga', ebook: 'E-Book', audiobook: 'Hörbuch', podcast: 'Podcast' })[category] || category;
  function availableSources() {
    const category = (state.status.categories || []).find((item) => item.id === q('category').value);
    const types = category ? category.media_types || [] : [];
    return state.sources.filter((item) => (item.media_types || []).some((kind) => types.includes(kind)));
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
  let toastTimer = null;
  function notice(message, error) {
    q('notice').innerHTML = '';
    if (!message) return;
    // Confirmations appear as a non-blocking toast (H0melab style) so they stay visible
    // wherever the user scrolled to; errors remain inline until the next action.
    if (!error) return toast(message);
    const box = document.createElement('div'); box.className = 'mf-notice mf-error'; box.textContent = message; q('notice').appendChild(box);
  }
  function toast(message) {
    const host = q('toast');
    if (!host) return;
    if (toastTimer) clearTimeout(toastTimer);
    host.textContent = message; host.classList.add('show');
    toastTimer = setTimeout(() => { toastTimer = null; host.classList.remove('show'); }, 5000);
  }
  function emptyLine(host, message) {
    host.replaceChildren();
    const box = document.createElement('div'); box.className = 'mf-emptyline'; box.textContent = message; host.appendChild(box);
  }
  function dialogNotice(message, error) {
    const host = q('dialog-notice'); host.innerHTML = '';
    if (!message) return;
    const box = document.createElement('div'); box.className = 'mf-notice' + (error ? ' mf-error' : ''); box.textContent = message; host.appendChild(box);
    // On small screens the sticky action bar can cover the message; bring it into view.
    if (typeof box.scrollIntoView === 'function') box.scrollIntoView({ block: 'nearest' });
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

  // Maintenance must be impossible to overlook: a large banner on the page and in the
  // dialog, and every request/handover button stays disabled. The server enforces it too.
  function maintenanceMessage() { return state.status.maintenanceMessage || 'Anfragen sind derzeit deaktiviert.'; }
  function guardRequestButton() {
    if (!state.status.maintenance) return;
    q('request').disabled = true; q('request').textContent = 'Anfragen deaktiviert';
  }
  function applyMaintenance() {
    const on = Boolean(state.status.maintenance);
    q('maintenance').hidden = !on; q('maintenance-text').textContent = on ? maintenanceMessage() : '';
    q('dialog-maintenance').hidden = !on; q('dialog-maintenance-text').textContent = on ? maintenanceMessage() : '';
    if (on) guardRequestButton();
    else if (q('request').textContent === 'Anfragen deaktiviert') q('request').textContent = 'Anfragen';
    view.querySelectorAll('[data-mf-handover]').forEach((button) => { button.disabled = on; button.title = on ? 'Im Wartungsmodus deaktiviert' : ''; });
  }
  async function refreshStatus() {
    try {
      const status = await call('Status');
      if (disposed || !view.isConnected) return;
      state.status = Object.assign({}, state.status, status); applyMaintenance();
    } catch (_) { /* the last known state stays visible */ }
  }
  async function boot() {
    try {
      state.status = await call('Status');
      if (disposed || !view.isConnected) return;
      q('mode').textContent = state.status.mode === 'automatic' ? 'Direkter Download' : 'Freigabe durch Admin';
      if (!state.status.configured) notice('Das Plugin ist noch nicht mit AniWorld verbunden.', true);
      applyMaintenance();
      const data = await call('Sources');
      if (disposed || !view.isConnected) return;
      state.sources = Array.isArray(data.sources) ? data.sources : [];
      (state.status.categories || []).forEach((item) => {
        const option = document.createElement('option'); option.value = item.id; option.textContent = item.label; q('category').appendChild(option);
      });
      if (Array.from(q('category').options).some((item) => item.value === 'video')) q('category').value = 'video';
      updatePlaceholder();
      await Promise.all([loadDiscover(), detectAdmin()]);
    } catch (error) { if (!disposed && view.isConnected) notice(error.message, true); }
  }
  async function loadDiscover() {
    if (disposed || !view.isConnected) return;
    const host = q('discover');
    const generation = ++discoverGeneration;
    const category = q('category').value;
    const sources = availableSources();
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
          definition = (data.sections || []).find((section) => section.source === item.id) || { items: [], error: 'Die Quelle hat keine gültige Empfehlungsantwort geliefert.' };
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
  function updatePlaceholder() { q('query').placeholder = q('category').value === 'catalog' ? 'Titel oder Autor suchen' : 'Serien, Filme, Anime oder Manga suchen'; }
  function resetSearch() { searchGeneration++; closeDetail(); q('results').replaceChildren(); q('query').value = ''; syncSearchMode(); notice(''); loadDiscover(); }
  q('category').addEventListener('change', () => { updatePlaceholder(); resetSearch(); });
  q('query').addEventListener('input', () => { searchGeneration++; closeDetail(); q('results').innerHTML = ''; syncSearchMode(); });
  q('search-form').addEventListener('submit', async (event) => {
    event.preventDefault();
    const generation = ++searchGeneration;
    const keyword = q('query').value.trim();
    const category = q('category').value;
    const maximum = Math.max(1, Math.min(32, Number(state.status.maxSearchSources) || 8));
    const sources = availableSources();
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
        const data = await call('Search', { query: { query: keyword, source: item.id, mediaType: category } });
        if (!Array.isArray(data.groups) || !data.groups.length) throw new Error('Die Quelle hat keine gültige Suchantwort geliefert.');
        groups = data.groups;
      } catch (error) {
        groups = [{ source: item.id, label: item.label, error: error.message }];
      }
      if (disposed || generation !== searchGeneration || !view.isConnected) return;
      const rendered = await appendResults(host, groups, pending, generation);
      resultCount += rendered.count; errorCount += rendered.errors;
      }
    }));
    if (disposed || generation !== searchGeneration || !view.isConnected) return;
    pending.remove();
    if (!resultCount && !errorCount) host.innerHTML = '<div class="mf-empty">Keine Treffer gefunden.</div>';
  });

  async function appendResults(host, groups, before, generation) {
    let count = 0; let errors = 0;
    for (const group of groups) {
      const results = group.data && Array.isArray(group.data.results) ? group.data.results : [];
      for (const item of results) {
        if (disposed || generation !== searchGeneration || !view.isConnected) return { count, errors };
        const card = createMediaCard(item, group.source, group.label);
        host.insertBefore(card, before); count++;
        // Keep the downloader's order and yield so large result sets remain interactive.
        if (count % 24 === 0) await new Promise((resolve) => setTimeout(resolve, 0));
      }
      if (disposed || generation !== searchGeneration || !view.isConnected) return { count, errors };
      if (group.error) { errors++; const box = document.createElement('div'); box.className = 'mf-notice mf-error'; box.textContent = group.label + ': ' + group.error; host.insertBefore(box, before); }
    }
    return { count, errors };
  }

  function createMediaCard(item, sourceId, sourceLabel) {
    const card = document.createElement('article'); card.className = 'mf-card'; card.tabIndex = 0;
    const rawUrl = item.url || item.link || item.series_url;
    const poster = item.poster_url || item.cover_url;
    if (poster) {
      const cover = document.createElement('img'); cover.className = 'mf-cover'; cover.loading = 'lazy'; cover.alt = '';
      card.appendChild(cover); loadCover(cover, poster, item, sourceId);
    }
    // A missing poster must not start a detail request for every search result.
    // Full details are loaded when the user opens the selected title.
    const body = document.createElement('div'); body.className = 'mf-cardbody';
    const title = document.createElement('div'); title.className = 'mf-cardtitle'; title.textContent = item.title || item.name || 'Unbekannter Titel';
    const source = document.createElement('div'); source.className = 'mf-source'; source.textContent = [sourceLabel || sourceId, item.grouped_series ? 'Serie · Staffeln/Folgen im Detail' : '', item.author, item.year].filter(Boolean).join(' · ');
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

  async function openDetail(item, source, subtitleLanguage = 'none', preferredLanguage = state.status.defaultLanguage) {
    if (isCatalog(item.media_kind)) return openCatalogDetail(item, source);
    const rawUrl = item.url || item.link || item.series_url;
    if (!rawUrl) return notice('Der Treffer enthält keine AniWorld-URL.', true);
    const generation = ++detailGeneration;
    q('catalog-options').hidden = true; view.querySelector('.mf-options').hidden = false;
    // Soft subtitles exist where the downloader offers them: SerienStream and ZDF.
    const subtitles = source === 'sto' || source === 'zdf';
    q('subtitle-field').hidden = !subtitles;
    q('subtitle').value = subtitles ? subtitleLanguage : 'none';
    q('subtitle').onchange = () => openDetail(item, source, q('subtitle').value, q('language').value || preferredLanguage);
    state.source = source; state.detail = null; q('overlay').style.display = 'flex'; focusDialog('close'); refreshStatus(); q('detail-title').textContent = item.title || item.name || 'Laden…'; q('description').textContent = 'Vorhandene Staffeln und Episoden werden geprüft…'; q('plan').innerHTML = '<div class="mf-empty">Bestand in Jellyfin wird geprüft…</div>'; dialogNotice(''); q('request').textContent = 'Anfragen'; q('request').disabled = true;
    setOptions(q('language'), [], ''); setOptions(q('provider'), [], '');
    q('partial-container').hidden = true; q('partial').checked = false;
    try {
      const sourceObj = state.sources.find((s) => s.id === source);
      const knownTypes = sourceObj && sourceObj.media_types || [];
      // Group IDs are never request media types. Mixed sources are resolved by
      // the existing detail/season plan, not by probing every search result.
      const type = source === 'mangafire' ? 'manga' : ['movie', 'series', 'manga'].includes(item.media_type)
        ? item.media_type : knownTypes.length === 1 ? knownTypes[0] : 'series';
      const payload = { title: item.title || item.name || 'Unbekannter Titel', seriesUrl: rawUrl, source, mediaType: type, subtitleLanguage: q('subtitle').value };
      const plan = await call('Requests/Plan', { method: 'POST', body: payload });
      if (disposed || generation !== detailGeneration || !view.isConnected) return;
      state.detail = Object.assign(payload, { mediaType: source === 'mangafire' ? 'manga' : plan.is_movie ? 'movie' : 'series', title: plan.title || payload.title, plan });
      q('detail-title').textContent = state.detail.title;
      q('description').textContent = plan.description || 'Keine Beschreibung verfügbar.';
      const advertisedProviderLanguages = plan.providers && typeof plan.providers === 'object'
        ? Object.keys(plan.providers).filter((language) => Array.isArray(plan.providers[language]) && plan.providers[language].length)
        : [];
      const providerLanguages = Object.keys(plan.coverage || {}).length ? Object.keys(plan.coverage) : advertisedProviderLanguages;
      const languages = providerLanguages.slice();
      // Never silently preselect another audio language when the configured choice is unavailable.
      if (preferredLanguage && !languages.includes(preferredLanguage)) languages.unshift(preferredLanguage);
      setOptions(q('language'), languages, preferredLanguage);
      const syncProviders = () => {
        const available = plan.providers && Array.isArray(plan.providers[q('language').value]) ? plan.providers[q('language').value] : [];
        setOptions(q('provider'), available, state.status.defaultProvider);
        const coverage = plan.coverage && plan.coverage[q('language').value];
        const missing = coverage ? coverage.missing_count : plan.missing_count;
        const unavailable = coverage ? coverage.unavailable_count : 0;
        q('partial-container').hidden = true;
        q('request').disabled = !missing || available.length === 0; guardRequestButton();
        q('plan').replaceChildren();
        const summary = document.createElement('div'); summary.className = 'mf-plan';
        const headline = document.createElement('div');
        headline.textContent = coverage ? q('language').value + ': Bestand in dieser Sprache'
          : (plan.missing_count ? plan.missing_count + ' fehlende Inhalte.' : 'Bereits vorhanden.');
        summary.appendChild(headline);
        if (!providerLanguages.length) headline.textContent = 'Keine Sprache mit verfügbaren Providern bestätigt. Bitte später erneut prüfen.';
        else if (!coverage && !available.length) headline.textContent = 'Die gewählte Sprache ist derzeit nicht bestätigt. Bitte später erneut prüfen oder ausdrücklich eine andere Sprache auswählen.';
        else if (coverage) {
          const stats = document.createElement('div'); stats.className = 'mf-stats';
          [[coverage.available_count + '/' + plan.total_count, 'an der Quelle bestätigt', ''],
            [coverage.existing_count, 'bereits vorhanden', coverage.existing_count ? 'good' : ''],
            [missing, 'werden angefragt', missing ? 'good' : ''],
            [unavailable, 'nicht bestätigt', unavailable ? 'warn' : '']].forEach(([value, label, tone]) => {
            const stat = document.createElement('div'); stat.className = 'mf-stat' + (tone ? ' ' + tone : '');
            const number = document.createElement('span'); number.className = 'mf-statvalue'; number.textContent = String(value);
            const caption = document.createElement('span'); caption.className = 'mf-statlabel'; caption.textContent = label;
            stat.append(number, caption); stats.appendChild(stat);
          });
          summary.appendChild(stats);
          if (unavailable && missing) {
            const note = document.createElement('div'); note.className = 'mf-plannote';
            note.textContent = 'Nicht bestätigte Inhalte bleiben offen; die übrigen werden in dieser Sprache geladen.';
            summary.appendChild(note);
          }
        }
        if (missing > 500) {
          const note = document.createElement('div'); note.className = 'mf-plannote';
          note.textContent = 'Ein Serienauftrag mit ' + Math.ceil(missing / 500) + ' fortsetzbaren Arbeitsabschnitten.';
          summary.appendChild(note);
        }
        q('plan').appendChild(summary);
      };
      q('language').onchange = () => { q('partial').checked = false; syncProviders(); };
      q('partial').onchange = syncProviders; syncProviders();
    } catch (error) { if (!disposed && generation === detailGeneration && view.isConnected) { q('description').textContent = ''; q('plan').innerHTML = ''; dialogNotice(error.message, true); } }
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
      const payload = { planToken: detail.plan.plan_token || '', title: detail.title, seriesUrl: detail.seriesUrl, source: detail.source, mediaType: detail.mediaType, language: q('language').value, provider: q('provider').value, subtitleLanguage: detail.subtitleLanguage || 'none' };
      const result = await call('Requests/Automatic', { method: 'POST', body: payload });
      if (disposed || generation !== detailGeneration || !view.isConnected) return;
      const message = detail.title + ' wurde erfolgreich angefragt.' + (result.status === 'pending' ? ' Die Anfrage wartet auf Freigabe.' : ' Die Verarbeitung läuft im Hintergrund.');
      closeDetail(); notice(message);
    } catch (error) { if (!disposed && generation === detailGeneration && view.isConnected) { dialogNotice(error.message, true); refreshStatus(); } } finally { if (!disposed && generation === detailGeneration && view.isConnected) { q('request').textContent = 'Anfragen'; q('request').disabled = !q('language').value || !q('provider').value; guardRequestButton(); } }
  });
  async function openCatalogDetail(item, source) {
    const generation = ++detailGeneration;
    state.detail = null;
    q('overlay').style.display = 'flex'; focusDialog('close'); refreshStatus(); q('detail-title').textContent = item.title || 'Laden…'; q('description').textContent = 'Katalogdetails werden geladen…';
    q('plan').replaceChildren(); dialogNotice(''); q('request').disabled = true; q('request').textContent = 'Anfragen';
    q('partial-container').hidden = true;
    view.querySelector('.mf-options').hidden = true; q('catalog-options').hidden = false; q('format').replaceChildren(); q('catalog-target').textContent = '';
    try {
      const detail = await call('Catalog/Details', { query: { source, id: item.id, mediaType: item.media_kind } });
      if (disposed || generation !== detailGeneration || !view.isConnected) return;
      state.detail = Object.assign({}, detail, { backend: 'catalog' });
      q('detail-title').textContent = detail.title;
      q('description').textContent = [detail.author, detail.year, detail.language, detail.description].filter(Boolean).join(' · ');
      (detail.assets || []).forEach((asset) => { const option = document.createElement('option'); option.value = asset.id; option.textContent = (asset.label || asset.extension.toUpperCase()) + (asset.downloaded ? ' – bereits heruntergeladen' : ''); q('format').appendChild(option); });
      q('format').onchange = () => {
        if (!state.detail || state.detail.backend !== 'catalog') return;
        const asset = state.detail.assets.find((asset) => asset.id === q('format').value);
        q('request').disabled = !asset || asset.downloaded; guardRequestButton();
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
      closeDetail(); notice(detail.title + ' wurde erfolgreich angefragt.' + (result.status === 'pending' ? ' Die Anfrage wartet auf Freigabe.' : ' Die Verarbeitung läuft im Hintergrund.'));
    } catch (error) { if (!disposed && generation === detailGeneration && view.isConnected) { dialogNotice(error.message, true); refreshStatus(); } }
    finally { if (!disposed && generation === detailGeneration && view.isConnected) { q('request').disabled = false; guardRequestButton(); } }
  }
  function closeDetail() {
    detailGeneration++; state.detail = null; dialogNotice('');
    const wasOpen = q('overlay').style.display === 'flex';
    q('overlay').style.display = 'none';
    if (wasOpen && detailOpener && detailOpener.isConnected) detailOpener.focus();
    detailOpener = null;
  }
  let detailOpener = null;
  function focusDialog(name) {
    // A lingering confirmation toast must not cover the dialog's action buttons.
    if (toastTimer) { clearTimeout(toastTimer); toastTimer = null; }
    if (q('toast')) q('toast').classList.remove('show');
    // Remember the card that opened the dialog so keyboard users return to it.
    if (name === 'close' && !detailOpener && view.contains(document.activeElement)) detailOpener = document.activeElement;
    const target = q(name); if (target && typeof target.focus === 'function') target.focus({ preventScroll: true });
  }
  q('close').addEventListener('click', closeDetail); q('cancel').addEventListener('click', closeDetail); q('overlay').addEventListener('click', (e) => { if (e.target === q('overlay')) closeDetail(); });
  q('delete-overlay').addEventListener('click', (e) => { if (e.target === q('delete-overlay')) closeDeletePreview(); });
  view.addEventListener('keydown', (event) => {
    if (event.key !== 'Escape') return;
    if (q('delete-overlay').style.display === 'flex') { event.preventDefault(); closeDeletePreview(); }
    else if (q('overlay').style.display === 'flex') { event.preventDefault(); closeDetail(); }
  });

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
      const hasActiveDownload = items.some((item) => ['ready', 'processing', 'queued'].includes(item.status))
        || progress.some((item) => item.status === 'queued' || item.status === 'running');
      scheduleMineRefresh(hasActiveDownload);
      if (initial || !hasActiveDownload) loadDownloads(false);
    } catch (error) {
      if (disposed || !view.isConnected) return;
      if (!mineRendered) emptyLine(q('mine'), error.message);
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
      await Promise.all([loadDownloads(true), loadLibrary()]);
    } catch (error) {
      if (!disposed && view.isConnected) emptyLine(q('admin'), error.message);
    }
  }
  async function loadDownloads(admin) {
    const host = q(admin ? 'admin-downloads' : 'downloads');
    try {
      const groups = await call(admin ? 'Admin/Downloads' : 'Downloads/Mine');
      if (disposed || !view.isConnected) return;
      host.replaceChildren();
      if (!groups.length) emptyLine(host, 'Noch keine nachweislich zugeordneten Downloads vorhanden.');
      groups.forEach((group) => {
        const card = document.createElement('article'); card.className = 'mf-request';
        const title = document.createElement('div'); title.className = 'mf-requesttitle'; title.textContent = group.title; card.appendChild(title);
        const meta = document.createElement('div'); meta.className = 'mf-meta';
        meta.textContent = group.files.length + ' Dateien · Angefragt: ' + Array.from(new Set(group.files.map((file) => file.language))).join(', ');
        card.appendChild(meta);
        const files = document.createElement('div'); files.className = 'mf-filelist';
        group.files.forEach((file) => {
          const row = document.createElement('div'); row.className = 'mf-file';
          const name = document.createElement('div'); name.className = 'mf-filename'; name.textContent = file.name;
          const audio = (file.streams || []).filter((stream) => stream.codec_type === 'audio').map((stream) => stream.tags && stream.tags.language).filter(Boolean);
          const details = document.createElement('div');
          details.textContent = file.state + ' · Tonspuren: ' + (audio.join(', ') || 'unbekannt')
            + ' · ' + ({ verified: 'Sprache bestätigt', mismatch: 'Tonspur weicht von der Anfrage ab', unknown: 'Sprachprüfung unbestätigt' }[file.verification] || 'Sprachprüfung unbestätigt');
          if (file.verification === 'mismatch') row.classList.add('mf-error');
          if (file.subtitleLanguage === 'deu') {
            details.textContent += ' · Untertitel Deutsch: ' + ({ verified: 'bestätigt', missing: 'fehlen', unknown: 'Prüfung offen' }[file.subtitleVerification] || 'Prüfung offen');
            if (file.subtitleVerification === 'missing') row.classList.add('mf-error');
          }
          row.append(name, details); files.appendChild(row);
        });
        if (files.children.length) card.appendChild(files);
        if (group.canDelete) {
          const actions = document.createElement('div'); actions.className = 'mf-actions';
          const button = document.createElement('button'); button.type = 'button'; button.className = 'mf-btn danger'; button.textContent = admin ? 'Zugeordnete Downloads löschen' : 'Eigene Downloads / Serie löschen';
          button.onclick = () => deleteDownload((admin ? 'Admin/' : '') + 'Downloads', group.id, admin); actions.appendChild(button); card.appendChild(actions);
        }
        host.appendChild(card);
      });
    } catch (error) { if (!disposed && view.isConnected) emptyLine(host, error.message); }
  }
  async function deleteDownload(base, id, admin) {
    try {
      const preview = await call(base + '/' + encodeURIComponent(id) + '/Preview', { method: 'POST' });
      if (disposed || !view.isConnected) return;
      state.deletion = { base, admin, token: preview.token, running: false };
      q('delete-title').textContent = preview.title;
      q('delete-message').textContent = preview.message;
      q('delete-count').textContent = preview.count + ' Dateien werden endgültig entfernt:';
      q('delete-files').replaceChildren();
      preview.files.forEach((file) => { const row = document.createElement('li'); row.textContent = file.name; q('delete-files').appendChild(row); });
      q('delete-progress').textContent = '';
      q('delete-confirm').disabled = false;
      q('delete-overlay').style.display = 'flex'; q('delete-cancel').focus();
    } catch (error) { if (!disposed && view.isConnected) notice(error.message, true); }
  }
  function closeDeletePreview() {
    if (state.deletion && state.deletion.running) return;
    state.deletion = null; q('delete-overlay').style.display = 'none';
  }
  q('delete-cancel').addEventListener('click', closeDeletePreview); q('delete-close').addEventListener('click', closeDeletePreview);
  q('delete-confirm').addEventListener('click', async () => {
    const selection = state.deletion;
    if (!selection || selection.running) return;
    selection.running = true; q('delete-confirm').disabled = true;
    q('delete-progress').textContent = 'Dateien werden geprüft und gelöscht…';
    try {
      const result = await call(selection.base + '/Delete', { method: 'POST', body: { token: selection.token } });
      if (disposed || !view.isConnected) return;
      const conflicts = (result.items || []).filter((item) => item.status !== 'deleted').length;
      notice(conflicts ? conflicts + ' Dateien wurden wegen Änderungen oder Dateifehlern nicht gelöscht. Bitte neu prüfen.' : 'Dateien gelöscht. Jellyfin aktualisiert den Bibliotheksbestand.', conflicts > 0);
      await loadDownloads(selection.admin);
      if (selection.base === 'Admin/Library') await loadLibrary();
    } catch (error) { if (!disposed && view.isConnected) notice(error.message, true); }
    finally { selection.running = false; if (!disposed && view.isConnected) closeDeletePreview(); }
  });
  async function loadLibrary() {
    const host = q('admin-library');
    try {
      const items = await call('Admin/Library', { query: { query: q('library-query').value.trim() } });
      if (disposed || !view.isConnected) return;
      host.replaceChildren();
      if (!items.length) emptyLine(host, 'Keine passenden Bibliotheksinhalte.');
      items.forEach((item) => {
        const card = document.createElement('article'); card.className = 'mf-request';
        const top = document.createElement('div'); top.className = 'mf-requesttop';
        const title = document.createElement('div'); title.className = 'mf-requesttitle'; title.textContent = item.title;
        const kind = document.createElement('span'); kind.className = 'mf-pill'; kind.textContent = categoryLabel(item.mediaType);
        top.append(title, kind);
        const actions = document.createElement('div'); actions.className = 'mf-actions';
        const button = document.createElement('button'); button.type = 'button'; button.className = 'mf-btn danger'; button.textContent = 'Als Admin vollständig löschen';
        button.onclick = () => deleteDownload('Admin/Library', item.id, true); actions.appendChild(button);
        card.append(top, actions); host.appendChild(card);
      });
    } catch (error) { if (!disposed && view.isConnected) emptyLine(host, error.message); }
  }
  q('library-search').addEventListener('submit', (event) => { event.preventDefault(); loadLibrary(); });
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
    card.querySelector('[data-role="meta"]').textContent = (admin ? item.username + ' · ' : '') + categoryLabel(item.mediaType) + ' · ' + (item.selectionLabel || ((item.episodes || []).length + ' Episoden')) + ' · ' + item.language + (item.subtitleLanguage === 'deu' ? ' · Untertitel: Deutsch' : '') + ' · ' + new Date(item.createdUtc).toLocaleString();
    const pill = card.querySelector('[data-role="pill"]'); pill.className = 'mf-pill ' + item.status; pill.textContent = progressLabel(progress) || statusLabel(item.status);
    const progressBox = card.querySelector('[data-role="progress"]'); progressBox.hidden = !progress;
    if (progress) {
      card.querySelector('[data-role="progress-fill"]').style.width = Math.max(0, Math.min(100, Number(progress.percent) || 0)) + '%';
      card.querySelector('[data-role="progress-detail"]').textContent = progressDetail(progress);
    }
    const error = card.querySelector('[data-role="error"]'); error.hidden = !item.error; error.textContent = item.error || '';
    const actions = card.querySelector('[data-role="actions"]'); actions.innerHTML = '';
    if (admin && (item.status === 'pending' || item.status === 'failed')) {
      const approve = document.createElement('button'); approve.className = 'mf-btn'; approve.textContent = item.status === 'failed' ? 'Erneut versuchen' : 'Freigeben'; approve.dataset.mfHandover = '1'; approve.disabled = Boolean(state.status.maintenance); if (approve.disabled) approve.title = 'Im Wartungsmodus deaktiviert'; approve.onclick = () => decide(item.id, 'Approve');
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
  function statusLabel(status) { return ({ pending: 'Wartet auf Freigabe', ready: 'Wartet auf Vorbereitung', processing: 'Wird vorbereitet', queued: 'Wartet auf Download', completed: 'Fertig', available: 'Bereits in Jellyfin vorhanden', partial: 'Teilweise fertig', cancelled: 'Abgebrochen', rejected: 'Abgelehnt', withdrawn: 'Zurückgezogen', failed: 'Fehlgeschlagen' })[status] || status; }
  q('refresh-mine').addEventListener('click', () => { loadMine({ initial: !mineRendered }); loadDownloads(false); }); q('refresh-admin').addEventListener('click', loadAdmin);
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
    if (toastTimer) clearTimeout(toastTimer);
    searchGeneration++;
    detailGeneration++;
    discoverGeneration++;
  };
}
