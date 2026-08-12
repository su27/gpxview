import { Decoder, Stream } from './vendor/garmin-fitsdk/21.213.0/src/index.js';

const degreesPerSemicircle = 180 / 2147483648;

self.addEventListener('message', event => {
  const id = event.data?.id;
  try {
    const stream = Stream.fromArrayBuffer(event.data?.buffer);
    if (!Decoder.isFIT(stream)) throw new Error('不是有效的 FIT 文件');

    const decoder = new Decoder(stream);
    const { messages, errors } = decoder.read({
      applyScaleAndOffset: true,
      expandSubFields: true,
      expandComponents: true,
      convertTypesToStrings: true,
      convertDateTimesToDates: true,
      includeUnknownData: false,
      mergeHeartRates: true,
      decodeMemoGlobs: false
    });
    if (errors?.length) throw new Error(`FIT 文件解析失败：${String(errors[0]?.message || errors[0])}`);

    const points = (messages?.recordMesgs || []).map(record => {
      const latitude = finiteNumber(record.positionLat);
      const longitude = finiteNumber(record.positionLong);
      if (latitude === null || longitude === null) return null;
      return {
        latitude: latitude * degreesPerSemicircle,
        longitude: longitude * degreesPerSemicircle,
        elevationMeters: firstFinite(record.enhancedAltitude, record.altitude),
        timestamp: timestampMilliseconds(record.timestamp),
        speedMetersPerSecond: firstFinite(record.enhancedSpeed, record.speed),
        heartRateBpm: roundedOrNull(record.heartRate),
        cadenceRpm: roundedOrNull(record.cadence),
        powerWatts: finiteNumber(record.power)
      };
    }).filter(point => point && validCoordinate(point.latitude, point.longitude));

    self.postMessage({ id, points });
  } catch (error) {
    self.postMessage({ id, error: error?.message || '无法解析 FIT 文件' });
  }
});

function finiteNumber(value) {
  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}

function firstFinite(...values) {
  for (const value of values) {
    const number = finiteNumber(value);
    if (number !== null) return number;
  }
  return null;
}

function roundedOrNull(value) {
  const number = finiteNumber(value);
  return number === null ? null : Math.round(number);
}

function timestampMilliseconds(value) {
  if (value instanceof Date) return Number.isFinite(value.getTime()) ? value.getTime() : null;
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) ? timestamp : null;
}

function validCoordinate(latitude, longitude) {
  return latitude >= -90 && latitude <= 90 && longitude >= -180 && longitude <= 180;
}
