# GpxView

Windows 下轻量、快速的 GPX、KML、KMZ、FIT 轨迹查看器。

## 界面预览

<img width="2477" height="1425" alt="ScreenShot_2026-07-21_153337_294" src="https://github.com/user-attachments/assets/946e107d-44a2-4021-a4ae-a349aac71a0e" />
<img width="2477" height="1425" alt="ScreenShot_2026-07-21_153504_396" src="https://github.com/user-attachments/assets/57f66c59-8a37-4018-b772-61eb75af2398" />
<img width="2477" height="1425" alt="ScreenShot_2026-07-21_153642_400" src="https://github.com/user-attachments/assets/639993b1-672c-4de6-988c-b5ae5b29274a" />


## 功能

- 一次打开或拖放多个 `.gpx`、`.kml`、`.kmz`、`.fit`，拖到已有窗口会追加为多轨迹对比，也可从 Windows 文件资源管理器启动
- 已打开轨迹以地图顶部标签呈现，可独立显示、隐藏和关闭；不同轨迹使用不同颜色，当前标签对应轨迹在地图上置顶，并独占摘要、海拔图与回放控制
- 显示 GPX 标注点与 KML/KMZ 点地标；地图上保留名称标签，点击可查看说明、海拔和类型信息
- 默认使用 OpenFreeMap 现代矢量地图，并可切换免 Key 户外矢量地图、OSM 经典、Esri 卫星、OpenTopoMap、OSM 人道主义；GitHub 版还可由用户配置天地图
- 自动发现并加载多个本地或远程 PMTiles 历史轨迹密度图层，并独立于底图统一显示或隐藏；矢量底图下路网图层保持在底图之上、用户轨迹之下
- 可一键切换 2D/3D，使用在线 DEM 将底图和轨迹贴合到真实地形，并叠加海拔分层设色、山体阴影与随主题变化的地平线雾化
- 地图铺满内容区，摘要和图表以半透明背景模糊层覆盖在地图上，可一键收起
- 状态栏持续显示文件格式、距离、累计爬升、总用时和轨迹点数
- 可选的轨迹地点识别默认关闭，首次使用前明确询问；只发送轨迹文件中的一个低精度代表坐标，不读取设备当前位置
- 最近轨迹面板缓存文件名、统计、地名、轨迹缩略图和海拔缩略图，启动时无需重新解析原文件
- 中英文界面可跟随系统或手动切换；单图标切换跟随系统、浅色和深色主题，WPF、地图覆盖层与图表同步
- 设置面板集中管理语言、地点识别、Windows 文件关联、版本渠道、隐私政策和第三方许可；仅在检测到本地 PMTiles 或远程路网缓存/配置时显示路网相关设置
- 轨迹可按统一颜色、海拔、坡度、速度、心率或功率动态着色
- 可按原始时间戳或距离回放轨迹，支持变速、暂停、拖动定位、地图跟随和海拔图同步
- 海拔作为基础曲线，可按数据存在情况叠加心率、速度和功率；悬停数值与地图位置联动
- 地图提供米制比例尺；3D 开启时可在鼠标位置读取 DEM 地面海拔
- 距离、时长、移动时间、爬升、速度、心率、踏频、功率统计
- Garmin FIT GPS 与常用运动记录字段
- WGS84 默认显示，可手动纠正实际保存为 GCJ-02 或 BD-09 的非标准源文件
- 大轨迹在发送到地图和图表前自动抽样
- 多尺寸 Windows 应用图标、开始菜单入口和卸载支持；向 Windows 注册四类受支持文件，但不擅自修改默认打开程序

键盘与鼠标操作：`Ctrl+O` 打开文件；地图获得焦点时按 `Tab` 切换摘要和海拔面板；`F11` 进入或退出全屏；全屏时也可按 `Esc` 返回；在地图上按住 `Shift` 并用鼠标左键拖动，可水平旋转地图，3D 状态下还可垂直调整倾斜角度；点击地图右上角的指南针可回到正北方向。设置、最近文件等面板中的 `Tab` 仍用于正常的键盘焦点导航。

## 网页版

网页版部署在 <https://web.example.invalid/>。它与桌面应用复用同一套地图、轨迹、海拔图、标注点和多路网前端代码，支持在浏览器中打开或拖放多个 GPX、KML、KMZ、FIT 文件，并提供轨迹切换、显示隐藏、底图、3D 地形、源坐标纠正和北京/河北私有路网选择。轨迹文件完全在浏览器本地解析，不会上传；网页版暂不支持最近文件、Windows 文件关联和本地 PMTiles。

私有路网使用一次性激活码授权当前浏览器。长期设备凭证和短期访问凭证都只存入 `HttpOnly`、`SameSite=Strict` Cookie，不暴露给网页 JavaScript，也不写入 `localStorage`；访问 PMTiles 必须携带有效授权且只能按受限字节段读取。北京和河北归档使用各自稳定的 dataset ID，省份选择不依赖目录顺序。

## 安装

构建出的 64 位 MSI 位于：

```text
artifacts\installer\GpxView-0.2.7-win-x64.msi
```

安装器将 GpxView 安装到 Program Files，创建开始菜单入口，并向 Windows 注册 GPX、KML、KMZ、FIT 的可选打开方式和“默认应用”能力；它不会在安装时抢占现有默认程序，也不创建桌面快捷方式。GitHub 版可在应用设置中为这些格式设为 GpxView；若 Windows 已用受保护的默认应用记录锁定其他程序，设置面板会改为打开系统确认入口。安装包包含 .NET 10 桌面运行时，终端用户无需另行安装 .NET。现代 Windows 通常已包含 Microsoft Edge WebView2 Runtime；若该组件缺失，应用会显示修复提示。

## 技术栈

- .NET 10 LTS / C# / WPF Fluent theme
- Microsoft WebView2
- 随应用本地分发的 MapLibre GL JS 5.6.2
- 随应用本地分发的 maplibre-contour 0.1.0，在 Web Worker 中从 DEM 生成矢量等高线
- 随应用本地分发的 PMTiles JavaScript 4.4.1，通过 WebView2 Range 响应读取多个本地单文件瓦片包
- 随应用本地分发的 fflate 0.8.2，供网页版在浏览器中解压 KMZ
- Garmin FIT JavaScript SDK 21.213.0，供网页版在独立 Worker 中解析 FIT
- OpenFreeMap/OpenMapTiles 矢量地图，Mapterhorn DEM，以及 OSM 系、天地图与 Esri 栅格地图服务
- Garmin.FIT.Sdk 21.205.0，供桌面版解析 FIT
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

MapLibre 页面通过平台中立的 `gpxHost` 桥与原生宿主通信；消息方向、字段和现存的可移植性边界记录在 [`docs/HOST_PROTOCOL.md`](docs/HOST_PROTOCOL.md)。WebView2 专有调用只保留在桥接适配器中，为以后接入 WKWebView 等宿主预留边界。

## 地图服务配置

GitHub 版可复制示例配置，并按需填写在天地图服务中心申请的浏览器端 Key 和安全密钥：

```powershell
Copy-Item src\GpxView.App\MapServices.example.json src\GpxView.App\MapServices.local.json
```

配置格式：

```json
{
  "tianditu": {
    "tk": "YOUR_BROWSER_KEY",
    "sk": "YOUR_SECURITY_KEY"
  }
}
```

`MapServices.local.json` 只保存天地图凭据，已被 Git 忽略。Debug 构建会默认复制该文件，方便本机开发；Release 和公开 GitHub Release 默认不复制，避免把本机 Key 打进安装包。需要生成自用的带天地图凭据 Release 包时，可在 publish 时显式追加 `-p:IncludeLocalMapServices=true`。缺少有效的 `tk` 或 `sk` 时，三个天地图选项会自动禁用。Microsoft Store 渠道在编译时移除天地图入口、忽略该文件，并在运行时再次丢弃天地图配置，因此不会把本机 Key 带入商店包。

天地图官方建议安全密钥仅由自有代理服务器追加。当前桌面版本没有代理，在开启安全密钥后必须把 `tk` 与 `sk` 一起发送给 WMTS，因此该配置虽然不会进入 Git，仍会包含在发布目录和 MSI 中，也能被终端用户提取。当前方式只适合自用和小范围测试；公开分发前应改为用户自行配置或由受控代理转发。

## 户外矢量地图

“户外”底图不需要 API Key。它以 OpenFreeMap Bright 矢量数据为基础，使用自然地貌色系，强化森林、水系、步道与山峰，并淡化面向机动车导航的道路层级。

地形数据来自 Mapterhorn Terrarium DEM。应用通过随包分发的 maplibre-contour 在 Web Worker 中生成米制矢量等高线：低缩放级别使用较疏的等高距，进入山地细节后逐步细化到 20 米普通等高线与 100 米主等高线。2D 户外底图会显示海拔分层设色、单方向山体阴影、等高线和高度标注；切换 3D 后复用同一份 DEM 与缓存，不重复下载地形瓦片。

Mapterhorn 当前提供到 12 级的原生 DEM，应用在更高层级使用过缩放，并从 8 级开始请求等高线，以减少无效请求和不必要的计算。等高线加载失败时仍保留 OpenFreeMap 户外矢量底图，并在状态栏显示降级提示。

## 历史轨迹路网实验

仓库包含一个离线工具，可把用户持有的两步路历史路网省份包转换为透明栅格 PMTiles。默认参数仍选择门头沟中西部 `115.9–116.0E / 39.9–40.0N` 作为快速实验范围；也可以显式扩大边界和记录数上限，按缩放层分块、缓存并续算省级数据。

先安装工具依赖，再生成实验包：

```powershell
python -m pip install -r tools\roadnet\requirements.txt
python tools\roadnet\build_density_pmtiles.py `
  C:\Users\su27\Downloads\北京.zip `
  "$env:LOCALAPPDATA\GpxView\RoadNetwork\mentougou-density.pmtiles" `
  --bounds 115.9,39.9,116.0,40.0 `
  --preview artifacts\roadnet\mentougou-density-preview.png
```

转换器以不同的 `ORIGINALID` 作为通行次数，而不是直接累计 GPS 点，避免采样频率和 `PCOUNT` 放大热度；每个缩放层使用非零像素的 p99 和 `log1p` 映射热度。应用通过内部虚拟地址按 Range 请求读取最终归档，不监听本地端口。

转换器默认生成 lossless WebP，并设置 2 万条源记录保护上限；扩大范围时必须显式提高 `--max-records`。应用启动时会扫描 `%LOCALAPPDATA%\GpxView\RoadNetwork` 顶层的所有 `*.pmtiles`，跳过损坏或不支持的文件，并为每个有效归档建立独立 Range 地址和地图图层。每个远程归档使用稳定的 dataset ID 建立独立请求地址和缓存索引；同名本地归档只替代对应的远程 dataset，不会因北京、河北等省域边界框相交而屏蔽其他路网。备份文件可放入子目录，子目录不会被扫描。本地和远程都没有有效归档时，才不显示“路网”按钮。

当远程私有路网服务提供 PMTiles 归档时，应用仍通过内部 Range 地址读取瓦片，但会把成功返回的 `GET 206` 字节段缓存到 `%LOCALAPPDATA%\GpxView\RoadNetworkCache`。缓存键包含服务归档 ID 与 ETag，因此归档版本变化后不会复用旧数据。设置面板会显示远程路网缓存大小和数据块数量，并可一键清理该缓存目录；清理不会删除 `%LOCALAPPDATA%\GpxView\RoadNetwork` 中的本地 PMTiles 文件。Worker 也会把远程 Range 片段写入 edge platform Cache API，以减少多人或多次访问同一区域时的 R2 读取操作；该缓存是边缘数据中心本地缓存，最好通过自有域名或 edge platform route 提供服务。当前部署使用 `https://roadnet.example.invalid/`；已经连接旧 `example.invalid` 地址的设备会在目录验证成功后迁移设置和 Windows 凭据，迁移失败时保留旧凭据以便重试。

例如生成北京范围、最高到 z16 的归档：

```powershell
python tools\roadnet\build_density_pmtiles.py `
  C:\Users\su27\Downloads\北京.zip `
  "$env:LOCALAPPDATA\GpxView\RoadNetwork\beijing-density.pmtiles" `
  --bounds 115.5,39.5,117.5,41.0 `
  --maxzoom 16 `
  --max-records 300000 `
  --metatile-size 32 `
  --name "Beijing historical trajectory density (2017)" `
  --preview artifacts\roadnet\beijing-density-preview.png
```

河北生产归档使用同一套参数，网格总边界为 `113.5,36.0,119.5,42.5`：

```powershell
python tools\roadnet\build_density_pmtiles.py `
  C:\Users\su27\Downloads\河北.zip `
  "$env:LOCALAPPDATA\GpxView\RoadNetwork\hebei-density.pmtiles" `
  --bounds 113.5,36.0,119.5,42.5 `
  --maxzoom 16 `
  --max-records 300000 `
  --metatile-size 32 `
  --name "Hebei historical trajectory density (2017)" `
  --preview artifacts\roadnet\hebei-density-preview.png
```

## 最近轨迹与地名

应用最多保存 20 条最近轨迹到 `%LOCALAPPDATA%\GpxView\recent-tracks.json`。缓存包含原文件路径、格式、距离、爬升、地名及经过归一化抽样的轨迹和海拔缩略数据；打开最近面板不会重新读取轨迹文件。点击记录时才检查原文件，文件不存在会提示并从历史中删除。

地点识别默认关闭，首次运行会询问是否开启。只有用户明确允许后，应用才会把轨迹中最长分段的中间点以约 3 位小数的 WGS84 坐标发送给 OpenStreetMap Foundation 的 Nominatim 反向地理编码服务，并请求地区名称。它不读取设备当前位置、不使用 Windows 定位服务，也不上传文件或完整轨迹。结果会持久化复用；请求使用明确且随版本变化的 GpxView User-Agent、全应用串行且间隔不少于 1.1 秒，不执行批量或周期查询。界面显示 OpenStreetMap/Nominatim 署名。

这意味着轨迹文件中的一个代表坐标和常规网络请求信息会发送给 OSMF，因此仍属于需要说明的可选数据传输。用户可随时在设置中关闭；关闭后不会再发起新请求，已缓存在本机的地点名称仍可显示。公共 Nominatim 容量有限，当前实现适合低频桌面使用；大规模分发应使用受控代理、自建实例或有容量保障的服务。应用内置中英文[隐私政策](src/GpxView.App/Web/legal/privacy.zh-CN.md)和[第三方许可说明](src/GpxView.App/Web/legal/third-party-notices.md)。

## GitHub 与 Microsoft Store 渠道

两个渠道共享同一套代码、设置、国际化、轨迹和路网功能。唯一的功能差异由 `DistributionChannel` 构建属性控制：

- `GitHub`：允许显示天地图选项，并可复制本机 `MapServices.local.json`。
- `Store`：定义 `GPXVIEW_STORE`，不生成天地图选项、不复制本机配置，并在读取配置时再次清除天地图凭据。

分别生成两个不裁剪的 64 位自包含目录：

```powershell
dotnet publish src\GpxView.App\GpxView.App.csproj -p:PublishProfile=win-x64
dotnet publish src\GpxView.App\GpxView.App.csproj -p:PublishProfile=win-x64-store
```

```text
artifacts\publish\win-x64\
artifacts\publish\win-x64-store\
```

Store 发布目录是与商店包身份解耦的应用载荷。若 Partner Center 为该产品开放 Win32 MSI 提交流程，可构建只读取 Store 载荷的安装器：

```powershell
dotnet build installer\GpxView.Store.Installer.wixproj -c Release
```

输出为 `artifacts\installer\GpxView-0.2.7-store-win-x64.msi`。该项目会在构建时再次拒绝任何包含 `MapServices.local.json` 的 Store 载荷。

Microsoft Store 中已预留正式产品 `GpxView`，其公开包身份如下：

| 字段 | 值 |
| --- | --- |
| Store ID | `9NXNTF9Q29R2` |
| `Package/Identity/Name` | `SuDan.GpxView` |
| `Package/Identity/Publisher` | `CN=DBB8CB7C-AA92-4365-B28B-709FB95AB14B` |
| `PublisherDisplayName` | `Su Dan` |
| Package Family Name | `SuDan.GpxView_5de2zzw6ecnz2` |

正式 MSIX 清单位于 `installer/msix/Package.appxmanifest`。它将 GPX、KML、KMZ 和 FIT 注册为可打开的文件类型，但不会把 GpxView 强制设为系统默认应用；Store 版设置面板会提供这些格式的状态和 Windows 要求的确认入口。构建上传包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Build-StoreMsix.ps1
```

输出为 `artifacts\store\GpxView-<四段版本>-win-x64.msix`。脚本会重新发布 `win-x64-store` 载荷、使用 Partner Center 身份生成 MSIX、验证包内身份和架构，并拒绝任何包含 `MapServices.local.json` 的产物。Store 上传包按 Microsoft 的流程保持未签名；Microsoft Store 会在提交后签名。只有绕过商店进行本机侧载时，才需要另行创建并信任 Publisher 匹配的开发证书，证书和密码不得提交到仓库。详见 Microsoft 的 [Store 签名说明](https://learn.microsoft.com/windows/msix/package/sign-msix-package-guide#production-microsoft-store-distribution)。

`src\GpxView.App\Assets\Store\AppTileIcon300.png` 是商店列表建议上传的 300 x 300 应用图标；其余 Store 图标由清单引用并打入 MSIX。商店列表至少需要一张 1366 x 768 或更大的 PNG 桌面截图，README 中现有的三张界面图满足像素尺寸要求，但正式提交前仍应确认它们与当前版本一致。

产品公开地址为 <https://apps.microsoft.com/detail/9NXNTF9Q29R2>；在首次通过认证前，该页面可能不会向普通访客显示。

GitHub MSI 使用 GitHub 渠道的发布目录。公开 Release 默认不包含 `MapServices.local.json`：

```powershell
dotnet publish src\GpxView.App\GpxView.App.csproj -p:PublishProfile=win-x64
dotnet build installer\GpxView.Installer.wixproj -c Release
```

输出为 `artifacts\installer\GpxView-0.2.7-win-x64.msi`。

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
- 天地图街道（仅 GitHub 版）：`vec_w` 底图叠加 `cva_w` 中文注记。
- 天地图影像（仅 GitHub 版）：`img_w` 影像叠加 `cia_w` 中文注记。
- 天地图地形（仅 GitHub 版）：`ter_w` 地形晕渲叠加 `cta_w` 中文注记。
- Esri 卫星：Esri World Imagery。
- OpenTopoMap：OSM 数据与 SRTM 地形风格。
- OSM 人道主义：OSM France 托管的 HOT 风格瓦片。
- 地形与等高线：Mapterhorn Terrarium DEM；开启 3D 或选择户外底图时按视野请求，二者共享 DEM 缓存，切换底图后会自动恢复三维状态。

应用会在地图上显示各服务要求的署名。OpenFreeMap 和 Mapterhorn 公共实例无需 Key，但均按原样提供、没有 SLA，并可能停止或变更服务；需要稳定保障时应考虑自托管。天地图服务需要有效 Key，并受其服务条款和调用额度约束；Esri 影像受 Esri 及其数据提供方条款约束；OSMF、OpenTopoMap 和 OSM France 的公共瓦片也不是无限制 CDN。不得批量下载或离线预取，公开大规模分发前应分别核对服务方的最新政策并选择有明确授权和容量保障的服务。
