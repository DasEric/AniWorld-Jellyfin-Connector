export default function (view) {
  const pluginId = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890';
  const byId = (id) => view.querySelector('#' + id);
  const connector = (path, type, body) => {
    const request = { url: ApiClient.getUrl('AniWorldRequests/' + path), type: type || 'GET', dataType: 'json' };
    if (body !== undefined) {
      request.headers = { 'Content-Type': 'application/json' };
      request.data = JSON.stringify(body);
    }
    return ApiClient.fetch(request);
  };
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
    return 'Verbindung fehlgeschlagen. Bitte URL, API-Key und installiertes AniWorld-System prüfen.';
  }
  function showKeyStatus(hasKey) {
    const input = byId('mfApiKey');
    input.value = '';
    input.placeholder = hasKey ? 'Sicher gespeichert – leer lassen, um beizubehalten' : 'Neuen API-Key eingeben';
    byId('mfClearApiKey').disabled = !hasKey;
  }
  async function load() {
    if (typeof Dashboard !== 'undefined') Dashboard.showLoadingMsg();
    try {
      const [config, secret] = await Promise.all([
        ApiClient.getPluginConfiguration(pluginId),
        connector('Admin/ApiKey')
      ]);
      byId('mfUrl').value = config.AniWorldUrl || 'http://127.0.0.1:8080';
      showKeyStatus(secret.hasApiKey === true);
      byId('mfAutoApprove').checked = config.AutoApproveRequests === true;
      byId('mfAllowEbooks').checked = config.AllowEbookRequests === true;
      byId('mfAllowAudiobooks').checked = config.AllowAudiobookRequests === true;
      byId('mfAllowPodcasts').checked = config.AllowPodcastRequests === true;
      byId('mfAllUsers').checked = config.EnableAllUsers !== false;
      byId('mfMaintenance').checked = config.MaintenanceMode === true;
      byId('mfMaintenanceMessage').value = config.MaintenanceMessage || '';
      byId('mfMaxPending').value = config.MaxPendingRequestsPerUser || 10;
      byId('mfAllowedSources').value = config.AllowedSources || '';
      byId('mfDefaultLanguage').value = config.DefaultLanguage || 'German Dub';
      byId('mfDefaultProvider').value = config.DefaultProvider || 'VOE';
      byId('mfMaxSources').value = config.MaxSearchSources || 8;
      byId('mfMediaRouting').checked = config.EnableMediaRouting === true;
      await loadTargets(config);
    } finally {
      if (typeof Dashboard !== 'undefined') Dashboard.hideLoadingMsg();
    }
  }
  async function save(event) {
    event.preventDefault();
    if (typeof Dashboard !== 'undefined') Dashboard.showLoadingMsg();
    const target = byId('mfTestResult');
    try {
      const config = await ApiClient.getPluginConfiguration(pluginId);
      const oldUrl = config.AniWorldUrl;
      delete config.AniWorldApiKey;
      config.AniWorldUrl = byId('mfUrl').value.trim();
      config.AutoApproveRequests = byId('mfAutoApprove').checked;
      config.AllowEbookRequests = byId('mfAllowEbooks').checked;
      config.AllowAudiobookRequests = byId('mfAllowAudiobooks').checked;
      config.AllowPodcastRequests = byId('mfAllowPodcasts').checked;
      config.EnableAllUsers = byId('mfAllUsers').checked;
      config.MaintenanceMode = byId('mfMaintenance').checked;
      config.MaintenanceMessage = byId('mfMaintenanceMessage').value.trim();
      config.MaxPendingRequestsPerUser = Math.max(1, Math.min(100, parseInt(byId('mfMaxPending').value, 10) || 10));
      config.AllowedSources = byId('mfAllowedSources').value.trim();
      config.DefaultLanguage = byId('mfDefaultLanguage').value.trim() || 'German Dub';
      config.DefaultProvider = byId('mfDefaultProvider').value.trim() || 'VOE';
      config.MaxSearchSources = Math.max(1, Math.min(32, parseInt(byId('mfMaxSources').value, 10) || 8));
      config.EnableMediaRouting = byId('mfMediaRouting').checked;
      if (config.EnableMediaRouting) {
        if (config.AniWorldUrl !== oldUrl || byId('mfApiKey').value.trim()) throw new Error('Verbindung/API-Key zuerst mit ausgeschalteter Zielzuordnung speichern und die Ziele danach neu laden.');
        const movie = JSON.parse(byId('mfMovieLibrary').value || '{}'); const series = JSON.parse(byId('mfSeriesLibrary').value || '{}');
        config.MoviePathId = Number(byId('mfMoviePath').value); config.SeriesPathId = Number(byId('mfSeriesPath').value);
        config.MovieLibraryId = movie.id || ''; config.MovieLibraryPath = movie.path || '';
        config.SeriesLibraryId = series.id || ''; config.SeriesLibraryPath = series.path || '';
        const checked = await connector('Admin/Targets/Validate', 'POST', config);
        config.MovieDownloadRoot = checked.movieDownloadRoot; config.SeriesDownloadRoot = checked.seriesDownloadRoot;
      }
      const result = await ApiClient.updatePluginConfiguration(pluginId, config);
      const newApiKey = byId('mfApiKey').value.trim();
      if (newApiKey) await connector('Admin/ApiKey', 'POST', { apiKey: newApiKey });
      showKeyStatus(newApiKey ? true : !byId('mfClearApiKey').disabled);
      target.textContent = 'Einstellungen sicher gespeichert.';
      target.style.color = '#52b54b';
      if (typeof Dashboard !== 'undefined') Dashboard.processPluginConfigurationUpdateResult(result);
    } catch (error) {
      target.textContent = await readErrorMessage(error);
      target.style.color = '#e35b64';
    } finally {
      if (typeof Dashboard !== 'undefined') Dashboard.hideLoadingMsg();
    }
  }
  async function clearApiKey() {
    if (!window.confirm('Den gespeicherten AniWorld API-Key wirklich löschen?')) return;
    const target = byId('mfTestResult');
    try {
      await connector('Admin/ApiKey', 'DELETE');
      showKeyStatus(false);
      target.textContent = 'API-Key gelöscht.';
      target.style.color = '#52b54b';
    } catch (_) {
      target.textContent = 'API-Key konnte nicht gelöscht werden.';
      target.style.color = '#e35b64';
    }
  }
  async function loadTargets(config) {
    try {
      config = config || await ApiClient.getPluginConfiguration(pluginId);
      const data = await connector('Admin/Targets');
      if (data.mediaTargets && data.mediaTargets.enabled) {
        config.MoviePathId = data.mediaTargets.movie;
        config.SeriesPathId = data.mediaTargets.series;
      }
      function fill(id, options, selected) {
        const select = byId(id); select.replaceChildren();
        const empty = document.createElement('option'); empty.value = ''; empty.textContent = 'Bitte auswählen'; select.appendChild(empty);
        options.forEach((item) => { const option = document.createElement('option'); option.value = item.value; option.textContent = item.label; select.appendChild(option); });
        if (selected !== undefined) select.value = String(selected);
      }
      const paths = data.paths.filter((path) => path.id > 0).map((path) => ({ value: String(path.id), label: path.name + ' — ' + path.path }));
      const libraries = (type) => data.libraries.filter((library) => library.type === type).flatMap((library) => library.paths.map((path) => ({ value: JSON.stringify({ id: library.id, path }), label: library.name + ' — ' + path })));
      fill('mfMoviePath', paths, config.MoviePathId); fill('mfSeriesPath', paths, config.SeriesPathId);
      fill('mfMovieLibrary', libraries('movies'), JSON.stringify({ id: config.MovieLibraryId, path: config.MovieLibraryPath }));
      fill('mfSeriesLibrary', libraries('tvshows'), JSON.stringify({ id: config.SeriesLibraryId, path: config.SeriesLibraryPath }));
      byId('mfTargetStatus').textContent = data.mediaTargets && data.mediaTargets.enabled
        ? 'Zentrale Medienziele des Downloaders geladen. Die Typzuordnung gilt auch für direkte Downloader-Downloads. Änderungen hier werden nach erfolgreicher Bibliotheksprüfung im Downloader gespeichert.'
        : 'Noch keine zentralen Medienziele eingerichtet. Beide Mounts und Bibliotheken auswählen und die Zuordnung aktivieren; anschließend gelten die Ziele auch im Downloader.';
    } catch (error) { byId('mfTargetStatus').textContent = 'Ziele nicht geladen: ' + await readErrorMessage(error); }
  }
  async function test() {
    const target = byId('mfTestResult');
    target.textContent = 'Verbindung wird getestet…';
    try {
      const result = await connector('Admin/Test', 'POST', {});
      target.textContent = result.ok ? 'Verbindung erfolgreich – ' + (result.version || '') + ' · Admin/Full Access' : 'Unerwartete Antwort von AniWorld.';
      target.style.color = '#52b54b';
    } catch (error) {
      target.textContent = await readErrorMessage(error);
      target.style.color = '#e35b64';
    }
  }
  view.querySelector('#AniWorldRequestsConfigForm').addEventListener('submit', save);
  byId('mfTest').addEventListener('click', test);
  byId('mfClearApiKey').addEventListener('click', clearApiKey);
  byId('mfLoadTargets').addEventListener('click', () => loadTargets());
  view.addEventListener('viewshow', load);
  load();
}
