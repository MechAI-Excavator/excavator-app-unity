## 项目结构说明

### 顶层结构（概览）

```
Assets/
  Controller/                  # 仿真/玩法控制脚本（挖掘机、相机等）
  Map/                         # OSM 地图渲染与控制脚本
  Networking/
    MQTT/                      # MQTT 消息 DTO（ElevationMsg、RtkGpsMsg 等）
    WebRTC/                    # WHEP/WebRTC 拉流脚本
  Plugins/
    M2Mqtt/                    # 第三方 MQTT 库（M2Mqtt）
  Scenes/                      # 场景
  StreamingAssets/             # Build 安全的原始数据（OSM 等）
  Terrain/                     # 地形高程渲染与 tile 流式系统
  UI Toolkit/                  # UXML/USS 与 UI Settings
  Utils/                       # 通用脚本（UI 逻辑、Bootstrapper 等）
docs/
  TECHNICAL.md                 # 英文：架构/数据流/流程图
  PROJECT_STRUCTURE.md         # 英文：目录结构与规范
  TECHNICAL_ZH.md              # 中文：架构/数据流/流程图
  PROJECT_STRUCTURE_ZH.md      # 中文：目录结构与规范
```

> 注意：目前还存在一个 `Assets/map/`（小写）目录，里面有 `MqttManager.cs`。
> 建议后续统一到一个位置（例如 `Assets/Networking/MQTT/Runtime/`），避免重复与歧义。

---

### 各目录职责说明

#### `Assets/Controller/`
与仿真/控制强相关的脚本（通常具有明确“归属权”）。
- `ExcavatorController.cs`：Articulation 关节驱动 + RTK 位姿平滑
- `CameraController.cs`：轨道相机（围绕目标旋转/缩放）
- `ExcavatorMqttController.cs`：示例控制（可选）

#### `Assets/map/`（当前 MQTT 入口）
MQTT 管理入口：
- `MqttManager.cs`：连接 broker、订阅、主线程派发、解析消息、抛事件/直调控制器


#### `Assets/Networking/MQTT/`
只存放 `JsonUtility` 使用的 **Serializable DTO**（纯数据结构）。
- `ElevationMsg.cs`
- `RtkGpsMsg.cs`

规则：
- DTO 不要引用场景对象/MonoBehaviour，不要写业务逻辑。

#### `Assets/Terrain/`
地形相关的全部逻辑与资源。
- `HandleElevationMap.cs`：把 `ElevationMsg` 写入 TerrainData（SetHeights）
- `TerrainTileManager.cs`：tile 池 + 流式地形（按 `tile_x/tile_y`）
- `ElevationTileStore.cs`：tile 缓存
- `prefab/`：tile 用 Terrain prefab
- `GroundTerrainData.asset`：TerrainData 资产

规则：
- 所有直接调用 `TerrainData.SetHeights/SetAlphamaps` 的逻辑都在这里。

#### `Assets/Map/`
OSM 地图 UI Toolkit 渲染。
- `OSMMapController.cs`：加载 `.osm` 并挂载 `OSMMapElement`
- `OSMMapElement.cs` / `OSMMapLayerElement.cs`：绘制与交互
- `OSMLoader.cs`：解析 `.osm`

数据：
- `Assets/StreamingAssets/map/map.osm`（Build 必需）

#### `Assets/Networking/WebRTC/`
WebRTC/WHEP 视频到 UI Toolkit 的渲染。
- `MultiWebRtcManager.cs`：多路流管理
- `WebRtcSingleStream.cs`：单流实现
- `WebRtcReceiver.cs`：备选实现

#### `Assets/UI Toolkit/`
UI Toolkit 资源：
- `MainLayout.uxml`
- `MainLayout.uss`
- `PanelSettings.asset`

规则：
- UI 查找依赖 `name="..."`，改名时要同步修改 `UILogic`。

#### `Assets/Utils/`
横切关注脚本（不属于单一领域）。
- `UILogic.cs`：订阅 MQTT 事件并更新 UI
- `FKRobotBootstrapper.cs`：URDF Importer 的时序 workaround（如需要）
- `PistonFollower.cs`：当前未使用（确认无引用后可移除）

---

### 命名规范（建议）

#### 脚本/类
- 一个文件一个 `public class`，文件名与类名一致
- 类名 PascalCase（`TerrainTileManager`）
- 若需要领域前缀，使用清晰前缀（`OSMMapController`）

#### UI Toolkit
- `name="..."`：代码稳定标识（例如 `lbl-battery-pct`）
- USS class：kebab-case（`.bottom-bar`、`.panel--dock`）

#### 资源存放
- Build 需要读取的原始文件 → `StreamingAssets/`
- 模块专属 prefab → 放在模块目录内 `prefab/`
- 第三方库 → `Plugins/`

---

### 模块边界（工程规则）
- MQTT 层（`MqttManager`）：
  - UI/观察者用 **事件**（可多订阅、低耦合）
  - 仿真归属系统用 **直接调用**（唯一权威、避免重复应用）
- UI 层（`UILogic`）：
  - 订阅事件更新展示
  - 尽量不直接写仿真状态（避免 UI 变成“控制源”）

