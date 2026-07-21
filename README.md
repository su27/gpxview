# GpxView

Windows 下轻量、快速的 GPX、KML、KMZ、FIT 轨迹查看器。

## 界面预览

<img width="2477" height="1425" alt="ScreenShot_2026-07-21_153337_294" src="https://github.com/user-attachments/assets/946e107d-44a2-4021-a4ae-a349aac71a0e" />
<img width="2477" height="1425" alt="ScreenShot_2026-07-21_153504_396" src="https://github.com/user-attachments/assets/57f66c59-8a37-4018-b772-61eb75af2398" />
<img width="2477" height="1425" alt="ScreenShot_2026-07-21_153642_400" src="https://github.com/user-attachments/assets/639993b1-672c-4de6-988c-b5ae5b29274a" />


## 功能

- 打开、拖放或从 Windows 文件资源管理器启动 `.gpx`、`.kml`、`.kmz`、`.fit`
- 默认使用 OpenFreeMap 现代矢量地图，并可切换免 Key 户外矢量地图、OSM 经典、天地图街道/影像/地形、Esri 卫星、OpenTopoMap、OSM 人道主义
- 可一键切换 2D/3D，使用在线 DEM 将底图和轨迹贴合到真实地形，并叠加海拔分层设色、山体阴影与随主题变化的地平线雾化
- 地图铺满内容区，摘要和图表以半透明背景模糊层覆盖在地图上，可一键收起
- 状态栏持续显示文件格式、距离、累计爬升、总用时和轨迹点数
- 城市级轨迹地名识别，并在当前摘要和最近轨迹中显示
- 最近轨迹面板缓存文件名、统计、地名、轨迹缩略图和海拔缩略图，启动时无需重新解析原文件
- 单图标切换跟随系统、浅色和深色主题，WPF、地图覆盖层与图表同步
- 轨迹可按统一颜色、海拔、坡度、速度、心率或功率动态着色
- 可按原始时间戳或距离回放轨迹，支持变速、暂停、拖动定位、地图跟随和海拔图同步
- 海拔作为基础曲线，可按数据存在情况叠加心率、速度和功率；悬停数值与地图位置联动
- 地图提供米制比例尺；3D 开启时可在鼠标位置读取 DEM 地面海拔
- 距离、时长、移动时间、爬升、速度、心率、踏频、功率统计
- Garmin FIT GPS 与常用运动记录字段
- WGS84 默认显示，可手动纠正实际保存为 GCJ-02 或 BD-09 的非标准源文件
- 大轨迹在发送到地图和图表前自动抽样
- 多尺寸 Windows 应用图标、开始菜单入口、卸载支持和四类文件关联

键盘与鼠标操作：`Ctrl+O` 打开文件；`F11` 进入或退出全屏；全屏时也可按 `Esc` 返回；在地图上按住 `Shift` 并用鼠标左键拖动，可水平旋转地图，3D 状态下还可垂直调整倾斜角度；点击地图右上角的指南针可回到正北方向。

## 安装

构建出的 64 位 MSI 位于：

```text
artifacts\installer\GpxView-0.1.4-win-x64.msi
```

安装器将 GpxView 安装到 Program Files，创建开始菜单入口，并向 Windows 注册 GPX、KML、KMZ、FIT 的打开方式和“默认应用”能力；不创建桌面快捷方式。安装包包含 .NET 10 桌面运行时，终端用户无需另行安装 .NET。现代 Windows 通常已包含 Microsoft Edge WebView2 Runtime；若该组件缺失，应用会显示修复提示。

## 技术栈

- .NET 10 LTS / C# / WPF Fluent theme
- Microsoft WebView2
- 随应用本地分发的 MapLibre GL JS 5.6.2
- 随应用本地分发的 maplibre-contour 0.1.0，在 Web Worker 中从 DEM 生成矢量等高线
- OpenFreeMap/OpenMapTiles 矢量地图，Mapterhorn DEM，以及 OSM 系、天地图与 Esri 栅格地图服务
- Garmin.FIT.Sdk 21.205.0
- WiX Toolset SDK 5.0.2
- xUnit

## 开发构建

```powershell
dotnet restore
dotnet build GpxView.sln
dotnet test GpxView.sln
dotnet run --project src\GpxView.App\GpxView.App.csproj
```

仓库通过 `global.json` 固定使用 .NET SDK 10.0.302。MapLibre 与 maplibre-contour 的 JS、CSS 和许可证固定保存在 `src\GpxView.App\Web\vendor` 对应版本目录，应用运行时不依赖 unpkg 等前端 CDN。maplibre-contour 使用 BSD 3-Clause 许可证。

## 地图服务配置

复制示例配置并按需填写在天地图服务中心申请的浏览器端 Key 和安全密钥：

```powershell
Copy-Item src\GpxView.App\MapServices.example.json src\GpxView.App\MapServices.local.json
```

配置格式：

```json
{
  "tianditu": {
    "tk": "YOUR_BROWSER_KEY",
    "sk": "YOUR_SECURITY_KEY"
  },
  "geocoding": {
    "enabled": true,
    "endpoint": "https://nominatim.openstreetmap.org/reverse"
  }
}
```

`MapServices.local.json` 已被 Git 忽略，并会复制到开发输出和自包含发布目录。缺少有效的 `tk` 或 `sk` 时，三个天地图选项会自动禁用。

天地图官方建议安全密钥仅由自有代理服务器追加。当前桌面版本没有代理，在开启安全密钥后必须把 `tk` 与 `sk` 一起发送给 WMTS，因此该配置虽然不会进入 Git，仍会包含在发布目录和 MSI 中，也能被终端用户提取。当前方式只适合自用和小范围测试；公开分发前应改为用户自行配置或由受控代理转发。

## 户外矢量地图

“户外”底图不需要 API Key。它以 OpenFreeMap Bright 矢量数据为基础，使用自然地貌色系，强化森林、水系、步道与山峰，并淡化面向机动车导航的道路层级。

地形数据来自 Mapterhorn Terrarium DEM。应用通过随包分发的 maplibre-contour 在 Web Worker 中生成米制矢量等高线：低缩放级别使用较疏的等高距，进入山地细节后逐步细化到 20 米普通等高线与 100 米主等高线。2D 户外底图会显示海拔分层设色、单方向山体阴影、等高线和高度标注；切换 3D 后复用同一份 DEM 与缓存，不重复下载地形瓦片。

Mapterhorn 当前提供到 12 级的原生 DEM，应用在更高层级使用过缩放，并从 8 级开始请求等高线，以减少无效请求和不必要的计算。等高线加载失败时仍保留 OpenFreeMap 户外矢量底图，并在状态栏显示降级提示。

## 最近轨迹与地名

应用最多保存 20 条最近轨迹到 `%LOCALAPPDATA%\GpxView\recent-tracks.json`。缓存包含原文件路径、格式、距离、爬升、地名及经过归一化抽样的轨迹和海拔缩略数据；打开最近面板不会重新读取轨迹文件。点击记录时才检查原文件，文件不存在会提示并从历史中删除。

首次打开一个尚未缓存地名的新位置时，应用会把轨迹中最长分段的中间点以 WGS84 坐标发送给 OpenStreetMap Foundation 的 Nominatim 反向地理编码服务，并请求城市级结果。结果会持久化复用；请求使用明确的 GpxView User-Agent、全应用串行且间隔不少于 1.1 秒，不执行批量或周期查询。界面显示 OpenStreetMap/Nominatim 署名。

这意味着轨迹的一个代表坐标会发送给 OSMF。若不希望发送，可在 `MapServices.local.json` 中把 `geocoding.enabled` 设为 `false`；也可以修改 `geocoding.endpoint` 切换到自建或其他兼容的 Nominatim 服务，无需修改程序代码。公共 Nominatim 容量有限，当前实现适合低频桌面使用；大规模分发应使用受控代理、自建实例或有容量保障的服务。

## 发布与安装器构建

先生成不裁剪的 64 位自包含发布目录，再构建 MSI：

```powershell
dotnet publish src\GpxView.App\GpxView.App.csproj -p:PublishProfile=win-x64
dotnet build installer\GpxView.Installer.wixproj -c Release
```

输出目录：

```text
artifacts\publish\win-x64\
artifacts\installer\GpxView-0.1.4-win-x64.msi
```

应用图标如需从可编辑源重新生成：

```powershell
dotnet run --project tools\GpxView.IconGenerator\GpxView.IconGenerator.csproj --configuration Release -- src\GpxView.App\Assets
```

## 坐标约定

GPX、KML 和 FIT 按规范默认视为 WGS84，直接显示在 OpenFreeMap、OSM 和天地图球面墨卡托图层上，不做火星坐标偏移。如果某个第三方文件实际写入了 GCJ-02 或 BD-09，可在窗口顶部选择对应“源坐标”，应用只在内存中纠正为 WGS84，不修改原文件。

## 地图服务

- OpenFreeMap 现代：默认使用 Liberty 矢量样式，基于 OpenMapTiles 与 OpenStreetMap 数据，无需 API Key。
- OpenFreeMap 户外：以 Bright 矢量样式为基础，叠加 Mapterhorn 地形和本地动态等高线；详见“户外矢量地图”。
- OSM 经典：OpenStreetMap Foundation 公共栅格瓦片服务。
- 天地图街道：`vec_w` 底图叠加 `cva_w` 中文注记。
- 天地图影像：`img_w` 影像叠加 `cia_w` 中文注记。
- 天地图地形：`ter_w` 地形晕渲叠加 `cta_w` 中文注记。
- Esri 卫星：Esri World Imagery。
- OpenTopoMap：OSM 数据与 SRTM 地形风格。
- OSM 人道主义：OSM France 托管的 HOT 风格瓦片。
- 地形与等高线：Mapterhorn Terrarium DEM；开启 3D 或选择户外底图时按视野请求，二者共享 DEM 缓存，切换底图后会自动恢复三维状态。

应用会在地图上显示各服务要求的署名。OpenFreeMap 和 Mapterhorn 公共实例无需 Key，但均按原样提供、没有 SLA，并可能停止或变更服务；需要稳定保障时应考虑自托管。天地图服务需要有效 Key，并受其服务条款和调用额度约束；Esri 影像受 Esri 及其数据提供方条款约束；OSMF、OpenTopoMap 和 OSM France 的公共瓦片也不是无限制 CDN。不得批量下载或离线预取，公开大规模分发前应分别核对服务方的最新政策并选择有明确授权和容量保障的服务。
