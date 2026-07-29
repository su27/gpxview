# GpxView Web Host Protocol

This document defines the boundary between the shared MapLibre UI and a native desktop host. The current Windows host uses WPF and WebView2; a future macOS host can implement the same contract with AppKit and WKWebView.

## Host bridge

Shared Web code communicates only through `globalThis.gpxHost`:

```javascript
gpxHost.send({type: 'openFile'});

const unsubscribe = gpxHost.onMessage(message => {
  // Handle native-to-Web messages.
});
```

The interface contains:

| Member | Meaning |
| --- | --- |
| `protocolVersion` | Current protocol version. Version 1 is implemented. |
| `platform` | Diagnostic host name such as `windows-webview2` or `browser`. |
| `available` | Whether a native host transport is connected. |
| `send(message)` | Sends a JSON-compatible value to the native host and returns whether it was delivered. |
| `onMessage(listener)` | Subscribes to native messages and returns an unsubscribe function. |

`Web/host.js` supplies the WebView2 adapter and a safe browser fallback. Another native host may inject an object with the same interface before `host.js` runs; the script preserves an existing implementation.

Messages use lower-camel-case JSON properties. Payloads must contain JSON-compatible values only. File-system paths never form part of native-to-Web track or road-network identifiers, except for the current recent-file command described below.

## Handshake

After registering its native-message listener, the Web UI sends:

```json
{
  "type": "ready",
  "protocolVersion": 1
}
```

The Windows host also accepts the legacy string message `"ready"` so that an older cached page cannot leave the application uninitialized. A host must not send state until it receives a supported ready message.

Version 1 currently initializes the page with a sequence of state messages rather than one combined `initialize` message. Consolidating that bootstrap sequence is the next protocol refactor.

## Web-to-native messages

| Type | Fields | Purpose |
| --- | --- | --- |
| `ready` | `protocolVersion` | Announces that the Web UI can receive state. |
| `openFile` | none | Requests a native multi-file picker. This name is retained for version 1. |
| `openRecentTrack` | `path` | Opens a cached recent track. The raw path is a Windows-era coupling that must become an opaque document ID before adding a sandboxed host. |
| `selectTrack` | `id` | Selects the track whose summary, chart, and playback should be active. |
| `setTrackVisibility` | `id`, `visible` | Shows or hides one track on the map. |
| `closeTrack` | `id` | Removes a track from the current session. |
| `setLanguage` | `language` | Selects `system`, `zh-CN`, or `en-US`. |
| `setGeocodingEnabled` | `enabled` | Records the user's place-recognition choice. |
| `closeSettings` | none | Notifies the native toolbar that the settings panel closed. |
| `openDefaultAppsSettings` | none | Opens Windows default-app settings. This will be renamed to the platform-neutral `manageFileAssociations` in a later protocol version. |
| `openRoadNetworkFolder` | none | Requests the platform action for the local road-network location. |
| `refreshRoadNetworks` | none | Rescans local PMTiles archives. |
| `openProjectHome` | none | Opens the project home page with the platform browser. |
| `terrainState` | `enabled`, optional `error` | Reports the terrain state after asynchronous WebGL setup. |
| `mapError` | `error` | Reports a user-visible map-layer failure. |

The native host validates every field and ignores malformed or unsupported messages.

## Native-to-Web messages

| Type | Principal fields | Purpose |
| --- | --- | --- |
| `setLocalization` | `locale`, `strings` | Replaces all localized Web strings. |
| `setTheme` | `theme` | Applies `light` or `dark` presentation. |
| `setMapStyle` | `mapStyle` | Selects the active base map by its stable ID. |
| `setTerrainEnabled` | `enabled` | Requests 2D or 3D terrain. |
| `setTracks` | `tracks`, `activeTrackId`, `fit` | Replaces the open-track collection and optionally fits the map. |
| `setActiveTrack` | `id` | Selects an already loaded track. |
| `setTrackVisibility` | `id`, `visible` | Updates one track after a native state change. |
| `removeTrack` | `id`, `activeTrackId` | Removes one track and selects its successor. |
| `setPlaceName` | `placeName` | Updates the active track's recognized place. |
| `setRecentTracks` | `entries` | Replaces the recent-track cache shown by the Web UI. |
| `setRecentPanelVisible` | `visible` | Opens or closes the recent-track panel. |
| `setSettings` | settings and build metadata | Updates language, place recognition, channel, version, and discovered road archives. |
| `setSettingsPanelVisible` | `visible` | Opens or closes the settings panel. |
| `setRoadNetworkConfig` | `config` | Replaces archive URLs, bounds, zoom ranges, availability, and enabled state. |
| `setRoadNetworkEnabled` | `enabled` | Shows or hides all configured road-network layers. |

Track payloads contain stable IDs, display names, colors, visibility, line segments, a downsampled profile, and formatted summary values. Coordinates are WGS84 longitude/latitude pairs. The Windows host currently limits map geometry to about 30,000 points and profiles to about 8,000 points before crossing the bridge.

Road-network payloads expose opaque archive IDs and readable URLs. The shared Web UI must not derive local paths. Windows currently serves HTTP Range responses through WebView2; a macOS host may provide a custom-scheme PMTiles source without changing archive identity or map behavior.

## Temporary bootstrap globals

Before the Web page loads, the Windows host currently injects:

```javascript
window.gpxViewMapServices
window.gpxViewRoadNetwork
```

They are intentionally outside the long-term host contract. A later refactor should replace them with an `initialize` message sent after `ready`, then delay MapLibre construction until initialization arrives. Keeping this change separate avoids altering map startup behavior during the first bridge extraction.

## Portability rules

- Shared Web code must not access `chrome.webview`, `window.webkit`, Windows paths, or macOS bookmarks directly.
- Host commands describe user intent; platform-specific actions stay in native code.
- Native hosts validate all Web messages even though the page is bundled locally.
- The Web UI receives opaque track, document, and archive IDs instead of assuming a reusable file path.
- Large binary PMTiles ranges stay outside the JSON message channel.
- Secrets and local paths must not be logged or included in map attribution.

## Refactor checkpoints

The boundary is ready for another native host when:

1. `chrome.webview` appears only in the WebView2 adapter.
2. The page can run with a browser mock implementing `gpxHost`.
3. Bootstrap globals have been replaced by a versioned `initialize` message.
4. Recent tracks use opaque document references instead of raw paths.
5. Windows-specific commands have platform-neutral names or declared capabilities.
