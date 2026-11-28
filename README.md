# baobao-7799 自动休息区停靠 / ETS2LA AutoRestPark

**Author: baobao-7799** · GPL-3.0-or-later

自动前往最近的休息区并把车停好：没有目的地（无任务）时，插件自动接管方向盘油门，
驶向最近的休息区（快没油时改去加油站），到点减速、拉手刹、触发游戏原生自动泊车
（倒车入库/侧方停车由游戏自动泊车完成）。

## How it works

1. **触发**：无目的地（没有任务：未接货/已送达/已取消）且非手动驾驶时，确认 2 秒后自动出发；
   缺油时（fuelWarning 或油量低于阈值）自动出发；也可 UI 手动按钮立即出发。
   车未打火会自动点火、手刹未松会自动松开（AutoIgnition）。
2. **选点**：在 ETS2LA 解析出的地图数据（TruckLib MapData）里找最近的休息区
   （模型名含 rest/hotel/motel/parking/service）与最近的加油站（gas/fuel），
   去两者中更近的那一个。
3. **寻路**：自研 A*（顶点 = 道路节点；边 = 道路长度，prefab 内部走 NavCurve 最短路径），
   目标 = prefab 内最深的停车位节点。
4. **行驶**：路径采样成 4m 折线，纯追踪转向 + 曲率限速 + 限速牌限速 + 前方车辆障碍减速。
5. **停车**：距停车点 10m 内刹停 → 拉手刹 → 按 Enter（游戏原生自动泊车接管倒库/侧方停车）
   → 释放控制。

## Prerequisites

| 组件 | 用途 | 来源 |
| --- | --- | --- |
| ETS2LA Game Plugin（游戏内） | steering 输出、地图/遥测数据 | ETS2LA 官方 |
| scs-sdk-controller 虚拟手柄 | 油门/刹车、手刹/Enter 按键 | github.com/ETS2LA/scs-sdk-controller |

地图数据需要在 ETS2LA 中可用（游戏运行时 ETS2LA 会自动解析；或 DataSettings 中
`ForceMapLoad: true` 提前解析）。

## Installation

1. 关闭 ETS2LA。
2. 把 `dist\AutoRestPark.dll` 复制到 `%LOCALAPPDATA%\ETS2LA\current\Plugins\`。
3. 启动 ETS2LA → 设置 → 插件 → 启用「baobao-7799 自动休息区停靠」。
4. 游戏内按住 **右 Alt** 打开设置窗调参（430×540），也可改
   `%APPDATA%\ETS2LA\fishleia.autorestpark.json`。

## 状态日志（v1.4+）

插件每次状态变化都会写一条 `AutoRestPark: 状态 → …` 到 ETS2LA 的 ets2la.log
（最多 1 条/秒）。排查「为什么不出发」时看日志里的最后一条状态即可定位卡点。

## Settings

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| Enabled / ShowWindow | true | 总开关 / 显示设置窗 |
| TriggerWhenNoDestination | true | 无目的地（无任务）时自动触发 |
| TriggerOnLowFuel | true | 快没油时自动触发（优先加油站） |
| TriggerDelaySeconds | 2 | 触发条件需稳定保持的秒数 |
| ParkedProximityMeters | 80 | 已在休息区旁该距离内时跳过触发（防循环） |
| FuelThreshold | 0.2 | 油量比例低于此值时判定缺油 |
| ReTriggerCooldown / FailCooldown / UserCooldown | 300 / 60 / 10 | 各冷却（秒） |
| MaxSpeed | 22 | 巡航速度上限 m/s |
| SteeringGain / MaxSteering | 0.06 / 0.45 | 转向增益与上限 |
| LookaheadTime / MaxLatAccel | 0.7 / 2.5 | 前瞻时间 s / 弯道横向加速度 m/s² |
| SearchRadiusM / SampleDist | 100000 / 4 | 寻路半径 m / 采样间距 m |
| SlowdownDistance / StopDistance | 15 / 10 | 减速距离 / 停车距离 m |
| AutoActivate | true | 停稳后自动按 Enter 触发自动泊车 |
| AutoIgnition | true | 出发前自动点火+松开手刹 |
| IgnitionOffAfterPark | false | 停好后熄火 |

## Safety

- 任何时刻用户转动方向盘/踩油门/踩刹车，插件立即释放控制（手动驾驶中也不会接管）。
- 行驶中遇到前方车辆自动减速跟随，太近刹停。
- 任务进行中且不缺油时不会自动触发；停靠完成/失败后进入冷却。
- 已在休息区旁（ParkedProximityMeters 内）不会重复触发，防止停好后立刻又出发。
- 寻路/泊车失败自动放弃并冷却，可在设置窗「立即中止」。

## Building

本机没有 .NET SDK 也能编译：`build.ps1` 用 ETS2LA 自带的 Roslyn
（Microsoft.CodeAnalysis）+ 已安装的 .NET 10 运行时，两层引导编译：

```
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

输出 `dist\AutoRestPark.dll`；`install.ps1` 负责复制到 Plugins 目录。
`AutoRestPark.csproj` 保留给有 SDK 的环境（`dotnet build`）。

## License

GPL-3.0-or-later，见 LICENSE。
