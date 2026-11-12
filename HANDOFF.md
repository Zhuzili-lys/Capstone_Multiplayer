# 交接上下文：VR 双人协同无人机设计（Capstone）

> 用途：迁移会话时，把本文件内容作为上下文提供给 AI 助手，即可无缝继续。
> 最后更新：2026-11-12

## 1. 背景与目标

- 项目：墨尔本大学 Capstone — VR-based Collaborative UAV Design
- 目标：双人 VR 协同空间，两名设计师远程同时查看/修改同一架参数化无人机
- 当前阶段：单人原型已完成 → 正在做多人化（网络同步）
- 主工程：`C:\Users\刘雨声\My project`（Unity 6000.6.7f1，VR Multiplayer 模板工程）
- 硬件：Meta Quest 2 ×2（同一 WiFi）；开发机 RTX 4060 Ti 8GB

## 2. 已完成的资产迁移

已从队友的 `UAV_AllFeatures_v01.unitypackage` 导入到工程（保留 `.meta`，GUID 未变）：

- `Assets/scripts/ParametricDrone.cs`
- `Assets/scripts/DroneRotator.cs`
- `Assets/scripts/DronePartSelector.cs`
- `Assets/scripts/ExplodedView.cs`
- `Assets/FeatureHandoff/Drone.prefab`

已确认**不需要**导入 `VR_Simple_prototype.unitypackage`：它与本工程有 576 个 GUID 重复，
且其 `VRTemplateAssets/Scripts/XRKnob.cs`、`AnchorVisuals.cs` 与
`Assets/VRMPAssets/Scripts/Helpers/` 下已有的同名类冲突（会报 `CS0101`）。
它本质是另一份独立的 VR 模板工程，应单独打开。

## 3. 现有代码架构（四个脚本，均为单人离线逻辑）

### ParametricDrone
用 Unity 图元程序化构建四旋翼（尺寸来自 Fusion 360 "Drone Design 101" 示例）。

- 参数（毫米）：`frameSize`(120-450) / `armWidth` / `plateLength` / `plateWidth` /
  `plateGap` / `bottomPlateThickness` / `topPlateThickness` / `motorDiameter` / `propDiameter`
- `displayScale`(1-10)：VR 中放大便于观察
- 流程：`Start` → `BuildParts()` → `Apply()` → `MakeGrabbable()`；
  `OnValidate`/`Update` 里 `dirty` → `Apply()`
- 公开事件 `event Action Rebuilt`（参数重建后触发，`ExplodedView` 依赖它）
- `MakeGrabbable()`：`Rigidbody`(kinematic, 无重力) + `XRGeneralGrabTransformer`
  (`allowTwoHandedScaling=true`) + `XRGrabInteractable`(`Multiple` / `Instantaneous` /
  `throwOnDetach=false` / `useDynamicAttach=true`)

### DroneRotator
- 右手 Grip 短按 = 绕当前轴转 45°；长按 0.45s = 切换轴 Y→X→Z
- 用 `transform.RotateAround(模型包围盒中心, 世界轴, step)` 原地旋转
- 默认 InputAction：`<XRController>{RightHand}/{GripButton}`

### DronePartSelector
- 双手射线（`Physics.SphereCastAll`，`rayRadius` 0.015）命中无人机的子物体
- Trigger = 选中（青色）；A/X = 隐藏选中件（指向空则显示全部）；hover = 黄色
- 用 `MaterialPropertyBlock` 染色；`LineRenderer` 画选择射线
- 自动按名字查找 `"Right Controller"` / `"Left Controller"`，并在其子物体里找
  Near-Far / Ray Interactor 作为射线起点

### ExplodedView
- B/Y 切换爆炸视图。算法来自 Freeman (2017)：取整体质心，各零件沿「中心→零件」方向外移，
  `spread`=1.5；`LateUpdate` 用 `MoveTowards` + `SmoothStep` 做动画
- 订阅 `drone.Rebuilt` 重新 `Capture` 基准位置

### 依赖
XR Interaction Toolkit 3.x。`Drone.prefab` 若报 Missing Script，多为 XRI 版本不匹配
（包内 Samples 版本为 3.5.1）。

## 4. 已确定的技术方案

- 网络栈：**Netcode for GameObjects (NGO) + Unity Transport + UGS（Relay / Lobby / Authentication）**
- 权威模型：**Host-authoritative + 物件所有权转移**（谁抓谁拥有，松手归还）
- 同步范围：
  - 必须同步：无人机**参数**、root 位姿与 `displayScale`、`exploded` 状态、
    `hidden` 零件列表、玩家**头显 + 双手**位姿
  - 本地即可：hover 高亮、选择射线、UI 面板朝向
- 关键设计：模型是**程序化重建**，所以只需同步「参数」，两端各自 rebuild，
  **几何体本身不传输**（省带宽）
- 保留单机离线模式；网络不能阻塞单人功能推进

## 5. 环境搭建进度

- [已完成] VR Multiplayer 模板工程已创建
  （Unity Hub → New Project → VR Multiplayer → 勾选 Connect to Unity Cloud）
- [进行中] 打开工程后的 "Welcome to VR Multiplayer Template" 向导：
  点 `Start UGS Configuration` → 关联工程到 Unity Cloud
  （`Edit > Project Settings > Services`，确认勾选 "Connect project to Unity Cloud"）→
  Vivox 步骤（本阶段可跳过语音）→ 完成
- [待确认] Unity Cloud Dashboard 里 `Authentication` / `Lobby` / `Relay` 是否已 Enable
- [待验证] 打开 **Basic scene** → Play → Join Online → 看到 **Status: Connected**

## 6. 实施阶段（工作分解）

- **阶段 0 环境搭建**（当前）
- **阶段 1 玩家化身**：网络化 XR Rig（Player Prefab 必须带 `NetworkObject`），
  头/手位姿约 10-30Hz 同步 + 远端插值，加名字标签
- **阶段 2 共享无人机状态**：参数改为 `NetworkVariable` → 广播触发两端 `Rebuild`；
  同步 root 位姿/缩放；同步 `exploded` 与 `hidden` 列表；后加入者下发全量快照
- **阶段 3 交互同步**：`XRGrabInteractable` → `NetworkObject` 所有权转移（双向抓取仲裁）；
  `DroneRotator` 同步 Transform；`hidden` 同步；hover 保持本地
- **阶段 4 加入 / 权威 / 冲突**：late join 快照、同时操作仲裁、断线重连
- **阶段 5 联调验收**：先 LAN 再 Relay；测 RTT/丢包/同步误差；Quest 2 目标 72Hz；
  自动日志 + 断线恢复；两人能看到同一形状、同步同一动作

## 7. 待改动的代码点

| 文件 | 改动 |
|---|---|
| `ParametricDrone.cs` | 9 个设计参数 + `displayScale` 改 `NetworkVariable`；root 位姿/缩放网络同步；`Rebuilt` 广播 |
| `DronePartSelector.cs` | `hiddenParts` 列表同步；`selected` 视需要同步；hover 保持本地 |
| `DroneRotator.cs` | 同步 Transform（最简） |
| `ExplodedView.cs` | 同步 `exploded` 布尔；`factor` 由两端本地动画推演 |
| 新增 | NetworkManager 连接脚本、网络化 XR 玩家化身、抓取所有权桥接 |

## 8. 已知坑 / 硬约束

- Quest 2 出包：`Build Settings` → Android；`Player Settings` → Scripting Backend = **IL2CPP**，
  Target Architectures = **ARM64**，**Internet Access = Require**（不设会连不上网）
- `XR Plug-in Management` → Android：勾 `Initialize XR on Startup`，Provider = OpenXR，
  Feature Groups 勾 **Meta Quest Support**
- `Multiplayer Play Mode` 可在编辑器内多开虚拟玩家，**每个虚拟玩家要给不同 Tag**，
  否则认证冲突
- Relay / Vivox 有免费额度，按用量计费

## 9. 建议的下一步

1. 完成 UGS 配置验证（Basic scene 出现 `Status: Connected`）
2. 写最小连接脚本：Host / Client + LAN IP 输入 + 状态显示，先在 Multiplayer Play Mode 里跑通
3. 网络化 XR 玩家化身（头 + 双手）
4. 把 `ParametricDrone` 的 9 个设计参数 + `displayScale` 改为网络同步

## 10. 协作偏好

- 用中文回答；分步、具体到 Unity 里的操作位置（菜单路径 / 组件名 / 字段名）
- 严格区分「项目实际实现」与「理论补充」，避免误导
- 不要主动创建多余文档；改动尽量小、聚焦
