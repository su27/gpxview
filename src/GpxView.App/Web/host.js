(function initializeGpxHost(global) {
  'use strict';

  // Native hosts may inject the same interface before this script runs.
  if (global.gpxHost) return;

  const listeners = new Set();
  const webView = global.chrome?.webview ?? null;

  if (webView) {
    webView.addEventListener('message', event => {
      for (const listener of listeners) listener(event.data);
    });
  }

  global.gpxHost = Object.freeze({
    protocolVersion: 1,
    platform: webView ? 'windows-webview2' : 'browser',
    available: Boolean(webView),

    send(message) {
      if (!webView) return false;
      webView.postMessage(message);
      return true;
    },

    onMessage(listener) {
      if (typeof listener !== 'function') throw new TypeError('Host message listener must be a function.');
      listeners.add(listener);
      return () => listeners.delete(listener);
    }
  });
})(globalThis);
