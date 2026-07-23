# 历史轨迹密度实验工具

数据包的两层 ZIP 结构、静态密码恢复、Shapefile/DBF 字段以及旧 App 的完整加载与绘制流程见 [`DATA_FORMAT.md`](DATA_FORMAT.md)。

该工具把两步路历史离线路网包转换为透明栅格 PMTiles。默认参数选择一小块区域并设置 2 万条记录保护上限；省级构建必须显式扩大范围和 `--max-records`。生成过程使用 metatile 分块、SQLite 临时缓存和按缩放层续算，默认输出 lossless WebP。

安装依赖：

```powershell
python -m pip install -r tools\roadnet\requirements.txt
```

生成门头沟实验区域：

```powershell
python tools\roadnet\build_density_pmtiles.py `
  C:\Users\su27\Downloads\北京.zip `
  "$env:LOCALAPPDATA\GpxView\RoadNetwork\mentougou-density.pmtiles" `
  --bounds 115.9,39.9,116.0,40.0 `
  --preview artifacts\roadnet\mentougou-density-preview.png
```

实验范围对应分片 `54526392.zip`，包含约 2573 条裁切后的记录。工具以 `ORIGINALID` 为单位向每个像素投票，避免采样点数量直接放大热度；每个缩放级别使用非零像素的 p99 和 `log1p` 做稳定压缩。

生成北京范围、最高到 z16：

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

中断构建会保留 `.building.sqlite`，再次使用完全相同的参数运行时会跳过已经完成的缩放层；当前正在处理的缩放层会重新开始。成功生成正式 PMTiles 后，临时数据库会自动删除。

把输出文件放在 `%LOCALAPPDATA%\GpxView\RoadNetwork` 顶层后，GpxView 会扫描所有有效的 `*.pmtiles` 并启用“路网”按钮。多个归档目前必须互不重叠；备份和实验文件可移入 `RoadNetwork\Archive` 等子目录，应用不会扫描子目录。没有本地归档时不会提供热图图层。
