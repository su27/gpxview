(function initializeGpxHost(global) {
  'use strict';

  // Native hosts may inject the same interface before this script runs.
  if (global.gpxHost) return;

  const listeners = new Set();
  const webView = global.chrome?.webview ?? null;
  let browserHandler = null;
  const pendingBrowserMessages = [];

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
      if (webView) {
        webView.postMessage(message);
        return true;
      }
      if (!browserHandler) {
        pendingBrowserMessages.push(message);
        return false;
      }
      browserHandler(message);
      return true;
    },
    attachBrowser(handler) {
      if (webView) return () => {};
      if (typeof handler !== 'function') throw new TypeError('The browser host handler must be a function.');
      browserHandler = handler;
      while (pendingBrowserMessages.length) browserHandler(pendingBrowserMessages.shift());
      return message => {
        for (const listener of listeners) listener(message);
      };
    },

    onMessage(listener) {
      if (typeof listener !== 'function') throw new TypeError('Host message listener must be a function.');
      listeners.add(listener);
      return () => listeners.delete(listener);
    }
  });
})(globalThis);
