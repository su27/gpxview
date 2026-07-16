# GpxView

Windows 下轻量的 GPX、KML、KMZ、FIT 轨迹查看器。

## 当前功能

- 打开或拖放 `.gpx`、`.kml`、`.kmz`、`.fit`
- OSM 标准、Esri 卫星、OpenTopoMap、OSM 人道主义底图切换，轨迹分段、起终点和自动缩放
- 单图标切换跟随系统、浅色和深色主题，WPF 与海拔图同步
- 地图铺满内容区，摘要和海拔图以半透明背景模糊层覆盖在地图上
- 摘要覆盖层仅显示轨迹实际包含或可计算的运动指标
- 海拔作为基础曲线，可按数据存在情况多选叠加心率、速度和功率曲线
- 各指标独立缩放，悬停显示真实数值并与地图位置联动
- 距离、时长、移动时间、爬升、速度、心率、踏频、功率统计
- Garmin FIT GPS 与常用运动记录字段
- WGS84 默认显示，可手动纠正实际保存为 GCJ-02 或 BD-09 的非标准源文件
- 大轨迹在发送到地图和图表前自动抽样

## 技术栈

- .NET 10 LTS / C# / WPF Fluent theme
- Microsoft WebView2
- MapLibre GL JS 5.6.2
- OpenStreetMap raster tiles
- Garmin.FIT.Sdk 21.205.0
- xUnit

## 构建和运行

```powershell
dotnet restore
dotnet build GpxView.sln
dotnet test GpxView.sln
dotnet run --project src/GpxView.App/GpxView.App.csproj
```

仓库通过 `global.json` 固定使用 .NET SDK 10.0.302。

## 坐标约定

GPX、KML 和 FIT 按规范默认视为 WGS84，直接显示在 OSM 上，不做火星坐标偏移。如果某个第三方文件实际写入了 GCJ-02 或 BD-09，可在窗口顶部选择对应“源坐标”，应用只在内存中纠正为 WGS84，不修改原文件。

## 地图服务

当前开发版提供 OSM 标准、OpenTopoMap、OSM 人道主义三个 OSM 系公共栅格底图，以及 Esri World Imagery 卫星影像，并在地图上显示各自要求的署名。Esri 影像受 Esri 服务条款及其数据提供方要求约束；其他公共瓦片也不是无限制 CDN。公开大规模分发前应分别核对服务方的最新政策，不得批量下载或离线预取。地图源保持可替换，以便切换到自建或其他合规服务。
