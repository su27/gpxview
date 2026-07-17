# GpxView

Windows 下轻量、快速的 GPX、KML、KMZ、FIT 轨迹查看器。

## 功能

- 打开、拖放或从 Windows 文件资源管理器启动 `.gpx`、`.kml`、`.kmz`、`.fit`
- OSM 标准、天地图街道/影像/地形、Esri 卫星、OpenTopoMap、OSM 人道主义底图切换
- 地图铺满内容区，摘要和图表以半透明背景模糊层覆盖在地图上，可一键收起
- 状态栏持续显示文件格式、距离、累计爬升、总用时和轨迹点数
- 单图标切换跟随系统、浅色和深色主题，WPF、地图覆盖层与图表同步
- 海拔作为基础曲线，可按数据存在情况叠加心率、速度和功率；悬停数值与地图位置联动
- 距离、时长、移动时间、爬升、速度、心率、踏频、功率统计
- Garmin FIT GPS 与常用运动记录字段
- WGS84 默认显示，可手动纠正实际保存为 GCJ-02 或 BD-09 的非标准源文件
- 大轨迹在发送到地图和图表前自动抽样
- 多尺寸 Windows 应用图标、开始菜单入口、卸载支持和四类文件关联

键盘操作：`Ctrl+O` 打开文件；`F11` 进入或退出全屏；全屏时也可按 `Esc` 返回。

## 安装

构建出的 64 位 MSI 位于：

```text
artifacts\installer\GpxView-0.1.0-win-x64.msi
```

安装器将 GpxView 安装到 Program Files，创建开始菜单入口，并向 Windows 注册 GPX、KML、KMZ、FIT 的打开方式和“默认应用”能力；不创建桌面快捷方式。安装包包含 .NET 10 桌面运行时，终端用户无需另行安装 .NET。现代 Windows 通常已包含 Microsoft Edge WebView2 Runtime；若该组件缺失，应用会显示修复提示。

## 技术栈

- .NET 10 LTS / C# / WPF Fluent theme
- Microsoft WebView2
- 随应用本地分发的 MapLibre GL JS 5.6.2
- OSM 系、天地图与 Esri 栅格地图服务
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

仓库通过 `global.json` 固定使用 .NET SDK 10.0.302。MapLibre 的 JS、CSS 和许可证固定保存在 `src\GpxView.App\Web\vendor\maplibre-gl\5.6.2`，应用运行时不依赖 unpkg 等前端 CDN。

## 天地图配置

复制示例配置并填写在天地图服务中心申请的浏览器端 Key 和安全密钥：

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

`MapServices.local.json` 已被 Git 忽略，并会复制到开发输出和自包含发布目录。缺少有效的 `tk` 或 `sk` 时，三个天地图选项会自动禁用。

天地图官方建议安全密钥仅由自有代理服务器追加。当前桌面版本没有代理，在开启安全密钥后必须把 `tk` 与 `sk` 一起发送给 WMTS，因此该配置虽然不会进入 Git，仍会包含在发布目录和 MSI 中，也能被终端用户提取。当前方式只适合自用和小范围测试；公开分发前应改为用户自行配置或由受控代理转发。

## 发布与安装器构建

先生成不裁剪的 64 位自包含发布目录，再构建 MSI：

```powershell
dotnet publish src\GpxView.App\GpxView.App.csproj -p:PublishProfile=win-x64
dotnet build installer\GpxView.Installer.wixproj -c Release
```

输出目录：

```text
artifacts\publish\win-x64\
artifacts\installer\GpxView-0.1.0-win-x64.msi
```

应用图标如需从可编辑源重新生成：

```powershell
dotnet run --project tools\GpxView.IconGenerator\GpxView.IconGenerator.csproj --configuration Release -- src\GpxView.App\Assets
```

## 坐标约定

GPX、KML 和 FIT 按规范默认视为 WGS84，直接显示在 OSM 和天地图球面墨卡托图层上，不做火星坐标偏移。如果某个第三方文件实际写入了 GCJ-02 或 BD-09，可在窗口顶部选择对应“源坐标”，应用只在内存中纠正为 WGS84，不修改原文件。

## 地图服务

- OSM 标准：OpenStreetMap Foundation 公共瓦片服务。
- 天地图街道：`vec_w` 底图叠加 `cva_w` 中文注记。
- 天地图影像：`img_w` 影像叠加 `cia_w` 中文注记。
- 天地图地形：`ter_w` 地形晕渲叠加 `cta_w` 中文注记。
- Esri 卫星：Esri World Imagery。
- OpenTopoMap：OSM 数据与 SRTM 地形风格。
- OSM 人道主义：OSM France 托管的 HOT 风格瓦片。

应用会在地图上显示各服务要求的署名。天地图服务需要有效 Key，并受其服务条款和调用额度约束；Esri 影像受 Esri 及其数据提供方条款约束；OSMF、OpenTopoMap 和 OSM France 的公共瓦片也不是无限制 CDN。不得批量下载或离线预取，公开大规模分发前应分别核对服务方的最新政策并选择有明确授权和容量保障的服务。
