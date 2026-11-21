// ETS2LA AutoRestPark — automatically drives to the nearest rest area (or gas station
// when low on fuel) and parks the truck inside the lines. Native auto-park is triggered
// via the game's own "activate" (Enter) control once the truck is stopped in the zone.
// Copyright (C) 2026 baobao-7799
// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;

using ETS2LA.Backend.Events;
using ETS2LA.Game;
using ETS2LA.Game.Data;
using ETS2LA.Game.Output;
using ETS2LA.Game.SDK;
using ETS2LA.Game.Telemetry;
using ETS2LA.Logging;
using ETS2LA.Overlay;
using ETS2LA.Settings;
using ETS2LA.Shared;

using Hexa.NET.ImGui;

using TruckLib.ScsMap;

namespace FishLeia.AutoRestPark;

public class AutoRestParkPlugin : Plugin
{
    private const string DriveChannelId = "fishleia.autorestpark";
    private const string ParkChannelId = "fishleia.autorestpark.park";
    private const string OutputTopic = "ETS2LA.Game.Output.ControlEvent";
    private const string SettingsFileName = "fishleia.autorestpark.json";
    private const string WindowTitle = "baobao-7799 自动休息区停靠";

    private enum PState { Idle, Planning, Driving, Parking, Done }

    private readonly SettingsHandler _settingsHandler = new();
    private RestParkSettings _cfg = new();
    private volatile bool _settingsDirty;
    private float _saveTimer;

    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private float _now;

    // 本车坐标系
    private Vector3 _prevPos;
    private Vector3 _f = Vector3.UnitZ;
    private float _speed;
    private float _prevLatErr;

    // 转向方向运行时校准
    private int _steerSignRight = 1;
    private bool _steerSignLocked;
    private int _steerCorrCount;
    private int _steerCorrBad;

    // 状态机
    private PState _state = PState.Idle;
    private string _status = "待机";
    private float _stateTime;
    private float _cooldownUntil;
    private float _triggerTimer;

    // 路径
    private List<Vector3> _polyline = new();
    private float[] _cum = Array.Empty<float>();
    private int _followIdx;
    private float _remaining;
    private string _targetDesc = "";

    // 规划任务
    private Task<RoutePlanner.PlanResult?>? _planTask;
    private bool _manualTrigger;

    // 输出
    private bool _active;
    private float _steer, _throttle, _brake;
    private bool _parkPulse, _activatePulse, _ignitionPulse;

    // 停车序列
    private int _parkStage;
    private float _parkStageTime;

    public override PluginInformation Info => new()
    {
        Id = "fishleia.autorestpark",
        Name = "baobao-7799 自动休息区停靠",
        Description = "无目的地（无任务）时自动接管方向盘油门，驶向最近的休息区或最近的加油站（哪个近去哪），并停在线内；停稳后触发游戏原生自动泊车。",
        Version = "1.2.0",
        AuthorName = "baobao-7799",
        AppVersionSupport = new PluginVersionSupport { LowerBound = "2026.11.0" },
        Tags = new[] { "Driving", "AI", "Gameplay", "Navigation" }
    };

    public override float TickRate => 30.0f;

    public override void Init()
    {
        _cfg = _settingsHandler.Load<RestParkSettings>(SettingsFileName) ?? new RestParkSettings();
    }

    public override void OnEnable()
    {
        base.OnEnable();
        OverlayHandler.Current.RegisterWindow(new WindowDefinition
        {
            Title = WindowTitle,
            Width = 430,
            Height = 490,
            Alpha = 0.92f,
            Open = _cfg.ShowWindow
        }, RenderUI);
        Logger.Info("AutoRestPark: enabled.");
    }

    public override void OnDisable()
    {
        ReleaseOutput();
        OverlayHandler.Current.UnregisterWindow(new WindowDefinition { Title = WindowTitle });
        base.OnDisable();
    }

    public override void Shutdown()
    {
        ReleaseOutput();
        if (_settingsDirty)
        {
            _settingsDirty = false;
            _settingsHandler.Save(SettingsFileName, _cfg);
        }
        base.Shutdown();
    }

    public override void Tick()
    {
        _now = (float)_sw.Elapsed.TotalSeconds;

        if (_settingsDirty && (_saveTimer -= 1f / TickRate) <= 0f)
        {
            _saveTimer = 1f;
            _settingsDirty = false;
            _settingsHandler.Save(SettingsFileName, _cfg);
        }

        var cfg = _cfg;
        if (!cfg.Enabled)
        {
            ReleaseOutput();
            SetIdle("已禁用");
            return;
        }

        var td = GameTelemetry.Current.GetCurrentData();
        var traffic = TrafficProvider.Current.GetCurrentTrafficData();
        if (!td.sdkActive || td.paused)
        {
            ReleaseOutput();
            SetIdle("等待 SDK 数据…");
            return;
        }

        Vector3 pos = td.truckPlacement.coordinate.ToVector3();
        var delta = pos - _prevPos;
        if (delta.LengthSquared() > 0.01f)
            _f = Vector3.Normalize(delta);
        _prevPos = pos;
        Vector3 leftRaw = Vector3.Cross(Vector3.UnitY, _f);
        Vector3 left = leftRaw.LengthSquared() > 1e-6f ? Vector3.Normalize(leftRaw) : Vector3.UnitX;

        _speed = td.truckFloat.speed;
        float dt = 1f / TickRate;

        // 用户接管：立即让出控制（泊车阶段的 parkingBrake 是插件自己触发的，不算接管）
        bool userInput = Math.Abs(td.truckFloat.userSteer) > 0.18f ||
                         td.truckFloat.userThrottle > 0.25f ||
                         td.truckFloat.userBrake > 0.08f;
        bool pbInterrupt = td.truckBool.parkingBrake && _state != PState.Parking;
        if (_state != PState.Idle && _state != PState.Done && (userInput || pbInterrupt))
        {
            ReleaseOutput();
            SetIdle("用户接管，已退出");
            _cooldownUntil = _now + cfg.UserCooldown;
            return;
        }

        switch (_state)
        {
            case PState.Idle: TickIdle(cfg, td, traffic, pos, left, dt); break;
            case PState.Planning: TickPlanning(cfg, td, pos, left, dt); break;
            case PState.Driving: TickDriving(cfg, td, traffic, pos, left, dt); break;
            case PState.Parking: TickParking(cfg, td, dt); break;
            case PState.Done: TickDone(); break;
        }

        if (_active)
            PublishDrive();
        if (_parkPulse || _activatePulse || _ignitionPulse)
            PublishParkPulses();
        _parkPulse = _activatePulse = _ignitionPulse = false;
    }

    // ---------- 各状态 ----------

    private void TickIdle(RestParkSettings cfg, GameTelemetryData td, TrafficData? traffic, Vector3 pos, Vector3 left, float dt)
    {
        if (_now < _cooldownUntil)
        {
            _triggerTimer = 0f;
            _status = $"冷却中…（{_cooldownUntil - _now:F0} s）";
            return;
        }

        bool ok = td.truckBool.engineEnabled && !td.truckBool.parkingBrake;
        if (!ok)
        {
            _triggerTimer = 0f;
            _status = "待机（等待点火起步）";
            return;
        }

        // 无目的地 = 没有当前任务（未接货/已送达/已取消）
        bool noDest = string.IsNullOrEmpty(td.configString.jobMarket) && !td.specialBool.onJob;
        bool lowFuel = td.truckBool.fuelWarning || td.truckFloat.fuel < cfg.FuelThreshold;
        bool userDriving = Math.Abs(td.truckFloat.userSteer) > 0.18f ||
                           td.truckFloat.userThrottle > 0.25f ||
                           td.truckFloat.userBrake > 0.08f;

        bool auto = !userDriving && ((cfg.TriggerWhenNoDestination && noDest) || (cfg.TriggerOnLowFuel && lowFuel));

        if (auto) _triggerTimer += dt;
        else _triggerTimer = 0f;

        if (_manualTrigger)
        {
            _manualTrigger = false;
            _triggerTimer = 0f;
            if (StartPlanning(pos, cfg)) return;
        }
        else if (auto && _triggerTimer > cfg.TriggerDelaySeconds)
        {
            _triggerTimer = 0f;
            if (StartPlanning(pos, cfg)) return;
        }

        if (userDriving)
            _status = "待机（手动驾驶中，不接管）";
        else if (noDest)
            _status = lowFuel
                ? $"待机（无目的地+缺油，出发倒计时 {Math.Max(0f, cfg.TriggerDelaySeconds - _triggerTimer):F1} s）"
                : $"待机（无目的地，出发倒计时 {Math.Max(0f, cfg.TriggerDelaySeconds - _triggerTimer):F1} s）";
        else if (lowFuel)
            _status = $"待机（缺油，出发倒计时 {Math.Max(0f, cfg.TriggerDelaySeconds - _triggerTimer):F1} s）";
        else
            _status = "待机（任务进行中，或点“立即前往最近停靠点”）";
    }

    private bool StartPlanning(Vector3 truckPos, RestParkSettings cfg)
    {
        var map = GetMapData();
        if (map == null)
        {
            _status = "地图数据未加载（等待 ETS2LA 解析地图）";
            return false;
        }

        // 最近停靠点 = 最近的休息区 与 最近的加油站 二者中更近的那个
        var cands = FindCandidates(map, truckPos);
        if (cands.Count == 0)
        {
            _status = "地图内未找到休息区或加油站";
            _cooldownUntil = _now + cfg.FailCooldown;
            return false;
        }

        // 已经在停靠点旁边 → 不重复触发（防止停好后立刻又出发）
        float nearestDist = Vector3.Distance(cands[0].Item2, truckPos);
        if (nearestDist < cfg.ParkedProximityMeters)
        {
            _status = cands[0].Item3 ? "已在加油站旁，跳过" : "已在休息区旁，跳过";
            _cooldownUntil = _now + Math.Min(cfg.ReTriggerCooldown, 30f);
            return false;
        }

        int maxTry = Math.Min(6, cands.Count);
        var candCopy = cands.GetRange(0, maxTry);
        var planner = new RoutePlanner(map);
        float radius = cfg.SearchRadiusM;
        float sd = cfg.SampleDist;
        _targetDesc = candCopy[0].Item3 ? "加油站" : "休息区";

        _planTask = Task.Run(() =>
        {
            foreach (var (prefab, origin, isGas) in candCopy)
            {
                try
                {
                    var res = planner.Plan(truckPos, prefab, radius, sd, null);
                    if (res != null && res.Points.Count >= 2)
                        return res;
                }
                catch { }
            }
            return null;
        });

        SetState(PState.Planning, "规划路线中…");
        return true;
    }

    private void TickPlanning(RestParkSettings cfg, GameTelemetryData td, Vector3 pos, Vector3 left, float dt)
    {
        if (_planTask == null)
        {
            SetIdle("规划失败");
            _cooldownUntil = _now + cfg.FailCooldown;
            return;
        }
        if (!_planTask.IsCompleted)
        {
            _status = $"规划路线中…（{_stateTime:F1} s）";
            return;
        }

        RoutePlanner.PlanResult? res = null;
        try { res = _planTask.Result; }
        catch { res = null; }
        _planTask = null;

        if (res == null || res.Points.Count < 2)
        {
            SetIdle("规划失败：未找到可达路线（可能距离过远）");
            _cooldownUntil = _now + cfg.FailCooldown;
            return;
        }

        _polyline = res.Points;
        _cum = res.CumDist;
        _followIdx = 0;
        _remaining = _cum.Length > 0 ? _cum[_cum.Length - 1] : 0f;
        _prevLatErr = 0f;
        Logger.Info($"AutoRestPark: 路线规划成功，{_polyline.Count} 个点，全长 {_remaining:F0} m");
        SetState(PState.Driving, "行驶中 → " + _targetDesc);
    }

    private void TickDriving(RestParkSettings cfg, GameTelemetryData td, TrafficData? traffic, Vector3 pos, Vector3 left, float dt)
    {
        if (_polyline.Count < 2)
        {
            Abort("路径为空", cfg.UserCooldown);
            return;
        }

        int ci = FindClosestIndex(pos);
        if (ci >= 0)
            _followIdx = Math.Max(_followIdx, ci - 1);
        _remaining = _cum[_cum.Length - 1] - _cum[Math.Min(_followIdx, _cum.Length - 1)];

        if (_remaining < cfg.StopDistance)
        {
            _steer = 0f;
            _throttle = 0f;
            _brake = 1f;
            SetState(PState.Parking, "到达停车点，减速停车");
            _parkStage = 0;
            _parkStageTime = 0f;
            return;
        }

        // 纯追踪
        float look = Math.Clamp(_speed * cfg.LookaheadTime, 8f, 30f);
        Vector3 target = LookaheadPoint(look);
        var rel = target - pos;
        rel.Y = 0f;
        float dist = Math.Max(rel.Length(), 0.1f);
        float latErr = Vector3.Dot(rel, left);
        float dLat = (latErr - _prevLatErr) * TickRate;
        _prevLatErr = latErr;
        _steer = Math.Clamp(-_steerSignRight * cfg.SteeringGain * (latErr / dist), -cfg.MaxSteering, cfg.MaxSteering);
        CalibrateSteerSign(latErr, dLat);

        // 期望速度
        float desired = cfg.MaxSpeed;
        float limit = td.truckFloat.speedLimit;
        if (limit > 1f) desired = Math.Min(desired, limit + 0.5f);
        float curv = EstimateCurvature();
        if (curv > 1e-4f) desired = Math.Min(desired, MathF.Sqrt(cfg.MaxLatAccel / curv));
        desired = Math.Max(desired, 2f);
        if (_remaining < cfg.SlowdownDistance) desired = Math.Min(desired, 2.5f);

        // 障碍减速
        if (traffic != null)
        {
            float obRange = Math.Min(50f, look * 1.5f + 5f);
            foreach (var v in traffic.vehicles)
            {
                if (v == null || v.isTrailer) continue;
                var vr = v.Position - pos;
                vr.Y = 0f;
                float ahead = Vector3.Dot(vr, _f);
                float lat = Vector3.Dot(vr, left);
                if (ahead < 2f || ahead > obRange || Math.Abs(lat) > 5f) continue;
                desired = Math.Min(desired, Math.Max(0f, v.speed * 0.8f));
                if (ahead < 12f) desired = 0f;
            }
        }

        _throttle = desired - _speed > 0.3f ? Math.Clamp((desired - _speed) * cfg.ThrottleGain, 0f, 1f) : 0f;
        _brake = _speed - desired > 0.3f ? Math.Clamp((_speed - desired) * cfg.BrakeGain, 0f, 1f) : 0f;
        _status = $"行驶中 → {_targetDesc}（剩余 {_remaining:F0} m，速度 {_speed:F1}/{desired:F1} m/s）";
    }

    private void TickParking(RestParkSettings cfg, GameTelemetryData td, float dt)
    {
        _stateTime += dt;
        _parkStageTime += dt;

        if (_speed > 0.15f)
        {
            _throttle = 0f;
            _brake = 1f;
            _status = "停车中…";
            return;
        }
        _brake = 0f;

        switch (_parkStage)
        {
            case 0: // 拉手刹（Toggle 脉冲一次）
                if (!td.truckBool.parkingBrake && _parkStageTime > 0.5f)
                {
                    _parkPulse = true;
                    _parkStageTime = 0f;
                }
                else if (td.truckBool.parkingBrake || _stateTime > 6f)
                {
                    _parkStage = 1;
                    _parkStageTime = 0f;
                }
                _status = "泊车中：拉手刹…";
                break;
            case 1: // 稍等后按 Enter 触发游戏原生自动泊车
                if (_parkStageTime > 0.6f)
                {
                    if (cfg.AutoActivate)
                    {
                        _activatePulse = true;
                        _parkStage = 2;
                    }
                    else
                    {
                        _parkStage = 3;
                    }
                    _parkStageTime = 0f;
                }
                _status = "泊车中：触发自动泊车…";
                break;
            case 2: // 等待游戏自动泊车完成（若开启熄火则再熄火）
                if (_parkStageTime > 2.5f)
                {
                    if (cfg.IgnitionOffAfterPark)
                        _ignitionPulse = true;
                    _parkStage = 3;
                    _parkStageTime = 0f;
                }
                _status = "泊车中：等待游戏自动泊车（若弹出菜单请手动确认）";
                break;
            case 3:
                ReleaseOutput();
                SetState(PState.Done, "已停靠完成 ✓");
                _cooldownUntil = _now + cfg.ReTriggerCooldown;
                Logger.Info("AutoRestPark: 停靠完成。");
                break;
        }
    }

    private void TickDone()
    {
        _status = $"完成，冷却中（{_cooldownUntil - _now:F0} s）";
        if (_stateTime > 5f)
            SetIdle("待机");
    }

    // ---------- 路径与跟随 ----------

    private int FindClosestIndex(Vector3 pos)
    {
        if (_polyline.Count == 0) return -1;
        int lo = Math.Max(0, _followIdx - 3);
        int hi = Math.Min(_polyline.Count - 1, _followIdx + 80);
        int best = -1;
        float bestD = float.MaxValue;
        for (int i = lo; i <= hi; i++)
        {
            float d = Vector3.DistanceSquared(pos, _polyline[i]);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private Vector3 LookaheadPoint(float look)
    {
        float acc = 0f;
        int i = Math.Clamp(_followIdx, 0, _polyline.Count - 1);
        Vector3 target = _polyline[i];
        while (i + 1 < _polyline.Count)
        {
            float seg = Vector3.Distance(_polyline[i], _polyline[i + 1]);
            if (acc + seg >= look)
            {
                float frac = (look - acc) / Math.Max(seg, 1e-3f);
                return Vector3.Lerp(_polyline[i], _polyline[i + 1], frac);
            }
            acc += seg;
            i++;
            target = _polyline[i];
        }
        return target;
    }

    private float EstimateCurvature()
    {
        int n = _polyline.Count;
        if (n < 12) return 0f;
        int i = Math.Clamp(_followIdx, 0, n - 1);
        int a = Math.Max(0, i - 5);
        int b = Math.Min(n - 1, i + 5);
        Vector3 d1 = _polyline[b] - _polyline[i];
        Vector3 d2 = _polyline[i] - _polyline[a];
        float l1 = d1.Length(), l2 = d2.Length();
        if (l1 < 1f || l2 < 1f) return 0f;
        float sin = Vector3.Cross(d1, d2).Length() / (l1 * l2);
        sin = Math.Clamp(sin, 0f, 1f);
        float baseLen = Vector3.Distance(_polyline[a], _polyline[b]);
        return 2f * sin / Math.Max(baseLen, 2f);
    }

    private void CalibrateSteerSign(float err, float dLat)
    {
        if (_steerSignLocked) return;
        if (Math.Abs(_steer) < 0.15f || Math.Abs(err) < 0.3f) return;
        if (Math.Abs(dLat) < 0.02f) return;
        float expected = -_steerSignRight * _steer;
        _steerCorrCount++;
        if (Math.Sign(dLat) != Math.Sign(expected)) _steerCorrBad++;
        if (_steerCorrCount >= 25)
        {
            if (_steerCorrBad >= _steerCorrCount * 0.7f)
            {
                _steerSignRight = -_steerSignRight;
                Logger.Warn("AutoRestPark: 转向方向已自动校准（翻转 steerSignRight）");
            }
            _steerSignLocked = true;
        }
    }

    // ---------- 地图与目标 ----------

    private static MapData? GetMapData()
    {
        try
        {
            var installs = GameHandler.Current.Installations;
            foreach (var inst in installs)
            {
                if (!inst.IsParsed) continue;
                var md = inst.GetMapData();
                if (md != null) return md;
            }
            return null;
        }
        catch { return null; }
    }

    // 收集休息区 + 加油站两类停靠点，合并后按离卡车距离排序（哪个近就优先哪个）
    private static List<(Prefab, Vector3, bool)> FindCandidates(MapData map, Vector3 truckPos)
    {
        var list = new List<(Prefab, Vector3, bool)>();
        foreach (var kv in map.MapItems)
        {
            if (!(kv.Value is Prefab p)) continue;
            if (!p.ShowInUiMap) continue;
            if (p.Nodes == null || p.Nodes.Count == 0) continue;
            string m = p.Model.ToString().ToLowerInvariant();
            bool isGas = m.Contains("gas") || m.Contains("fuel");
            bool isRest = m.Contains("rest") || m.Contains("hotel") || m.Contains("motel") ||
                          m.Contains("parking") || m.Contains("service");
            if (!isGas && !isRest) continue;
            int oi = p.Origin < p.Nodes.Count ? p.Origin : 0;
            var origin = (Node)p.Nodes[oi];
            list.Add((p, origin.Position, isGas));
        }
        list.Sort((a, b) =>
            Vector3.DistanceSquared(a.Item2, truckPos).CompareTo(Vector3.DistanceSquared(b.Item2, truckPos)));
        return list;
    }

    // ---------- 输出 ----------

    private void PublishDrive()
    {
        _active = true;
        Events.Current.Publish(OutputTopic, new ControlEvent
        {
            ChannelDefinition = new ControlChannelDefinition { Id = DriveChannelId, Timeout = 0.5f },
            Properties = new ControlProperties { Weight = 8f, BooleanType = ControlBooleanType.Direct },
            Variables = new ControlVariables
            {
                steering = _steer,
                aforward = _throttle,
                abackward = _brake > 0f ? -_brake : 0f
            }
        });
    }

    private void PublishParkPulses()
    {
        Events.Current.Publish(OutputTopic, new ControlEvent
        {
            ChannelDefinition = new ControlChannelDefinition { Id = ParkChannelId, Timeout = 0.5f },
            Properties = new ControlProperties { Weight = 8f, BooleanType = ControlBooleanType.TrueToToggle },
            Variables = new ControlVariables
            {
                parkingbrake = _parkPulse,
                activate = _activatePulse,
                ignitionoff = _ignitionPulse
            }
        });
    }

    private void ReleaseOutput()
    {
        if (!_active && !_parkPulse && !_activatePulse && !_ignitionPulse) return;
        _active = false;
        _steer = 0f;
        _throttle = 0f;
        _brake = 0f;
        _parkPulse = _activatePulse = _ignitionPulse = false;

        // 1) 先发全零值，防止旧版 mmap 中布尔量残留
        Events.Current.Publish(OutputTopic, new ControlEvent
        {
            ChannelDefinition = new ControlChannelDefinition { Id = DriveChannelId, Timeout = 0.5f },
            Properties = new ControlProperties { Weight = 8f, BooleanType = ControlBooleanType.Direct },
            Variables = new ControlVariables { steering = 0f, aforward = 0f, abackward = 0f }
        });
        Events.Current.Publish(OutputTopic, new ControlEvent
        {
            ChannelDefinition = new ControlChannelDefinition { Id = ParkChannelId, Timeout = 0.5f },
            Properties = new ControlProperties { Weight = 8f, BooleanType = ControlBooleanType.TrueToToggle },
            Variables = new ControlVariables { parkingbrake = false, activate = false, ignitionoff = false }
        });
        // 2) 再删除通道
        Events.Current.Publish(OutputTopic, new ControlEvent
        {
            ChannelDefinition = new ControlChannelDefinition { Id = DriveChannelId },
            Variables = null!
        });
        Events.Current.Publish(OutputTopic, new ControlEvent
        {
            ChannelDefinition = new ControlChannelDefinition { Id = ParkChannelId },
            Variables = null!
        });
    }

    // ---------- 状态工具 ----------

    private void Abort(string reason, float cooldown)
    {
        ReleaseOutput();
        _planTask = null;
        SetIdle("已中止：" + reason);
        _cooldownUntil = _now + cooldown;
    }

    private void SetIdle(string status)
    {
        _state = PState.Idle;
        _stateTime = 0f;
        _status = status;
    }

    private void SetState(PState s, string status)
    {
        _state = s;
        _stateTime = 0f;
        _status = status;
    }

    // ---------- UI ----------

    private void RenderUI()
    {
        var cfg = _cfg;
        ImGui.Text($"状态：{_status}");
        ImGui.Text($"速度 {_speed:F1} m/s");
        if (_state == PState.Driving)
            ImGui.Text($"目标：{_targetDesc}　剩余 {_remaining:F0} m");
        if (_state == PState.Planning)
            ImGui.Text($"目标：{_targetDesc}");
        ImGui.Separator();

        bool dirty = false;
        dirty |= ImGui.Checkbox("启用自动停靠", ref cfg.Enabled);

        if (ImGui.Button("立即前往最近停靠点"))
            _manualTrigger = true;
        ImGui.SameLine();
        if (ImGui.Button("立即中止"))
        {
            if (_state is PState.Planning or PState.Driving or PState.Parking)
                Abort("手动中止", cfg.UserCooldown);
            else
                _status = "当前待机，无需中止";
        }
        ImGui.Separator();

        dirty |= ImGui.Checkbox("无目的地时自动前往", ref cfg.TriggerWhenNoDestination);
        dirty |= ImGui.Checkbox("缺油时自动前往", ref cfg.TriggerOnLowFuel);
        dirty |= ImGui.Checkbox("停稳后自动泊车 (Enter)", ref cfg.AutoActivate);
        dirty |= ImGui.Checkbox("停好后熄火", ref cfg.IgnitionOffAfterPark);
        dirty |= ImGui.SliderFloat("巡航速度 (m/s)", ref cfg.MaxSpeed, 15f, 30f);
        dirty |= ImGui.SliderFloat("低油阈值 (0~1)", ref cfg.FuelThreshold, 0.05f, 0.5f);
        dirty |= ImGui.SliderFloat("触发倒计时 (s)", ref cfg.TriggerDelaySeconds, 0f, 10f);
        dirty |= ImGui.SliderFloat("已在休息区旁距离 (m)", ref cfg.ParkedProximityMeters, 30f, 200f);
        dirty |= ImGui.SliderFloat("转向增益", ref cfg.SteeringGain, 0.01f, 0.15f);
        dirty |= ImGui.SliderFloat("最大转向", ref cfg.MaxSteering, 0.2f, 0.7f);
        dirty |= ImGui.SliderFloat("前瞻时间 (s)", ref cfg.LookaheadTime, 0.3f, 1.2f);
        dirty |= ImGui.SliderFloat("减速距离 (m)", ref cfg.SlowdownDistance, 5f, 30f);
        dirty |= ImGui.SliderFloat("停车距离 (m)", ref cfg.StopDistance, 4f, 20f);
        dirty |= ImGui.SliderFloat("再触发冷却 (s)", ref cfg.ReTriggerCooldown, 60f, 600f);

        if (dirty)
            _settingsDirty = true;
    }
}
