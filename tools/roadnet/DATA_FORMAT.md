# 两步路历史离线路网数据格式与旧 App 使用方式

本文记录对以下两个文件的静态分析和实测结果：

- `北京.zip`
- `两步路户外助手_6.6.6_共存版.apk`

研究日期为 2026-07-22。本文只描述数据格式、可复现的解密方法和旧 App 的读取/绘制流程；北京全量瓦片的生成方案另行讨论。

## 1. 结论摘要

`北京.zip` 不是地图瓦片包，也不是某种私有空间数据库。它是一个省级汇总容器，内部包含 96 个以数字 `fileId` 命名的小 ZIP；每个小 ZIP 都是一块约 `0.1 x 0.1` 度范围的、使用传统 ZipCrypto 加密的 Esri Shapefile。

解密后的核心数据是：

- WGS84 / EPSG:4326 坐标；
- Esri Shapefile `PolyLineZ`，形状类型编号为 13；
- XY 是经纬度，Z 是逐点海拔，M 未使用；
- DBF 保存轨迹标识、点数/规模、运动类型和长度；
- 数据由 ArcGIS 10.4 在 2017-08-16 按规则网格裁切生成。

旧 App 并没有预先把整个省转换成地图瓦片。它在地图缩放到 z11 以后，按当前位置加载相交的 Shapefile，通过 GDAL/OGR 做空间和属性过滤，将轨迹即时画到透明位图，再切成 4 个子瓦片写入本地缓存。所有轨迹使用相同线宽和颜色，没有显式的密度归一化；多条 GPS 轨迹的空间偏移会自然形成较粗的通行走廊。

## 2. 样本指纹

为便于以后确认研究对象是否一致：

| 文件 | 大小 | SHA-256 |
|---|---:|---|
| `北京.zip` | 321,117,778 B | `6A22E8819924020D3733B67C0B65C509FC71AC86D7E249E5E55F560B382355FF` |
| `两步路户外助手_6.6.6_共存版.apk` | 47,515,141 B | `4AA6ABB588B3C73FAA791CE268AE2238A6180D549F242C58107722D6931F7358` |
| APK 内 `lib/armeabi-v7a/libtbulujni.so` | 108,216 B | `DBC8009F12747FB6111549920BA230706E6C3F8DA89C36DE6AFC2B379EDC5284` |

北京包的总体统计：

| 项目 | 数值 |
|---|---:|
| 分片数 | 96 |
| DBF 记录数 | 212,598 |
| 被删除的 DBF 记录 | 0 |
| 不同 `PID` | 64,089 |
| 不同 `ORIGINALID` | 63,110 |
| 总边界 | `115.5,39.5,117.5,41.0` |
| 内层 ZIP 解压后总大小 | 791,524,968 B |
| 其中 `.shp` | 774,245,908 B |
| 其中 `.dbf` | 12,774,504 B |

这里的总边界是分片网格的并集，不等于北京市行政区边界。96 个分片只是该矩形内含数据的网格，不是完整的 20 x 15 个格子。

## 3. 两层 ZIP 结构

### 3.1 省级外层包

`北京.zip` 的 96 个条目形如：

```text
54526457.zip
54526318.zip
54526536.zip
...
```

外层 ZIP 的特点：

- 条目本身未加密；
- 压缩方法为 `STORE`（方法 0），即只是把内层 ZIP 原样装进省级容器；
- 数字文件名是旧服务端 XML 索引中的 `Fid` / `fileId`，不是经纬度编码。

旧 App 的下载逻辑实际处理的是一个个 `fileId.zip`。`北京.zip` 更像是后来汇总出来的省级分发包，不是旧 App 代码直接打开的单一文件格式。

### 3.2 加密的内层分片

每个内层 ZIP 包含同名的 Shapefile 组件：

```text
trackpos.cpg
trackpos.dbf
trackpos.prj
trackpos.sbn
trackpos.sbx
trackpos.shp
trackpos.shp.xml
trackpos.shx
```

内层条目的特点：

- 压缩方法为 `DEFLATE`（方法 8）；
- ZIP general-purpose flag bit 0 为 1，表示加密；
- `extract version` 为 2.0；
- 没有 WinZip AES 的 `0x9901` extra field；
- 因此它是传统 PKZIP/ZipCrypto，而不是 AES ZIP。

旧 App 使用 Zip4j 读取，显式设置文件名字符集为 `GBK`。当前包中的文件名都是 ASCII，但兼容工具最好仍允许 GBK 文件名。

## 4. 密码从哪里来

### 4.1 Java 层入口

APK 中存在：

```java
package com.lolaage.tbulu.jni;

public class CipherUtil {
    static {
        System.loadLibrary("tbulujni");
    }

    public native String getTrackNetworkSecretKey();
}
```

`ArcgisMapView` 解压路网时调用：

```java
CompressUtil.unzip(zipPath, outputFolder,
    cipherUtil.getTrackNetworkSecretKey(), progressListener);
```

因此密码不是用户密码，也不是由设备动态派生；它是旧 App 随 APK 携带的静态数据解密口令。

### 4.2 ARM 原生库中的构造过程

APK 只携带一个相关原生库：

```text
lib/armeabi-v7a/libtbulujni.so
```

它是 32 位 ARM EABI5 ELF。导出符号表中可以看到：

```text
Java_com_lolaage_tbulu_jni_CipherUtil_getTrackNetworkSecretKey
```

该函数位于 Thumb 地址约 `0x8a71`，大小 216 字节。密码没有以完整明文出现在 `strings` 结果中；函数从 `.rodata` 中取若干短字符串，通过多次头插和尾部追加构造最终值：

```text
初始:               AD7B25F8A743A0D55E
头插 "8":          8AD7B25F8A743A0D55E
头插 "A":         A8AD7B25F8A743A0D55E
头插 "9":         9A8AD7B25F8A743A0D55E
头插 "8":         89A8AD7B25F8A743A0D55E
头插 "A":         A89A8AD7B25F8A743A0D55E
尾加 "9755":      A89A8AD7B25F8A743A0D55E9755
尾加 "6695":      A89A8AD7B25F8A743A0D55E97556695
```

最终密码为：

```text
A89A8AD7B25F8A743A0D55E97556695
```

### 4.3 如何复核原生函数

可使用 Android NDK 自带的 `llvm-objdump`。符号实际为 Thumb 指令，反汇编时要指定 Thumb triple，否则会被误解码成 ARM 指令：

```powershell
$Objdump = "$env:LOCALAPPDATA\Android\Sdk\ndk\28.0.13004108\toolchains\llvm\prebuilt\windows-x86_64\bin\llvm-objdump.exe"
& $Objdump -d --triple=thumbv7-none-linux-android `
  --start-address=0x8a70 --stop-address=0x8b48 `
  .\libtbulujni.so
```

NDK 版本号可能不同，应按本机安装目录调整。

## 5. 如何解密和提取

Python 标准库支持传统 ZipCrypto。下面的脚本不需要第三方解密库，会遍历省级包并把每个分片提取到独立目录：

```python
from io import BytesIO
from pathlib import Path
from zipfile import ZipFile

SOURCE = Path(r"C:\Users\su27\Downloads\北京.zip")
OUTPUT = Path(r"C:\Users\su27\Downloads\北京-roadnet-decoded")
PASSWORD = b"A89A8AD7B25F8A743A0D55E97556695"

with ZipFile(SOURCE) as outer:
    for outer_entry in outer.infolist():
        if not outer_entry.filename.lower().endswith(".zip"):
            continue

        shard_name = Path(outer_entry.filename).stem
        shard_output = OUTPUT / shard_name
        shard_output.mkdir(parents=True, exist_ok=True)

        with ZipFile(BytesIO(outer.read(outer_entry))) as inner:
            inner.setpassword(PASSWORD)
            bad_entry = inner.testzip()
            if bad_entry is not None:
                raise RuntimeError(f"{outer_entry.filename}: CRC failed for {bad_entry}")

            for member in inner.infolist():
                if member.is_dir():
                    continue
                # 当前数据只有固定的 ASCII 文件名；仍拒绝目录穿越路径。
                target = (shard_output / member.filename).resolve()
                if shard_output.resolve() not in target.parents:
                    raise RuntimeError(f"unsafe ZIP entry: {member.filename}")
                target.write_bytes(inner.read(member))
```

对单个分片也可使用 7-Zip：

```powershell
7z x .\54526392.zip `
  -pA89A8AD7B25F8A743A0D55E97556695 `
  -o.\54526392
```

Windows 自带 `Expand-Archive` 不适合处理这种带密码的 ZIP。

## 6. Shapefile 格式

### 6.1 文件职责

| 文件 | 作用 | 是否为核心读取所必需 |
|---|---|---|
| `trackpos.shp` | 轨迹几何和逐点 Z 值 | 是 |
| `trackpos.shx` | `.shp` 记录偏移索引 | 是 |
| `trackpos.dbf` | 每条几何记录的属性 | 是 |
| `trackpos.prj` | WGS84 坐标系定义 | 强烈建议保留 |
| `trackpos.cpg` | DBF 字符集，内容为 `UTF-8` | 建议保留 |
| `trackpos.sbn/.sbx` | Esri 空间索引 | 可选，但有利于空间查询 |
| `trackpos.shp.xml` | ArcGIS 元数据、来源和处理历史 | 非运行必需，研究时应保留 |

旧 App 的 `ShpUtil.isShpFull()` 只强制检查非空的 `.shp`、`.shx` 和 `.dbf`，但 GDAL/OGR 会同时利用其余伴随文件。

### 6.2 坐标系

`trackpos.prj` 内容为：

```text
GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]]
```

`trackpos.shp.xml` 进一步标明 `EPSG:4326`。所以：

- X 是经度；
- Y 是纬度；
- 单位是度；
- 不是 GCJ-02，也不是 Web Mercator。

旧 App 在高德等中国底图上显示时，会先把当前地图瓦片边界转换回 `gps` 坐标，再对 WGS84 Shapefile 做空间过滤和像素投影。

### 6.3 几何类型和 Z/M

Shapefile 主头信息：

```text
file code: 9994
version: 1000
shape type: 13 (PolyLineZ)
```

记录可以包含一个或多个 part。每个点实际带有 X、Y、Z，并保留 PolyLineZ 的 M 槽位。

实测结论：

- Z 随山地轨迹变化，数值如 164、631、821、1175 等，可判定为海拔；业务和数值尺度表明单位应为米；
- 部分 Z 为 0，应视为缺失或无有效高程的候选值；
- M 范围为 `-1.7976931348623157e+308`，即未使用的 NoData 哨兵；
- 某些 `.shp` 文件总头中的 Z min/max 也错误地保留为上述哨兵，但每条记录自己的 Z 范围和 Z 数组有效。

因此，读取海拔时必须解析每条 PolyLineZ 记录的 Z 数组，不能只看 Shapefile 总头并据此断定“没有高程”。元数据没有声明垂直基准，不能仅凭这些文件区分椭球高、海拔基准或设备原始高度。

### 6.4 网格裁切

`trackpos.shp.xml` 暴露了生成路径，例如门头沟样本：

```text
D:\ClipFile\Shape\115.9_116.0v39.9_40.0.zip\trackpos.shp
```

元数据同时记录 ArcGIS 10.4 的 `Clip` 工具和日期 `20170816`。这说明每个分片是从更大的 `trackpos` 要素类按约 0.1 度网格裁切而来。

同一条原始轨迹跨越网格后，会在多个分片中重复出现或成为不同裁切片段。统计或生成热度时必须按稳定标识去重，不能把每个分片记录都当成一条独立轨迹。

## 7. DBF 属性表

DBF 版本为 dBASE III（`0x03`），门头沟样本的头日期为 2017-08-16。记录数应与 `.shp` 几何记录数一一对应。

字段定义：

| 字段 | DBF 类型 | 宽度/小数 | 已确认或推测的含义 |
|---|---|---:|---|
| `PID` | Numeric | 10/0 | 轨迹或轨迹片段的内部记录标识；同一 PID 可因网格裁切出现在多行/多片 |
| `ORIGINALID` | Numeric | 10/0 | 更上游的原始轨迹标识；适合跨分片、跨片段归并 |
| `PCOUNT` | Numeric | 10/0 | 最符合数据和旧代码行为的解释是原轨迹点数/规模；旧 App 只用它过滤 `> 10`，不把它作为热度权重 |
| `SPEEDTYPE` | Numeric | 10/0 | 运动/速度类别枚举；数据中只有 0、1、2，但 APK 的路网绘制代码没有给出可靠的文字映射 |
| `Shape_Leng` | Float | 19/11 | 轨迹长度；旧 App 与以米计算的瓦片对角线比较，因此操作语义是米 |

门头沟 `54526392.zip` 的第一条记录示例：

```text
PID         = 19531282
ORIGINALID  = 433707
PCOUNT      = 192
SPEEDTYPE   = 2
Shape_Leng  = 17943.4234137
```

北京全量字段统计：

| 字段 | 分布 |
|---|---|
| `PCOUNT` | min 21，p50 137，p90 559，p99 1869，max 10784 |
| `Shape_Leng` | min 151 m，p50 5,615 m，p90 41,276 m，p99 153,028 m，max 1,048,388 m |
| `SPEEDTYPE=0` | 7,142 行 |
| `SPEEDTYPE=1` | 126,934 行 |
| `SPEEDTYPE=2` | 78,522 行 |

标识关系的实测结果：

- 212,598 行中有 64,089 个不同 `PID`；
- 有 63,110 个不同 `ORIGINALID`；
- 一个 PID 只对应一个 `ORIGINALID`；
- 439 个 `ORIGINALID` 对应多个 PID，最多 23 个。

因此 `ORIGINALID` 比 PID 更接近“原始完整轨迹/轨迹组”的去重键。不过这只是从数据关系和字段命名得出的工程判断；旧 App 绘制时并没有使用这两个字段分组。

不要把 DBF 行数、点数或 `PCOUNT` 直接当作通行热度：

- 同一轨迹可能跨网格形成多行；
- 长轨迹天然有更多点；
- 不同设备和年份的采样频率可能不同；
- `PCOUNT` 在旧 App 中只是最低质量过滤条件。

## 8. 旧 App 如何获得和管理路网

以下流程来自 APK 6.6.6 的反编译代码。

### 8.1 服务端分片索引

`TrackNetInfo.queryUpdate()`：

1. 从本地 `TrackNetVersionDB` 读取最大版本；没有版本时使用 `1`；
2. 调用 `checkRoadNetUpdate`，请求参数包含 `ver`；
3. 若返回 `hasNew=true` 和 `xmlId`，按文件 ID 下载 `trackNet-{xmlId}.xml`；
4. 用 SAX 解析 XML 中的 `<row>`。

每个 `<row>` 使用以下属性：

| XML 属性 | App 字段 | 作用 |
|---|---|---|
| `Fid` | `fileId` | 下载文件 ID，最终文件名为 `{Fid}.zip` |
| `TName` | `name` | 分片名称 |
| `XMin/XMax` | `minLon/maxLon` | 经度范围 |
| `YMin/YMax` | `minLat/maxLat` | 纬度范围 |
| `TSize` | `fileSize` | 下载大小校验 |
| `version` | `version` | 路网版本和存储目录版本 |

版本升级时，`TrackNetUpdater` 会暂停下载任务、提示升级会删除旧数据、清除被新版本排除的离线任务，再导入新的 XML 分片信息。

### 8.2 下载与缓存

下载文件名是 `{fileId}.zip`。任务调度有两层并发限制：

- `NetManager` 同时最多运行 2 个路网下载任务；
- 每个 `NetDownloadManager` 使用 5 线程池下载该任务中的分片。

App 会尝试从已有离线缓存复制同一分片，找不到时才调用按文件 ID 下载的接口。它用 XML 中的 `fileSize` 做近似大小检查。

旧路径常量表明缓存主要位于外部存储下：

```text
/lolaage/TbuluTools/cache/TrackNetwork/{version}/
/lolaage/TbuluTools/map/net/{version}/{task-description}/
```

前者是自动/共享缓存，后者是用户创建的离线路网任务目录。不同 Android 版本和“共存版”的包环境可能改变实际挂载前缀，但目录结构由代码固定。

### 8.3 解密和落盘

地图准备加载某分片时：

1. 检查剩余空间是否大于压缩分片大小的 3 倍；
2. 在解析目录创建状态标记文件，避免把未完成目录当成有效数据；
3. 调用 Zip4j，文件名字符集设为 GBK；
4. 如果 ZIP 加密，设置 `getTrackNetworkSecretKey()` 返回的密码；
5. `extractAll()` 解压全部 Shapefile 组件；
6. 删除解析状态标记；
7. `ShpUtil.isShpFull()` 确认 `.shp/.shx/.dbf` 均存在且非空；
8. 找到 `.shp` 后连同 XML 索引中的经纬度范围注册到地图路网加载器。

## 9. 旧 App 如何实时绘制

### 9.1 何时加载

地图缩放级别低于 z11 时不加载路网。达到 z11 后，App 根据地图中心和 XML 索引找到相应 `TrackNetInfo`，下载/解压并注册与视野相交的 Shapefile。

`TrackNetworkLoader` 最多保留 8 个活跃 Shapefile 图层；超过后会移除离当前地图中心最远的一个。

### 9.2 GDAL/OGR 查询

每次需要新路网瓦片时，`TrackNetworkTileLayer`：

1. `gdal.AllRegister()`、`ogr.RegisterAll()`；
2. 将 GDAL 缓存上限设为 20 MiB；
3. 用 `ESRI Shapefile` 驱动打开 `.shp`；
4. 把当前地图瓦片边界从底图坐标系纠正到 `gps` / WGS84；
5. 调用 `SetSpatialFilterRect()`，只读取与瓦片相交的要素；
6. 设置属性过滤条件。

有效属性过滤条件是：

```text
Shape_Leng > 当前瓦片对角线米数 / 4 AND PCOUNT > 10
```

所以低缩放层会丢弃相对当前瓦片太短的轨迹，避免一次画入过多细碎线；随缩放增大，长度门槛自然降低。当前北京包的 `PCOUNT` 最小值已经是 21，因此 `PCOUNT > 10` 对这份包实际上不会再剔除记录。

### 9.3 几何抽稀

旧绘制器没有原样把每个 GPS 点都送入 Android `Path`。它做了两级像素距离抽稀：

- 从 OGR geometry 转为 `LatLng` 时，按当前画布经纬度/像素比例跳过过近点；
- 构造 `Path` 时，若新点相对上一个保留点在 X、Y 两轴都不超过约 3 px，则继续跳过。

这显著减少了旧设备上的 Path 节点数，但也会让高倍放大时的几何细节受限。

### 9.4 画布、切片和缓存

根据 Java heap 上限，单个子瓦片尺寸为：

- 最大内存小于 200 MiB：256 px；
- 否则：512 px。

绘制器不是每次只画一个子瓦片，而是：

1. 为父瓦片创建 `2 x tileSize` 的透明画布，即 512 或 1024 px；
2. 一次查询并绘制整个父画布范围；
3. 将大图切成 4 个等大的子瓦片；
4. 把 4 个结果写到下一缩放级别的本地瓦片缓存；
5. 后续请求优先读取缓存。

缓存按坐标纠偏类型分开，并使用 `.mbtiles` 文件。这让同一份 WGS84 路网可以叠加到不同坐标体系的底图上，同时避免反复调用 GDAL。

路网父瓦片的并发绘制数量被限制为 2，重复请求同一父瓦片时会等待已有任务完成。

### 9.5 线条样式

原始绘制 Paint 的关键设置：

- `Style.STROKE`；
- 黑色中间色；
- alpha 255；
- 开启抗锯齿；
- 高内存瓦片使用约 0.6 dp 线宽，低内存瓦片约 0.3 dp。

绘制完成后，缓存层再把黑色替换为用户/地图配置的路网颜色和透明度。

所有要素使用同一线宽、颜色和 alpha。`PCOUNT`、`SPEEDTYPE`、`PID`、`ORIGINALID` 都没有参与线宽或色带计算；Z 海拔也没有用于渲染。

所谓“常走路线更粗”主要来自很多独立 GPS 轨迹在相近位置重复绘制，但各自存在数米到数十米的横向误差，从而形成较宽的轨迹束。完全重合的实线不会因为重复绘制而继续变暗，旧 App 也没有做核密度、通行次数归一化或跨分片去重。

## 10. 与当前 PMTiles 热度实验的区别

| 方面 | 旧 App | 当前实验方向 |
|---|---|---|
| 原始输入 | 加密 Shapefile 分片 | 同一批 Shapefile |
| 生成时机 | 浏览时按需实时绘制 | 预处理为地图服务瓦片 |
| 本地缓存 | 按坐标系分开的 MBTiles | 单个 PMTiles 归档 |
| 密度语义 | 轨迹束自然叠加，无显式归一化 | 按 `ORIGINALID` 去重后累计通行密度 |
| 样式 | 单色、固定细线 | 密度色带、透明栅格 |
| 高程 | 不使用 | 当前路网热度也不使用；可另做分析 |
| 性能代价 | 首次浏览时调用 GDAL 和 Android Canvas | 构建较慢，浏览时只读瓦片 |

旧 App 的设计很符合 2017 年移动设备的限制：只下载/解压当前区域，只保留少量活动分片，按需画 2 x 2 metatile，并积极抽稀与缓存。现代桌面或服务端更适合把这些成本前移到离线构建阶段，但需要控制高缩放级别造成的瓦片数量和磁盘占用。

## 11. 已确认事实、推断和未知项

### 已由文件或代码直接确认

- 内层 ZIP 是传统 ZipCrypto，静态密码来自 APK 原生库；
- Shapefile 是 EPSG:4326 PolyLineZ；
- Z 数组存在有效山地高度，M 未使用；
- DBF 字段、宽度和北京全量统计；
- ArcGIS 10.4、2017-08-16、约 0.1 度网格裁切；
- 旧 App 从 z11 开始，以 GDAL/OGR 空间过滤后实时栅格化；
- `Shape_Leng` 和 `PCOUNT` 的过滤表达式；
- 2 x 2 metatile、4 子瓦片缓存、最多 8 个活跃分片和 2 个并发父瓦片绘制；
- 旧绘制器不按 `PCOUNT`、`SPEEDTYPE` 或 ID 改变线条样式。

### 有较强依据但不是元数据明文定义

- Z 的单位应为米；垂直基准未知；
- `PCOUNT` 很可能是原始轨迹点数或规模，而不是通行人数；
- `ORIGINALID` 是当前数据中更合适的跨分片去重键；
- `Shape_Leng` 的操作单位是米。

### 尚未确认

- `SPEEDTYPE` 的 0、1、2 分别对应哪三种活动；
- `PID` 和 `ORIGINALID` 在服务端原始数据库中的正式业务定义；
- 数据在进入 ArcGIS Clip 前是否做过纠偏、清洗、去噪或额外抽稀；
- 高程采用的具体垂直基准和 0 值规则；
- 2017 年之后服务端是否存在未包含在此包中的更新版本。

这些未知项不妨碍解密、读取和地图叠加，但在把字段用于统计结论前应保留上述限制。
