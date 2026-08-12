(function initializeBrowserTrackParser(global) {
  'use strict';

  const maximumFileBytes = 64 * 1024 * 1024;
  const maximumMapPoints = 30000;
  const maximumProfilePoints = 8000;
  const earthRadiusMeters = 6371008.8;
  const fitRequests = new Map();
  let fitWorker = null;
  let fitRequestId = 0;

  function localElements(root, name) {
    return Array.from(root?.getElementsByTagName?.('*') || []).filter(element => element.localName === name);
  }

  function childElements(root, name) {
    return Array.from(root?.children || []).filter(element => element.localName === name);
  }

  function firstText(root, names, descendants = false) {
    for (const name of names) {
      const element = descendants ? localElements(root, name)[0] : childElements(root, name)[0];
      const value = element?.textContent?.trim();
      if (value) return value;
    }
    return '';
  }

  function numberOrNull(value) {
    if (value === '' || value === null || value === undefined) return null;
    const number = Number(value);
    return Number.isFinite(number) ? number : null;
  }

  function integerOrNull(value) {
    const number = numberOrNull(value);
    return number === null ? null : Math.round(number);
  }

  function parseTimestamp(value) {
    if (!value) return null;
    const timestamp = Date.parse(value);
    return Number.isFinite(timestamp) ? timestamp : null;
  }

  function parseXml(text) {
    const document = new DOMParser().parseFromString(text, 'application/xml');
    if (localElements(document, 'parsererror').length) throw new Error('XML 文件格式无效');
    return document;
  }

  function baseName(fileName) {
    return String(fileName || '未命名轨迹').replace(/\.[^.]+$/, '');
  }

  function parseGpxPoint(element, sourceCoordinateSystem) {
    const latitude = numberOrNull(element.getAttribute('lat'));
    const longitude = numberOrNull(element.getAttribute('lon'));
    if (!validCoordinate(latitude, longitude)) return null;
    const converted = convertCoordinate(latitude, longitude, sourceCoordinateSystem);
    return {
      latitude: converted.latitude,
      longitude: converted.longitude,
      elevationMeters: numberOrNull(firstText(element, ['ele'])),
      timestamp: parseTimestamp(firstText(element, ['time'])),
      speedMetersPerSecond: numberOrNull(firstText(element, ['speed'], true)),
      heartRateBpm: integerOrNull(firstText(element, ['hr', 'heartrate'], true)),
      cadenceRpm: integerOrNull(firstText(element, ['cad', 'cadence'], true)),
      powerWatts: numberOrNull(firstText(element, ['power', 'watts'], true))
    };
  }

  function parseGpxWaypoint(element, sourceCoordinateSystem) {
    const point = parseGpxPoint(element, sourceCoordinateSystem);
    if (!point) return null;
    return {
      latitude: point.latitude,
      longitude: point.longitude,
      elevationMeters: point.elevationMeters,
      name: firstText(element, ['name']),
      comment: firstText(element, ['cmt']),
      description: firstText(element, ['desc']),
      symbol: firstText(element, ['sym']),
      type: firstText(element, ['type'])
    };
  }

  function parseGpx(document, fileName, sourceCoordinateSystem) {
    const segments = [];
    for (const segmentElement of localElements(document, 'trkseg')) {
      const points = childElements(segmentElement, 'trkpt')
        .map(element => parseGpxPoint(element, sourceCoordinateSystem)).filter(Boolean);
      if (points.length) segments.push(points);
    }
    for (const routeElement of localElements(document, 'rte')) {
      const points = childElements(routeElement, 'rtept')
        .map(element => parseGpxPoint(element, sourceCoordinateSystem)).filter(Boolean);
      if (points.length) segments.push(points);
    }
    const waypoints = localElements(document, 'wpt')
      .map(element => parseGpxWaypoint(element, sourceCoordinateSystem)).filter(Boolean);
    const trackName = localElements(document, 'trk').map(element => firstText(element, ['name'])).find(Boolean);
    const metadataName = localElements(document, 'metadata').map(element => firstText(element, ['name'])).find(Boolean);
    return { name: trackName || metadataName || baseName(fileName), format: 'GPX', segments, waypoints };
  }

  function parseKmlCoordinate(value, sourceCoordinateSystem) {
    const parts = String(value || '').trim().split(/[\s,]+/).map(Number);
    if (parts.length < 2 || !validCoordinate(parts[1], parts[0])) return null;
    const converted = convertCoordinate(parts[1], parts[0], sourceCoordinateSystem);
    return {
      latitude: converted.latitude,
      longitude: converted.longitude,
      elevationMeters: Number.isFinite(parts[2]) ? parts[2] : null,
      timestamp: null,
      speedMetersPerSecond: null,
      heartRateBpm: null,
      cadenceRpm: null,
      powerWatts: null
    };
  }

  function parseKml(document, fileName, sourceCoordinateSystem, format) {
    const segments = [];
    const waypoints = [];
    for (const placemark of localElements(document, 'Placemark')) {
      for (const lineString of localElements(placemark, 'LineString')) {
        const coordinateText = firstText(lineString, ['coordinates'], true);
        const points = coordinateText.split(/\s+/)
          .map(value => parseKmlCoordinate(value, sourceCoordinateSystem)).filter(Boolean);
        if (points.length) segments.push(points);
      }
      for (const track of localElements(placemark, 'Track')) {
        const coordinates = localElements(track, 'coord');
        const timestamps = localElements(track, 'when');
        const points = coordinates.map((element, index) => {
          const point = parseKmlCoordinate(element.textContent, sourceCoordinateSystem);
          if (point) point.timestamp = parseTimestamp(timestamps[index]?.textContent?.trim());
          return point;
        }).filter(Boolean);
        if (points.length) segments.push(points);
      }
      const pointElement = localElements(placemark, 'Point')[0];
      if (pointElement) {
        const point = parseKmlCoordinate(firstText(pointElement, ['coordinates'], true), sourceCoordinateSystem);
        if (point) waypoints.push({
          latitude: point.latitude,
          longitude: point.longitude,
          elevationMeters: point.elevationMeters,
          name: firstText(placemark, ['name']),
          comment: '',
          description: firstText(placemark, ['description']),
          symbol: '',
          type: ''
        });
      }
    }
    const documentName = localElements(document, 'Document').map(element => firstText(element, ['name'])).find(Boolean);
    return { name: documentName || baseName(fileName), format, segments, waypoints };
  }

  async function readTrackSource(file) {
    if (file.size > maximumFileBytes) throw new Error('文件超过 64 MiB，网页版暂不读取');
    const extension = file.name.split('.').pop()?.toLowerCase();
    if (extension !== 'kmz') return { text: await file.text(), format: extension?.toUpperCase() || 'XML' };
    if (!global.fflate) throw new Error('KMZ 解压组件未加载');
    let selectedEntry = false;
    const archive = global.fflate.unzipSync(new Uint8Array(await file.arrayBuffer()), {
      filter(entry) {
        if (selectedEntry || !entry.name.toLowerCase().endsWith('.kml') || entry.originalSize > maximumFileBytes) return false;
        selectedEntry = true;
        return true;
      }
    });
    const entryName = Object.keys(archive).find(name => name.toLowerCase().endsWith('.kml'));
    if (!entryName) throw new Error('KMZ 中没有 KML 文档');
    const entry = archive[entryName];
    if (entry.byteLength > maximumFileBytes) throw new Error('KMZ 解压后的 KML 过大');
    return { text: global.fflate.strFromU8(entry), format: 'KMZ' };
  }

  function getFitWorker() {
    if (fitWorker) return fitWorker;
    fitWorker = new Worker(new URL('./browser-fit-worker.js', document.baseURI), {
      type: 'module',
      name: 'gpxview-fit-decoder'
    });
    fitWorker.addEventListener('message', event => {
      const request = fitRequests.get(event.data?.id);
      if (!request) return;
      fitRequests.delete(event.data.id);
      if (event.data.error) request.reject(new Error(event.data.error));
      else request.resolve(event.data.points || []);
    });
    fitWorker.addEventListener('error', event => {
      const error = new Error(event.message || 'FIT 解析组件加载失败');
      for (const request of fitRequests.values()) request.reject(error);
      fitRequests.clear();
      fitWorker?.terminate();
      fitWorker = null;
    });
    return fitWorker;
  }

  async function parseFit(file, sourceCoordinateSystem) {
    if (file.size > maximumFileBytes) throw new Error('文件超过 64 MiB，网页版暂不读取');
    const id = ++fitRequestId;
    const buffer = await file.arrayBuffer();
    const rawPoints = await new Promise((resolve, reject) => {
      fitRequests.set(id, { resolve, reject });
      getFitWorker().postMessage({ id, buffer }, [buffer]);
    });
    const points = rawPoints.map(point => {
      const converted = convertCoordinate(point.latitude, point.longitude, sourceCoordinateSystem);
      return { ...point, latitude: converted.latitude, longitude: converted.longitude };
    });
    return { name: baseName(file.name), format: 'FIT', segments: points.length ? [points] : [], waypoints: [] };
  }

  async function parseFile(file, options = {}) {
    const sourceCoordinateSystem = options.sourceCoordinateSystem || 'wgs84';
    let parsed;
    if (/\.fit$/i.test(file.name)) parsed = await parseFit(file, sourceCoordinateSystem);
    else {
      const source = await readTrackSource(file);
      const document = parseXml(source.text);
      const rootName = document.documentElement?.localName?.toLowerCase();
      if (rootName === 'gpx') parsed = parseGpx(document, file.name, sourceCoordinateSystem);
      else if (rootName === 'kml') parsed = parseKml(document, file.name, sourceCoordinateSystem, source.format === 'KMZ' ? 'KMZ' : 'KML');
      else throw new Error('仅支持 GPX、KML、KMZ 和 FIT 轨迹文件');
    }
    if (!parsed.segments.length && !parsed.waypoints.length) throw new Error('文件中没有可显示的轨迹点或标注点');
    return buildPayload(parsed, file.name, options);
  }

  function buildPayload(parsed, fileName, options) {
    const allPoints = parsed.segments.flat();
    const mapStride = Math.max(1, Math.ceil(allPoints.length / maximumMapPoints));
    const webSegments = parsed.segments.map(segment => {
      const coordinates = segment.filter((_, index) => index % mapStride === 0 || index === segment.length - 1)
        .map(point => [point.longitude, point.latitude]);
      return { coordinates };
    }).filter(segment => segment.coordinates.length);

    const firstTimestamp = allPoints.map(point => point.timestamp).find(Number.isFinite) ?? null;
    const profileCandidates = [];
    let distanceMeters = 0;
    let elevationGainMeters = 0;
    let elevationLossMeters = 0;
    let movingSeconds = 0;
    let maximumSpeedKmh = 0;
    let firstTrackTimestamp = null;
    let lastTrackTimestamp = null;

    parsed.segments.forEach((segment, segmentIndex) => {
      let previous = null;
      segment.forEach(point => {
        let speedMetersPerSecond = point.speedMetersPerSecond;
        if (Number.isFinite(point.timestamp)) {
          if (firstTrackTimestamp === null) firstTrackTimestamp = point.timestamp;
          lastTrackTimestamp = point.timestamp;
        }
        if (previous) {
          const stepDistance = distanceBetween(previous, point);
          distanceMeters += stepDistance;
          const seconds = Number.isFinite(previous.timestamp) && Number.isFinite(point.timestamp)
            ? (point.timestamp - previous.timestamp) / 1000 : null;
          if (!Number.isFinite(speedMetersPerSecond) && seconds > 0 && seconds <= 3600) speedMetersPerSecond = stepDistance / seconds;
          if (seconds > 0 && seconds <= 3600 && (speedMetersPerSecond || 0) >= 0.5) movingSeconds += seconds;
          if (Number.isFinite(previous.elevationMeters) && Number.isFinite(point.elevationMeters)) {
            const delta = point.elevationMeters - previous.elevationMeters;
            if (delta >= 1) elevationGainMeters += delta;
            else if (delta <= -1) elevationLossMeters += -delta;
          }
        }
        const speedKmh = Number.isFinite(speedMetersPerSecond) ? speedMetersPerSecond * 3.6 : null;
        if (Number.isFinite(speedKmh)) maximumSpeedKmh = Math.max(maximumSpeedKmh, speedKmh);
        profileCandidates.push({
          latitude: point.latitude,
          longitude: point.longitude,
          distanceKm: distanceMeters / 1000,
          elevationMeters: point.elevationMeters,
          speedKmh,
          heartRateBpm: point.heartRateBpm,
          cadenceRpm: point.cadenceRpm,
          powerWatts: point.powerWatts,
          segmentIndex,
          elapsedSeconds: firstTimestamp !== null && Number.isFinite(point.timestamp)
            ? Math.max(0, (point.timestamp - firstTimestamp) / 1000) : null
        });
        previous = point;
      });
    });

    const profileStride = Math.max(1, Math.ceil(profileCandidates.length / maximumProfilePoints));
    const profile = profileCandidates.filter((point, index) => index % profileStride === 0
      || index === profileCandidates.length - 1
      || index === 0
      || profileCandidates[index - 1]?.segmentIndex !== point.segmentIndex
      || profileCandidates[index + 1]?.segmentIndex !== point.segmentIndex);
    const elapsedSeconds = firstTrackTimestamp !== null && lastTrackTimestamp !== null && lastTrackTimestamp >= firstTrackTimestamp
      ? (lastTrackTimestamp - firstTrackTimestamp) / 1000 : null;
    const averageSpeedKmh = elapsedSeconds > 0 ? distanceMeters / elapsedSeconds * 3.6 : null;
    const heartRates = finiteValues(allPoints.map(point => point.heartRateBpm));
    const cadences = finiteValues(allPoints.map(point => point.cadenceRpm));
    const powers = finiteValues(allPoints.map(point => point.powerWatts));
    const waypointCount = parsed.waypoints.length;
    const formatLine = `${parsed.format} · ${parsed.segments.length} 分段 · ${allPoints.length.toLocaleString('zh-CN')} 轨迹点${waypointCount ? ` · ${waypointCount} 标注` : ''}`;
    return {
      id: options.id || crypto.randomUUID(),
      name: parsed.name,
      fileName,
      color: options.color || '#176bde',
      visible: options.visible !== false,
      placeName: null,
      showPlaceName: false,
      segments: webSegments,
      waypoints: parsed.waypoints,
      profile,
      summary: {
        formatLine,
        distance: distanceMeters >= 1000 ? `${(distanceMeters / 1000).toFixed(2)} km` : `${Math.round(distanceMeters)} m`,
        duration: elapsedSeconds > 0 ? `${formatDuration(elapsedSeconds)} / ${formatDuration(movingSeconds)}` : null,
        elevation: elevationGainMeters || elevationLossMeters ? `↑ ${Math.round(elevationGainMeters)} m / ↓ ${Math.round(elevationLossMeters)} m` : null,
        speed: averageSpeedKmh !== null ? `${averageSpeedKmh.toFixed(1)} / ${maximumSpeedKmh.toFixed(1)} km/h` : null,
        heartRate: heartRates.length ? `${Math.round(average(heartRates))} / ${Math.round(Math.max(...heartRates))} bpm` : null,
        cadencePower: sensorSummary(cadences, powers)
      }
    };
  }

  function finiteValues(values) {
    return values.filter(Number.isFinite);
  }

  function validCoordinate(latitude, longitude) {
    return Number.isFinite(latitude) && Number.isFinite(longitude)
      && latitude >= -90 && latitude <= 90 && longitude >= -180 && longitude <= 180;
  }

  function average(values) {
    return values.reduce((sum, value) => sum + value, 0) / values.length;
  }

  function sensorSummary(cadences, powers) {
    const parts = [];
    if (cadences.length) parts.push(`${Math.round(average(cadences))} rpm`);
    if (powers.length) parts.push(`${Math.round(average(powers))} / ${Math.round(Math.max(...powers))} W`);
    return parts.length ? parts.join(' · ') : null;
  }

  function formatDuration(seconds) {
    const total = Math.max(0, Math.round(seconds));
    const hours = Math.floor(total / 3600);
    const minutes = Math.floor(total % 3600 / 60);
    const remaining = total % 60;
    return hours ? `${hours}:${String(minutes).padStart(2, '0')}:${String(remaining).padStart(2, '0')}`
      : `${minutes}:${String(remaining).padStart(2, '0')}`;
  }

  function distanceBetween(first, second) {
    const latitude1 = first.latitude * Math.PI / 180;
    const latitude2 = second.latitude * Math.PI / 180;
    const deltaLatitude = latitude2 - latitude1;
    const deltaLongitude = (second.longitude - first.longitude) * Math.PI / 180;
    const a = Math.sin(deltaLatitude / 2) ** 2
      + Math.cos(latitude1) * Math.cos(latitude2) * Math.sin(deltaLongitude / 2) ** 2;
    return 2 * earthRadiusMeters * Math.asin(Math.min(1, Math.sqrt(a)));
  }

  function convertCoordinate(latitude, longitude, source) {
    if (source === 'gcj02') return gcj02ToWgs84(latitude, longitude);
    if (source === 'bd09') {
      const x = longitude - 0.0065;
      const y = latitude - 0.006;
      const z = Math.sqrt(x * x + y * y) - 0.00002 * Math.sin(y * Math.PI * 3000 / 180);
      const theta = Math.atan2(y, x) - 0.000003 * Math.cos(x * Math.PI * 3000 / 180);
      return gcj02ToWgs84(z * Math.sin(theta), z * Math.cos(theta));
    }
    return { latitude, longitude };
  }

  function gcj02ToWgs84(latitude, longitude) {
    if (latitude < 0.8293 || latitude > 55.8271 || longitude < 72.004 || longitude > 137.8347) return { latitude, longitude };
    let resultLatitude = latitude;
    let resultLongitude = longitude;
    for (let index = 0; index < 5; index++) {
      const projected = wgs84ToGcj02(resultLatitude, resultLongitude);
      resultLatitude -= projected.latitude - latitude;
      resultLongitude -= projected.longitude - longitude;
    }
    return { latitude: resultLatitude, longitude: resultLongitude };
  }

  function wgs84ToGcj02(latitude, longitude) {
    const a = 6378245;
    const eccentricitySquared = 0.006693421622965943;
    const x = longitude - 105;
    const y = latitude - 35;
    let deltaLatitude = -100 + 2 * x + 3 * y + 0.2 * y * y + 0.1 * x * y + 0.2 * Math.sqrt(Math.abs(x));
    deltaLatitude += (20 * Math.sin(6 * x * Math.PI) + 20 * Math.sin(2 * x * Math.PI)) * 2 / 3;
    deltaLatitude += (20 * Math.sin(y * Math.PI) + 40 * Math.sin(y / 3 * Math.PI)) * 2 / 3;
    deltaLatitude += (160 * Math.sin(y / 12 * Math.PI) + 320 * Math.sin(y * Math.PI / 30)) * 2 / 3;
    let deltaLongitude = 300 + x + 2 * y + 0.1 * x * x + 0.1 * x * y + 0.1 * Math.sqrt(Math.abs(x));
    deltaLongitude += (20 * Math.sin(6 * x * Math.PI) + 20 * Math.sin(2 * x * Math.PI)) * 2 / 3;
    deltaLongitude += (20 * Math.sin(x * Math.PI) + 40 * Math.sin(x / 3 * Math.PI)) * 2 / 3;
    deltaLongitude += (150 * Math.sin(x / 12 * Math.PI) + 300 * Math.sin(x / 30 * Math.PI)) * 2 / 3;
    const radianLatitude = latitude * Math.PI / 180;
    const magic = 1 - eccentricitySquared * Math.sin(radianLatitude) ** 2;
    const sqrtMagic = Math.sqrt(magic);
    deltaLatitude = deltaLatitude * 180 / ((a * (1 - eccentricitySquared)) / (magic * sqrtMagic) * Math.PI);
    deltaLongitude = deltaLongitude * 180 / (a / sqrtMagic * Math.cos(radianLatitude) * Math.PI);
    return { latitude: latitude + deltaLatitude, longitude: longitude + deltaLongitude };
  }

  global.gpxBrowserTrack = Object.freeze({ parseFile, convertCoordinate, distanceBetween });
})(globalThis);
