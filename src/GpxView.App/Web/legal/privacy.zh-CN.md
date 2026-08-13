# GpxView 隐私政策

更新日期：2026-08-13

GpxView 是一个本地运行的 Windows 轨迹查看器。它不要求账号，不包含广告、分析 SDK 或用户行为遥测，也不会把完整轨迹文件上传到 GpxView 自有服务器。

## 本地处理和保存的数据

- 你主动打开的 GPX、KML、KMZ 和 FIT 文件仅在本机解析。
- 最近轨迹记录保存在 `%LOCALAPPDATA%\GpxView\recent-tracks.json`，其中包含文件路径、统计数据、地点名称，以及为快速预览而抽样的路线和海拔数据。
- 应用设置保存在 `%LOCALAPPDATA%\GpxView\settings.json`。
- 你放入 `%LOCALAPPDATA%\GpxView\RoadNetwork` 的 PMTiles 路网文件仅在本机读取。
- WebView2 缓存保存在 `%LOCALAPPDATA%\GpxView\WebView2`。

你可以关闭应用后删除 `%LOCALAPPDATA%\GpxView` 来清除以上本地数据。删除最近记录或缓存不会删除原始轨迹文件。

## 在线地图服务

显示在线底图、地形和等高线时，应用会向当前选择的地图服务请求视野所需的瓦片。服务提供方会收到常规网络请求信息，例如 IP 地址、User-Agent 和所请求的瓦片坐标。不同底图可能使用 OpenFreeMap、OpenStreetMap、Mapterhorn、Esri、OpenTopoMap 或 OSM France。GitHub 版本还可以在用户自行配置密钥后使用天地图；Microsoft Store 版本不包含天地图功能或密钥。

GpxView 不会为广告、用户画像或分析目的使用这些请求。

## 可选的地点识别

地点识别默认关闭。只有在你明确开启后，GpxView 才会把轨迹最长分段的中间点作为一个代表坐标发送给 OpenStreetMap Foundation 的 Nominatim 服务。请求坐标保留约 3 位小数，约为百米级，并用于返回城市、区县或附近地点名称。此功能不读取设备当前位置，也不使用 Windows 定位服务。

地点识别请求不会包含完整轨迹、轨迹文件、文件名、心率、功率或其他运动传感器数据。Nominatim 仍会收到常规网络请求信息，包括 IP 地址。识别结果会保存到本机最近轨迹缓存中，避免重复请求。

你可以随时在“设置 → 地点识别”中关闭该功能。关闭后不会再发起新的地点识别请求；已经保存在本机的地点名称仍可显示。

Nominatim 隐私政策：https://osmfoundation.org/wiki/Privacy_Policy

## 网页版当前位置

只有当你点击网页版的“当前位置”按钮并在浏览器中授权后，GpxView 才会读取浏览器提供的当前位置和精度。位置只保留在当前页面内存中，用于显示定位点和移动地图视野，不会写入轨迹文件、本地存储或发送到 GpxView 路网服务。地图移动到该区域后，所选地图服务会照常收到该视野所需的瓦片请求。

GpxView 不会在后台持续跟踪位置。你可以通过浏览器的站点设置随时撤销定位权限。

## 数据共享与保留

除上述按需使用的地图服务和可选地点识别外，GpxView 不向第三方出售、出租或共享轨迹数据。GpxView 没有自有云端账号或服务器，因此不会在云端保留轨迹数据。

## 联系与变更

项目与问题反馈：https://github.com/su27/gpxview

如果功能或数据处理方式发生变化，本政策会同步更新，并修改顶部的更新日期。
