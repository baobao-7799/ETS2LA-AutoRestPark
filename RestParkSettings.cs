// ETS2LA AutoRestPark — settings (JSON-persisted by ETS2LA's SettingsHandler).
// Copyright (C) 2026 baobao-7799
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FishLeia.AutoRestPark;

public class RestParkSettings
{
    // 总开关 / UI
    public bool Enabled = true;
    public bool ShowWindow = true;

    // 触发
    public bool TriggerOnArrival = true;          // 到达目的地（jobFinished/Delivered/Cancelled）时触发
    public bool TriggerWhenNoJob = true;          // 无任务且停车 N 秒时触发
    public float StationaryTriggerSeconds = 8f;   // 无任务停车的触发秒数
    public float FuelThreshold = 0.2f;            // 油量低于该比例（0~1）时优先去加油站
    public float ReTriggerCooldown = 300f;        // 完成后的再触发冷却（秒）
    public float FailCooldown = 60f;              // 规划失败后的冷却（秒）
    public float UserCooldown = 10f;              // 用户接管/中止后的冷却（秒）

    // 行驶
    public float MaxSpeed = 22f;                  // 巡航速度上限 m/s（≈80 km/h）
    public float SteeringGain = 0.06f;            // 纯追踪转向增益
    public float MaxSteering = 0.45f;             // 转向输出上限
    public float LookaheadTime = 0.7f;            // 前瞻时间（s），前瞻距离 = 速度 × 该值
    public float MaxLatAccel = 2.5f;              // 弯道限速的横向加速度上限 m/s²
    public float ThrottleGain = 0.15f;
    public float BrakeGain = 0.3f;

    // 寻路
    public float SearchRadiusM = 100000f;         // 寻路半径上限（米，默认 100 km）
    public float SampleDist = 4f;                 // 折线采样间距（米）

    // 停车
    public float SlowdownDistance = 15f;          // 距停车点该距离内限速 2.5 m/s
    public float StopDistance = 10f;              // 距停车点该距离时停车
    public bool AutoActivate = true;              // 停稳后自动按 Enter 触发游戏原生自动泊车
    public bool IgnitionOffAfterPark = false;     // 停好后熄火
}
