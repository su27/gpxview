(function initializeBrowserApplication(global) {
  'use strict';
  if (global.gpxHost?.platform !== 'browser') return;

  const palette = ['#176bde', '#d65b35', '#0f9d7a', '#8e63ce', '#d08a18', '#247ba0', '#c44569'];
  const state = {
    pageReady: false,
    domReady: false,
    activeTrackId: null,
    tracks: new Map(),
    sources: new Map(),
    mapStyle: readSetting('mapStyle', 'openfreemap'),
    sourceCoordinateSystem: readSetting('sourceCoordinateSystem', 'wgs84'),
    darkTheme: readSetting('theme', matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light') === 'dark',
    terrainEnabled: false,
    hasUserLocation: false,
    road: {
      authenticated: false,
      identity: null,
      archives: [],
      selectedIds: new Set(readJsonSetting('roadNetworkIds', [])),
      hadSavedSelection: hasSetting('roadNetworkIds'),
      enabled: readSetting('roadNetworkEnabled', 'true') !== 'false'
    },
    roadSessionRefreshAt: 0
  };
  const elements = {};
  let toastTimer = 0;
  let dragDepth = 0;
  const emit = global.gpxHost.attachBrowser(handlePageMessage);

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initializeDom, { once: true });
  else initializeDom();

  function initializeDom() {
    if (state.domReady) return;
    state.domReady = true;
    document.body.classList.add('browser-mode');
    document.title = 'GpxView Web';
    const layout = document.getElementById('layout');
    layout.insertAdjacentHTML('beforeend', `
      <header id="browserToolbar" class="glass" aria-label="网页版工具栏">
        <button id="browserOpen" class="browserButton primary" type="button"><span>＋</span><span class="wideLabel">打开轨迹</span></button>
        <select id="browserMapStyle" class="browserSelect" aria-label="底图">
          <option value="openfreemap">现代地图</option><option value="outdoor">户外地图</option><option value="osm">经典地图</option>
          <option value="satellite">卫星影像</option><option value="topo">等高线</option><option value="humanitarian">人道主义</option>
        </select>
        <select id="browserCoordinates" class="browserSelect" aria-label="源坐标系">
          <option value="wgs84">WGS84</option><option value="gcj02">GCJ-02</option><option value="bd09">BD-09</option>
        </select>
        <button id="browserLocate" class="browserButton" type="button" title="定位到当前位置" aria-label="定位到当前位置"><svg viewBox="0 0 20 20" aria-hidden="true"><circle cx="10" cy="10" r="4"></circle><path d="M10 2v3M10 15v3M2 10h3M15 10h3"></path></svg><span class="wideLabel">当前位置</span></button>
        <button id="browserRoad" class="browserButton locked" type="button">路网</button>
        <button id="browserTerrain" class="browserButton icon" type="button" title="切换三维地形">3D</button>
        <button id="browserTheme" class="browserButton icon" type="button" title="切换主题">◐</button>
        <span id="browserToolbarSpacer"></span><span id="browserStatus">可打开 GPX、KML、KMZ 和 FIT，文件不会上传</span>
      </header>
      <div id="browserDropOverlay" hidden>松开以打开轨迹</div>
      <div id="browserRoadScrim" hidden></div><section id="browserRoadPanel" class="glass" hidden></section>
      <div id="browserToast" hidden></div>
      <input id="browserFileInput" type="file" accept=".gpx,.kml,.kmz,.fit,application/gpx+xml,application/vnd.google-earth.kml+xml,application/vnd.google-earth.kmz,application/vnd.ant.fit" multiple hidden>
    `);
    Object.assign(elements, {
      open: document.getElementById('browserOpen'),
      fileInput: document.getElementById('browserFileInput'),
      mapStyle: document.getElementById('browserMapStyle'),
      coordinates: document.getElementById('browserCoordinates'),
      locate: document.getElementById('browserLocate'),
      road: document.getElementById('browserRoad'),
      terrain: document.getElementById('browserTerrain'),
      theme: document.getElementById('browserTheme'),
      status: document.getElementById('browserStatus'),
      dropOverlay: document.getElementById('browserDropOverlay'),
      roadScrim: document.getElementById('browserRoadScrim'),
      roadPanel: document.getElementById('browserRoadPanel'),
      toast: document.getElementById('browserToast')
    });
    elements.mapStyle.value = state.mapStyle;
    elements.coordinates.value = state.sourceCoordinateSystem;
    elements.open.addEventListener('click', chooseFiles);
    elements.fileInput.addEventListener('change', () => {
      void openFiles(elements.fileInput.files);
      elements.fileInput.value = '';
    });
    elements.mapStyle.addEventListener('change', () => {
      state.mapStyle = elements.mapStyle.value;
      writeSetting('mapStyle', state.mapStyle);
      emit({ type: 'setMapStyle', mapStyle: state.mapStyle });
    });
    elements.coordinates.addEventListener('change', () => {
      state.sourceCoordinateSystem = elements.coordinates.value;
      writeSetting('sourceCoordinateSystem', state.sourceCoordinateSystem);
      void reparseTracks();
    });
    elements.locate.addEventListener('click', locateCurrentPosition);
    if (!navigator.geolocation || !isSecureContext) {
      elements.locate.disabled = true;
      elements.locate.title = '当前浏览器不支持安全定位';
    }
    elements.road.addEventListener('click', openRoadPanel);
    elements.roadScrim.addEventListener('click', closeRoadPanel);
    elements.terrain.addEventListener('click', () => emit({ type: 'setTerrainEnabled', enabled: !state.terrainEnabled }));
    elements.theme.addEventListener('click', toggleTheme);
    addEventListener('dragenter', onDragEnter);
    addEventListener('dragover', onDragOver);
    addEventListener('dragleave', onDragLeave);
    addEventListener('drop', onDrop);
    addEventListener('keydown', event => {
      if (event.key === 'Escape' && !elements.roadPanel.hidden) closeRoadPanel();
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'o') {
        event.preventDefault();
        chooseFiles();
      }
    });
    addEventListener('error', event => showToast(event.message || '网页发生错误'));
    updateTheme();
    updateRoadButton();
    if (state.pageReady) sendInitialState();
    void restoreRoadAccess();
    setInterval(() => { if (state.road.authenticated) void refreshRoadSession(false); }, 45 * 60 * 1000);
    document.addEventListener('visibilitychange', () => {
      if (!document.hidden && state.road.authenticated && Date.now() >= state.roadSessionRefreshAt) {
        void refreshRoadSession(false);
      }
    });
  }

  function handlePageMessage(message) {
    switch (message?.type) {
      case 'ready':
        state.pageReady = true;
        if (state.domReady) sendInitialState();
        break;
      case 'openFile': chooseFiles(); break;
      case 'selectTrack': selectTrack(message.id); break;
      case 'setTrackVisibility': setTrackVisibility(message.id, Boolean(message.visible)); break;
      case 'closeTrack': closeTrack(message.id); break;
      case 'terrainState':
        const terrainWasEnabled = state.terrainEnabled;
        state.terrainEnabled = Boolean(message.enabled);
        elements.terrain?.classList.toggle('active', state.terrainEnabled);
        elements.terrain && (elements.terrain.textContent = state.terrainEnabled ? '2D' : '3D');
        if (!terrainWasEnabled && state.terrainEnabled && matchMedia('(max-width:560px),(pointer:coarse)').matches) showToast('双指上下拖动可调整俯仰角度');
        if (message.error) showToast(message.error);
        break;
      case 'mapError': if (message.error) showToast(message.error); break;
      case 'openProjectHome': global.open('https://github.com/su27/gpxview', '_blank', 'noopener'); break;
      case 'setLanguage': break;
    }
  }

  function sendInitialState() {
    emit({ type: 'setTheme', theme: state.darkTheme ? 'dark' : 'light' });
    emit({ type: 'setMapStyle', mapStyle: state.mapStyle });
    emit({ type: 'setSettings', language: 'zh-CN', resolvedLocale: 'zh-CN', geocodingAvailable: false, geocodingEnabled: false, version: 'Web', channel: 'GitHub', fileAssociations: [] });
    emitTracks(false);
    applyRoadConfig();
  }

  function chooseFiles() {
    elements.fileInput?.click();
  }

  async function openFiles(fileList) {
    const files = Array.from(fileList || []);
    if (!files.length) return;
    const supported = files.filter(file => /\.(gpx|kml|kmz|fit)$/i.test(file.name));
    const rejected = files.length - supported.length;
    const errors = [];
    let opened = 0;
    for (let index = 0; index < supported.length; index++) {
      const file = supported[index];
      setStatus(`正在解析 ${index + 1}/${supported.length} · ${file.name}`);
      try {
        const id = crypto.randomUUID();
        const color = palette[state.sources.size % palette.length];
        const track = await global.gpxBrowserTrack.parseFile(file, { id, color, sourceCoordinateSystem: state.sourceCoordinateSystem });
        state.sources.set(id, { file, color, visible: true });
        state.tracks.set(id, track);
        state.activeTrackId = id;
        opened++;
      } catch (error) {
        errors.push(`${file.name}：${error?.message || '无法读取'}`);
      }
    }
    emitTracks(opened > 0);
    setStatus(state.tracks.size ? `已打开 ${state.tracks.size} 条轨迹` : '尚未打开轨迹');
    if (rejected) errors.push(`${rejected} 个不支持的文件已跳过（网页版支持 GPX、KML、KMZ、FIT）`);
    if (errors.length) showToast(errors.slice(0, 3).join('\n'));
  }

  async function reparseTracks() {
    if (!state.sources.size) return;
    setStatus(`正在按 ${state.sourceCoordinateSystem.toUpperCase()} 重新解析…`);
    const nextTracks = new Map();
    const errors = [];
    for (const [id, source] of state.sources) {
      try {
        const track = await global.gpxBrowserTrack.parseFile(source.file, {
          id,
          color: source.color,
          visible: source.visible,
          sourceCoordinateSystem: state.sourceCoordinateSystem
        });
        nextTracks.set(id, track);
      } catch (error) {
        errors.push(`${source.file.name}：${error?.message || '无法读取'}`);
      }
    }
    state.tracks = nextTracks;
    if (!state.tracks.has(state.activeTrackId)) state.activeTrackId = state.tracks.keys().next().value || null;
    emitTracks(true);
    setStatus(`已按 ${state.sourceCoordinateSystem.toUpperCase()} 重新解析 ${state.tracks.size} 条轨迹`);
    if (errors.length) showToast(errors.join('\n'));
  }

  function emitTracks(fit) {
    if (!state.pageReady) return;
    emit({ type: 'setTracks', tracks: Array.from(state.tracks.values()), activeTrackId: state.activeTrackId, fit });
  }

  function selectTrack(id) {
    if (!state.tracks.has(id)) return;
    state.activeTrackId = id;
    emit({ type: 'setActiveTrack', id });
  }

  function setTrackVisibility(id, visible) {
    const track = state.tracks.get(id);
    const source = state.sources.get(id);
    if (!track) return;
    track.visible = visible;
    if (source) source.visible = visible;
    emit({ type: 'setTrackVisibility', id, visible });
  }

  function closeTrack(id) {
    if (!state.tracks.has(id)) return;
    state.tracks.delete(id);
    state.sources.delete(id);
    if (state.activeTrackId === id) state.activeTrackId = state.tracks.keys().next().value || null;
    emit({ type: 'removeTrack', id, activeTrackId: state.activeTrackId });
    if (!state.tracks.size) {
      setStatus('可打开 GPX、KML、KMZ 和 FIT，文件不会上传');
    } else setStatus(`已打开 ${state.tracks.size} 条轨迹`);
  }

  function onDragEnter(event) {
    if (!hasFiles(event)) return;
    event.preventDefault();
    dragDepth++;
    elements.dropOverlay.hidden = false;
  }

  function onDragOver(event) {
    if (!hasFiles(event)) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = 'copy';
  }

  function onDragLeave(event) {
    if (!hasFiles(event)) return;
    event.preventDefault();
    dragDepth = Math.max(0, dragDepth - 1);
    if (!dragDepth) elements.dropOverlay.hidden = true;
  }

  function onDrop(event) {
    if (!hasFiles(event)) return;
    event.preventDefault();
    dragDepth = 0;
    elements.dropOverlay.hidden = true;
    void openFiles(event.dataTransfer.files);
  }

  function hasFiles(event) {
    return Array.from(event.dataTransfer?.types || []).includes('Files');
  }

  function toggleTheme() {
    state.darkTheme = !state.darkTheme;
    writeSetting('theme', state.darkTheme ? 'dark' : 'light');
    updateTheme();
  }

  function locateCurrentPosition() {
    if (!navigator.geolocation || !isSecureContext || elements.locate.disabled) return;
    elements.locate.disabled = true;
    elements.locate.setAttribute('aria-busy', 'true');
    elements.locate.title = '正在获取当前位置…';
    navigator.geolocation.getCurrentPosition(position => {
      const latitude = Number(position.coords.latitude);
      const longitude = Number(position.coords.longitude);
      if (!Number.isFinite(latitude) || !Number.isFinite(longitude)) {
        finishLocationRequest('无法读取有效的当前位置');
        return;
      }
      state.hasUserLocation = true;
      elements.locate.classList.add('active');
      emit({
        type: 'setUserLocation',
        latitude,
        longitude,
        accuracyMeters: Number.isFinite(position.coords.accuracy) ? position.coords.accuracy : null,
        fit: true
      });
      const accuracy = Number(position.coords.accuracy);
      finishLocationRequest(null, Number.isFinite(accuracy) ? `已定位 · 精度约 ${Math.round(accuracy)} m` : '已定位到当前位置');
    }, error => {
      const message = error.code === 1
        ? '定位权限未授予，请在浏览器站点设置中允许定位'
        : error.code === 3 ? '获取当前位置超时，请重试' : '暂时无法获取当前位置';
      finishLocationRequest(message);
    }, { enableHighAccuracy: true, timeout: 15000, maximumAge: 30000 });
  }

  function finishLocationRequest(error, success) {
    elements.locate.disabled = false;
    elements.locate.removeAttribute('aria-busy');
    elements.locate.title = state.hasUserLocation ? '更新当前位置' : '定位到当前位置';
    if (error) showToast(error);
    else if (success) showToast(success);
  }

  function updateTheme() {
    elements.theme?.classList.toggle('active', state.darkTheme);
    emit({ type: 'setTheme', theme: state.darkTheme ? 'dark' : 'light' });
  }

  async function restoreRoadAccess() {
    if (!state.domReady) return;
    try {
      const status = await requestJson('/v1/web/status');
      state.road.identity = status;
      state.road.authenticated = true;
      state.roadSessionRefreshAt = refreshAtFromExpiry(status.expiresAt);
      await loadRoadCatalog();
      return;
    } catch (error) {
      if (error.status !== 401) {
        setStatus('私有路网服务暂时不可用');
        return;
      }
    }
    await refreshRoadSession(false);
  }

  async function refreshRoadSession(showErrors) {
    try {
      const identity = await requestJson('/v1/web/session', { method: 'POST' });
      state.road.identity = identity;
      state.road.authenticated = true;
      state.roadSessionRefreshAt = Date.now() + Math.max(60, (Number(identity.expiresIn) || 3600) - 300) * 1000;
      await loadRoadCatalog();
      return true;
    } catch (error) {
      if (error.status === 401) setRoadLocked();
      else if (showErrors) showToast('暂时无法刷新路网授权');
      return false;
    }
  }

  async function loadRoadCatalog() {
    const catalog = await requestJson('/v1/catalog');
    state.road.archives = Array.isArray(catalog.archives) ? catalog.archives : [];
    const validIds = new Set(state.road.archives.map(archive => archive.id));
    state.road.selectedIds = new Set(Array.from(state.road.selectedIds).filter(id => validIds.has(id)));
    if (!state.road.hadSavedSelection && !state.road.selectedIds.size) {
      state.road.archives.forEach(archive => state.road.selectedIds.add(archive.id));
    }
    state.road.authenticated = true;
    updateRoadButton();
    applyRoadConfig();
    if (!elements.roadPanel.hidden) renderRoadPanel();
  }

  function setRoadLocked() {
    state.road.authenticated = false;
    state.road.identity = null;
    state.road.archives = [];
    state.roadSessionRefreshAt = 0;
    updateRoadButton();
    applyRoadConfig();
    if (elements.roadPanel && !elements.roadPanel.hidden) renderRoadPanel();
  }

  function applyRoadConfig() {
    if (!state.pageReady) return;
    const selectedArchives = state.road.archives.filter(archive => state.road.selectedIds.has(archive.id));
    const bounds = unionBounds(selectedArchives.map(archive => archive.bounds));
    emit({
      type: 'setRoadNetworkConfig',
      config: {
        available: state.road.authenticated && state.road.archives.length > 0,
        enabled: state.road.enabled,
        selectedIds: Array.from(state.road.selectedIds),
        bounds,
        archives: state.road.archives.map(archive => ({
          ...archive,
          name: localizedArchiveName(archive),
          url: new URL(archive.path, location.origin).href
        }))
      }
    });
  }

  function unionBounds(boundsList) {
    const valid = boundsList.filter(bounds => Array.isArray(bounds) && bounds.length === 4);
    if (!valid.length) return null;
    return [Math.min(...valid.map(bounds => bounds[0])), Math.min(...valid.map(bounds => bounds[1])), Math.max(...valid.map(bounds => bounds[2])), Math.max(...valid.map(bounds => bounds[3]))];
  }

  function updateRoadButton() {
    if (!elements.road) return;
    const active = state.road.authenticated && state.road.enabled && state.road.selectedIds.size > 0;
    elements.road.classList.toggle('locked', !state.road.authenticated);
    elements.road.classList.toggle('active', active);
    elements.road.title = state.road.authenticated ? `已授权 ${state.road.archives.length} 个路网，点击管理` : '需要激活码才能查看私有路网';
  }

  function openRoadPanel() {
    elements.roadPanel.hidden = false;
    elements.roadScrim.hidden = false;
    renderRoadPanel();
  }

  function closeRoadPanel() {
    elements.roadPanel.hidden = true;
    elements.roadScrim.hidden = true;
  }

  function renderRoadPanel() {
    const panel = elements.roadPanel;
    panel.replaceChildren();
    const header = document.createElement('header');
    header.className = 'browserPanelHeader';
    const title = document.createElement('h2');
    title.textContent = '私有路网';
    const close = document.createElement('button');
    close.className = 'iconClose'; close.type = 'button'; close.textContent = '×'; close.title = '关闭';
    close.addEventListener('click', closeRoadPanel);
    header.append(title, close);
    panel.append(header);
    if (!state.road.authenticated) renderRoadEnrollment(panel);
    else renderRoadSelection(panel);
  }

  function renderRoadEnrollment(panel) {
    const copy = document.createElement('p');
    copy.className = 'browserPanelCopy';
    copy.textContent = '北京和河北历史轨迹密度路网仅向获得授权的设备开放。激活后，凭证保存在受保护的浏览器 Cookie 中，页面脚本无法读取。';
    const field = document.createElement('label');
    field.className = 'browserField'; field.textContent = '一次性激活码';
    const input = document.createElement('input');
    input.className = 'browserInput'; input.type = 'text'; input.autocomplete = 'one-time-code'; input.spellcheck = false; input.maxLength = 64; input.placeholder = 'XXXX-XXXX-XXXX-XXXX';
    field.append(input);
    const terms = document.createElement('label');
    terms.className = 'browserTerms';
    const checkbox = document.createElement('input'); checkbox.type = 'checkbox';
    const termsText = document.createElement('span');
    termsText.textContent = '我确认仅将路网用于获授权的个人查看，不批量下载、转存或向第三方传播。';
    terms.append(checkbox, termsText);
    const error = document.createElement('div'); error.className = 'browserPanelError';
    const actions = document.createElement('div'); actions.className = 'browserPanelActions';
    const activate = document.createElement('button'); activate.className = 'browserButton primary'; activate.type = 'button'; activate.textContent = '激活此浏览器';
    activate.addEventListener('click', async () => {
      error.textContent = '';
      const code = input.value.trim();
      if (!checkbox.checked) { error.textContent = '请先确认授权使用范围。'; return; }
      if (code.replace(/\s+/g, '').length < 12) { error.textContent = '请输入有效的激活码。'; return; }
      activate.disabled = true; activate.textContent = '正在激活…';
      try {
        await requestJson('/v1/web/enroll', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ code, deviceName: browserDeviceName(), clientVersion: 'GpxView Web', acceptTerms: true })
        });
        if (!await refreshRoadSession(false)) throw new Error('persistent_session_failed');
        renderRoadPanel();
        showToast('当前浏览器已获得路网授权');
      } catch (requestError) {
        error.textContent = requestError.status === 401 ? '激活码无效、已使用或已过期。' : '暂时无法连接授权服务，请稍后重试。';
        activate.disabled = false; activate.textContent = '激活此浏览器';
      }
    });
    actions.append(activate);
    panel.append(copy, field, terms, error, actions);
    setTimeout(() => input.focus(), 0);
  }

  function renderRoadSelection(panel) {
    const identity = document.createElement('div'); identity.className = 'browserIdentity';
    const identityName = document.createElement('strong'); identityName.textContent = state.road.identity?.displayName || state.road.identity?.accountId || '已授权设备';
    const identityMeta = document.createElement('span'); identityMeta.textContent = `当前浏览器 · ${state.road.archives.length} 个可用路网`;
    identity.append(identityName, identityMeta);
    const copy = document.createElement('p'); copy.className = 'browserPanelCopy'; copy.textContent = '选择需要显示的省份。多个路网使用各自稳定 ID，不依赖目录顺序。';
    const list = document.createElement('div'); list.className = 'browserArchiveList';
    state.road.archives.forEach(archive => {
      const row = document.createElement('label'); row.className = 'browserArchive';
      const checkbox = document.createElement('input'); checkbox.type = 'checkbox'; checkbox.checked = state.road.selectedIds.has(archive.id);
      const archiveCopy = document.createElement('span'); archiveCopy.className = 'browserArchiveCopy';
      const name = document.createElement('span'); name.className = 'browserArchiveName'; name.textContent = localizedArchiveName(archive);
      const meta = document.createElement('span'); meta.className = 'browserArchiveMeta'; meta.textContent = `缩放 ${archive.minZoom}–${archive.maxZoom} · ${formatBytes(archive.bytes)}`;
      archiveCopy.append(name, meta); row.append(checkbox, archiveCopy); list.append(row);
      checkbox.addEventListener('change', () => {
        checkbox.checked ? state.road.selectedIds.add(archive.id) : state.road.selectedIds.delete(archive.id);
        state.road.hadSavedSelection = true;
        writeJsonSetting('roadNetworkIds', Array.from(state.road.selectedIds));
        updateRoadButton(); applyRoadConfig();
      });
    });
    const actions = document.createElement('div'); actions.className = 'browserPanelActions';
    const logout = document.createElement('button'); logout.className = 'browserButton browserDanger'; logout.type = 'button'; logout.textContent = '撤销此浏览器授权';
    logout.addEventListener('click', async () => {
      logout.disabled = true;
      try {
        await requestJson('/v1/web/logout', { method: 'POST' });
        setRoadLocked(); renderRoadPanel(); showToast('已撤销当前浏览器的路网授权');
      } catch {
        logout.disabled = false;
        showToast('暂时无法撤销授权，请检查网络后重试');
      }
    });
    const enabled = document.createElement('button'); enabled.className = `browserButton${state.road.enabled ? ' active' : ''}`; enabled.type = 'button'; enabled.textContent = state.road.enabled ? '路网已显示' : '路网已隐藏';
    enabled.addEventListener('click', () => {
      state.road.enabled = !state.road.enabled;
      writeSetting('roadNetworkEnabled', String(state.road.enabled));
      emit({ type: 'setRoadNetworkEnabled', enabled: state.road.enabled });
      updateRoadButton(); renderRoadPanel();
    });
    actions.append(logout, enabled);
    panel.append(identity, copy, list, actions);
  }

  async function requestJson(path, options = {}) {
    const response = await fetch(path, { credentials: 'same-origin', cache: 'no-store', ...options });
    let data = null;
    try { data = await response.json(); } catch { /* Empty or non-JSON error response. */ }
    if (!response.ok) {
      const error = new Error(data?.error || `HTTP ${response.status}`);
      error.status = response.status;
      throw error;
    }
    return data || {};
  }

  function refreshAtFromExpiry(expiresAt) {
    const expiryMilliseconds = Number(expiresAt) * 1000;
    return Number.isFinite(expiryMilliseconds)
      ? Math.max(Date.now(), expiryMilliseconds - 5 * 60 * 1000)
      : Date.now();
  }

  function localizedArchiveName(archive) {
    if (typeof archive.name === 'string') return archive.name;
    return archive.name?.['zh-CN'] || archive.name?.['en-US'] || archive.id;
  }

  function browserDeviceName() {
    const platform = navigator.userAgentData?.platform || navigator.platform || 'Browser';
    return `${platform} 浏览器`;
  }

  function formatBytes(bytes) {
    let value = Number(bytes) || 0;
    const units = ['B', 'KiB', 'MiB', 'GiB'];
    let index = 0;
    while (value >= 1024 && index < units.length - 1) { value /= 1024; index++; }
    return `${value.toFixed(index ? 1 : 0)} ${units[index]}`;
  }

  function setStatus(text) {
    if (elements.status) elements.status.textContent = text;
  }

  function showToast(text) {
    if (!elements.toast) return;
    elements.toast.textContent = text;
    elements.toast.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { elements.toast.hidden = true; }, 6500);
  }

  function readSetting(key, fallback) {
    try { return localStorage.getItem(`gpxview.web.${key}`) ?? fallback; } catch { return fallback; }
  }

  function hasSetting(key) {
    try { return localStorage.getItem(`gpxview.web.${key}`) !== null; } catch { return false; }
  }

  function writeSetting(key, value) {
    try { localStorage.setItem(`gpxview.web.${key}`, value); } catch { /* Settings are optional. */ }
  }

  function readJsonSetting(key, fallback) {
    try {
      const value = localStorage.getItem(`gpxview.web.${key}`);
      return value === null ? fallback : JSON.parse(value);
    } catch { return fallback; }
  }

  function writeJsonSetting(key, value) {
    try { localStorage.setItem(`gpxview.web.${key}`, JSON.stringify(value)); } catch { /* Settings are optional. */ }
  }
})(globalThis);
