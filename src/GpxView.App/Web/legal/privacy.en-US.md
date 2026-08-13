# GpxView Privacy Policy

Last updated: August 13, 2026

GpxView is a local Windows track viewer. It does not require an account, contain advertising or analytics SDKs, or upload complete track files to a GpxView-operated server.

## Data processed and stored locally

- GPX, KML, KMZ, and FIT files that you choose are parsed locally.
- Recent-track data is stored in `%LOCALAPPDATA%\GpxView\recent-tracks.json`. It includes file paths, statistics, recognized place names, and sampled route and elevation data used for previews.
- App settings are stored in `%LOCALAPPDATA%\GpxView\settings.json`.
- PMTiles archives placed in `%LOCALAPPDATA%\GpxView\RoadNetwork` are read locally.
- WebView2 cache data is stored in `%LOCALAPPDATA%\GpxView\WebView2`.

You can close GpxView and delete `%LOCALAPPDATA%\GpxView` to remove this local data. Removing history or cache data does not delete the original track files.

## Online map services

To display online maps, terrain, and contours, GpxView requests the tiles needed for the current map view. Providers receive normal network request information such as the IP address, User-Agent, and requested tile coordinates. Depending on the selected map, providers may include OpenFreeMap, OpenStreetMap, Mapterhorn, Esri, OpenTopoMap, or OSM France. The GitHub build can also use Tianditu after the user supplies credentials; the Microsoft Store build contains neither Tianditu functionality nor credentials.

GpxView does not use these requests for advertising, profiling, or analytics.

## Optional place recognition

Place recognition is off by default. Only after you explicitly enable it does GpxView send one representative coordinate—the midpoint of the longest track segment—to the OpenStreetMap Foundation Nominatim service. The coordinate is rounded to approximately three decimal places, or roughly 100-meter precision, and is used to return a city, district, or nearby place name. This feature does not read the device's current location or use Windows location services.

The request does not include the complete route, track file, filename, heart rate, power, or other sensor data. Nominatim still receives normal network request information, including the IP address. Results are stored in the local recent-track cache to avoid repeated requests.

You can turn this feature off at any time under Settings → Place recognition. Once disabled, no new recognition requests are made. Place names already cached locally can still be displayed.

Nominatim privacy policy: https://osmfoundation.org/wiki/Privacy_Policy

## Current location on the web version

GpxView reads the current location and accuracy supplied by your browser only after you click the Current location button and grant browser permission. The position remains in the current page's memory and is used only to display a location dot and move the map. It is not written to a track file or local storage and is not sent to the GpxView road-network service. After the map moves to that area, the selected map provider receives the tile requests needed for the visible map as usual.

GpxView does not continuously track your location in the background. You can revoke the permission at any time in your browser's site settings.

## Sharing and retention

Except for the on-demand map services and optional place recognition described above, GpxView does not sell, rent, or share track data with third parties. GpxView has no account system or operated cloud service and therefore retains no track data in the cloud.

## Contact and changes

Project and issue tracker: https://github.com/su27/gpxview

If app functionality or data handling changes, this policy will be updated and the date at the top will be revised.
