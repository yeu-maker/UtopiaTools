using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using MelonLoader;

[assembly: MelonInfo(typeof(LiveTuner.Main), "Live Tuner", "2.0.0", "UtopiaTools")]
[assembly: MelonGame("IncontinentCell", "My Dystopian Robot Girlfriend")]

namespace LiveTuner
{

    public class Main : MelonMod
    {
        private const string TAG = "[TUNER]";
        private static readonly string POSE_FILE = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "pose.txt");
        private static readonly string PARAM_REF = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "pose_params.txt");

        private static Dictionary<string, object> _params = new Dictionary<string, object>();
        private static Dictionary<string, float> _defaults = new Dictionary<string, float>();
        private static object _model;
        private static object _ctrl;

        private static readonly Dictionary<string, float> Target = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> Current = new Dictionary<string, float>();
        private static readonly HashSet<string> Touched = new HashSet<string>();
        private static readonly List<(string id, bool on)> PendingDrawables = new List<(string, bool)>();

        private static float _tau = 0.15f;
        private static float _exprTime = 0.6f;
        private static float _exprHold = 3600f;
        private static bool _hadExpr;
        private static readonly Dictionary<string, float> _exprTargets = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _exprAppliedAt = DateTime.MinValue;

        private static void ApplyExprTargets(object expr, Type enumType)
        {
            if (expr == null || enumType == null || _exprTargets.Count == 0) return;
            foreach (var kv in _exprTargets)
            {
                try
                {
                    var ev = Enum.Parse(enumType, kv.Key, true);
                    Call(expr, "AddModifier", ev, kv.Value, _exprTime);
                }
                catch { }
            }
            _exprAppliedAt = DateTime.UtcNow;
        }
        private static DateTime _lastWrite = DateTime.MinValue;
        private static DateTime _lastCheck = DateTime.MinValue;
        private static DateTime _lastFrame = DateTime.UtcNow;
        private static DateTime _lastRebind = DateTime.MinValue;
        private static bool _ready;
        private static readonly HashSet<string> _appliedDrawables = new HashSet<string>();
        private static Dictionary<string, (float min, float max)> _ranges = new Dictionary<string, (float, float)>();
        private static readonly Dictionary<string, (string slot, string item)> _emoteMap =
            new Dictionary<string, (string slot, string item)>(StringComparer.OrdinalIgnoreCase);

        private static float Clamp(string id, float v)
        {
            if (!_ranges.TryGetValue(id, out var r)) return v;
            if (v < r.min) return r.min;
            if (v > r.max) return r.max;
            return v;
        }

        private static void Log(string m) { try { MelonLogger.Msg(TAG + " " + m); } catch { } }
        private static void Warn(string m) { try { MelonLogger.Warning(TAG + " " + m); } catch { } }

        public override void OnInitializeMelon()
        {
            Log("loaded v2.0 (缓动版) — 改 " + POSE_FILE + " 保存即生效");
            MelonCoroutines.Start(InitLoop());
        }

        private static IEnumerable<Type> AllTypes()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] ts;
                try { ts = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { ts = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                foreach (var t in ts) yield return t;
            }
        }

        private static Type FindType(string n) =>
            AllTypes().FirstOrDefault(t => t.FullName == n) ?? AllTypes().FirstOrDefault(t => t.Name == n);

        private static object GetStatic(Type t, params string[] names)
        {
            const BindingFlags SF = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            foreach (var n in names)
            {

                try
                {
                    var p = t.GetProperties(SF).FirstOrDefault(x => x.Name == n && x.GetIndexParameters().Length == 0);
                    if (p != null) { try { return p.GetValue(null); } catch { } }
                }
                catch { }
                try
                {
                    var f = t.GetFields(SF).FirstOrDefault(x => x.Name == n);
                    if (f != null) { try { return f.GetValue(null); } catch { } }
                }
                catch { }
            }
            return null;
        }

        private static object GetMember(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) { try { return p.GetValue(o); } catch { } }
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) { try { return f.GetValue(o); } catch { } }
            return null;
        }

        private static void SetMember(object o, string name, object v)
        {
            var t = o.GetType();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) { p.SetValue(o, v); return; }
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) { f.SetValue(o, v); return; }
            throw new MissingMemberException(t.Name, name);
        }

        private static object Call(object o, string name, params object[] args)
        {
            var m = o.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                     .FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length);
            if (m == null) throw new MissingMethodException(o.GetType().Name, name);
            return m.Invoke(o, args);
        }

        private static List<object> Enumerate(object o)
        {
            var list = new List<object>();
            if (o == null) return list;
            if (o is IEnumerable en) { foreach (var x in en) list.Add(x); return list; }
            var t = o.GetType();
            var len = t.GetProperty("Length") ?? t.GetProperty("Count");
            var idx = t.GetProperty("Item");
            if (len != null && idx != null)
            {
                int n = Convert.ToInt32(len.GetValue(o));
                for (int i = 0; i < n; i++) list.Add(idx.GetValue(o, new object[] { i }));
            }
            return list;
        }

        private static object FindUnityObject(Type t)
        {
            var ot = FindType("UnityEngine.Object");
            if (ot == null) return null;
            var g = ot.GetMethods(BindingFlags.Public | BindingFlags.Static)
                      .FirstOrDefault(m => m.Name == "FindObjectOfType" && m.IsGenericMethod);
            if (g == null) return null;
            return g.MakeGenericMethod(t).Invoke(null,
                g.GetParameters().Select(p => p.ParameterType == typeof(bool) ? (object)false : null).ToArray());
        }

        private IEnumerator InitLoop()
        {
            Log("等待读档…");
            var muType = FindType("ModUtilities");
            var gvType = FindType("GameVariables");

            while (true)
            {
                object gv = null;
                if (muType != null)
                {
                    var mu = GetStatic(muType, "Instance", "LuaInstance", "_instance") ?? FindUnityObject(muType);
                    if (mu != null) { try { gv = Call(mu, "GetGameVariables"); } catch { } }
                }
                if (gv == null && gvType != null)
                {
                    try
                    {
                        var p = gvType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
                        if (p != null) gv = p.GetValue(null);
                    }
                    catch { }
                }

                if (gv != null && !_ready)
                {
                    if (BindController())
                    {
                        _ready = true;
                        Log("已绑定模型，监听 " + POSE_FILE);
                        ReloadFile(force: true);
                    }
                }
                yield return null;
            }
        }

        private bool BindController()
        {
            var singType = FindType("Live2DControllerSingleton");
            if (singType == null) { Warn("找不到 singleton"); return false; }
            var singleton = GetStatic(singType, "Instance", "LuaInstance", "_instance") ?? FindUnityObject(singType);
            if (singleton == null) return false;
            try { _ctrl = Call(singleton, "GetReadyController", "bot"); } catch { }
            if (_ctrl == null) return false;
            _model = GetMember(_ctrl, "Model");
            if (_model == null) return false;

            _params.Clear();
            _defaults.Clear();
            _ranges.Clear();
            foreach (var p in Enumerate(GetMember(_model, "Parameters")))
            {
                var id = GetMember(p, "Id")?.ToString();
                if (string.IsNullOrEmpty(id)) continue;
                _params[id] = p;
                try { _defaults[id] = Convert.ToSingle(GetMember(p, "DefaultValue")); } catch { _defaults[id] = 0f; }
                try
                {
                    float mn = Convert.ToSingle(GetMember(p, "MinimumValue"));
                    float mx = Convert.ToSingle(GetMember(p, "MaximumValue"));
                    _ranges[id] = (mn, mx);
                }
                catch { }
            }
            Current.Clear();
            Touched.Clear();
            Log("参数 " + _params.Count + " 个已就绪（带缓动，含范围夹取）");
            DumpEmotes();
            WriteParamReference();
            return true;
        }

        private void DumpEmotes()
        {
            try
            {
                _emoteMap.Clear();
                var mgr = Call(_ctrl, "GetStoryEmoteManager");
                if (mgr == null) { Warn("GetStoryEmoteManager 返回 null"); return; }
                var items = Enumerate(GetMember(mgr, "AllItems"));
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# 游戏内可用表情（slotId / itemId）");
                sb.AppendLine("# 用法：pose.txt 里写  emote = <ItemId>");
                sb.AppendLine();
                foreach (var it in items)
                {
                    string slot = GetMember(it, "SlotId")?.ToString();
                    string item = GetMember(it, "ItemId")?.ToString();
                    if (slot == null && item == null)
                    {
                        slot = it?.ToString();
                        item = "";
                    }
                    sb.AppendLine($"{slot}\t{item}");
                    if (!string.IsNullOrEmpty(item))
                        _emoteMap[item] = (slot ?? "Expression", item);
                }
                File.WriteAllText(System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "emotes_dump.txt"), sb.ToString());
                Log($"表情清单 {_emoteMap.Count} 条 → D:\\MDRG\\emotes_dump.txt");
                if (_emoteMap.Count > 0)
                    Log("可用 itemId 示例: " + string.Join(", ", _emoteMap.Keys.Take(12)));
            }
            catch (Exception e) { Warn("导出表情失败: " + (e.InnerException ?? e).Message); }
        }

        private void ApplyEmote(string name)
        {
            try
            {
                string slot = "Expression";
                string item = name;
                if (_emoteMap.TryGetValue(name, out var hit)) { slot = hit.slot; item = hit.item; }

                var mgr = Call(_ctrl, "GetStoryEmoteManager");
                if (mgr == null) { Warn("没有 StoryEmoteManager"); return; }

                var emoteType = FindType("EmoteData");
                if (emoteType == null) { Warn("找不到 EmoteData 类型"); return; }

                object data = null;
                var ctors = emoteType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

                var c3 = ctors.FirstOrDefault(c =>
                {
                    var p = c.GetParameters();
                    return p.Length == 3 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(string);
                });
                var c2 = ctors.FirstOrDefault(c =>
                {
                    var p = c.GetParameters();
                    return p.Length == 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(string);
                });
                if (c3 != null) data = c3.Invoke(new object[] { slot, item, true });
                else if (c2 != null) data = c2.Invoke(new object[] { slot, item });
                if (data == null) { Warn("构造 EmoteData 失败（没有 string,string 构造）"); return; }

                Call(mgr, "SetEmote", data);
                Log($"表情 → {slot} / {item}");
            }
            catch (Exception e) { Warn("应用表情 " + name + " 失败: " + (e.InnerException ?? e).Message); }
        }

        private void TryRebind()
        {
            try
            {
                var singType = FindType("Live2DControllerSingleton");
                if (singType == null) return;
                var singleton = GetStatic(singType, "Instance", "LuaInstance", "_instance") ?? FindUnityObject(singType);
                if (singleton == null) return;
                var ctrl = Call(singleton, "GetReadyController", "bot");
                if (ctrl == null) return;
                if (ReferenceEquals(ctrl, _ctrl)) return;
                Log(">>> 检测到控制器变化（可能进了互动场景），重新绑定");
                _ctrl = ctrl;
                _model = GetMember(ctrl, "Model");
                if (_model == null) { Warn("新 controller 的 Model 为 null"); return; }
                if (BindController()) ReloadFile(force: true);
            }
            catch { }
        }

        private void WriteParamReference()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# MDRG Live2D 参数参考（自动生成）");
                sb.AppendLine("# 参数名 = 数值      部件名 = on/off");
                sb.AppendLine("# 可选：单独一行  # speed = 0.15   （缓动时间常数，秒。越小越快）");
                sb.AppendLine();
                foreach (var kv in _params)
                {
                    var p = kv.Value;
                    float mn = Convert.ToSingle(GetMember(p, "MinimumValue"));
                    float mx = Convert.ToSingle(GetMember(p, "MaximumValue"));
                    float df = Convert.ToSingle(GetMember(p, "DefaultValue"));
                    sb.AppendLine($"{kv.Key}\t[{mn:0.###} .. {mx:0.###}]\t默认 {df:0.###}");
                }
                File.WriteAllText(PARAM_REF, sb.ToString());
            }
            catch (Exception e) { Warn("写参考表失败: " + e.Message); }
        }

        private void ReloadFile(bool force = false)
        {
            try
            {
                if (!File.Exists(POSE_FILE)) return;
                var mtime = File.GetLastWriteTimeUtc(POSE_FILE);
                if (!force && mtime == _lastWrite) return;
                _lastWrite = mtime;

                Target.Clear();
                PendingDrawables.Clear();
                var exprRequests = new List<(string channel, float value)>();
                var emoteRequests = new List<string>();
                int unknown = 0;

                foreach (var raw in File.ReadAllLines(POSE_FILE))
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;

                    if (line.StartsWith("#"))
                    {
                        var body = line.TrimStart('#').Trim();
                        if (body.StartsWith("speed", StringComparison.OrdinalIgnoreCase))
                        {
                            int eqs = body.IndexOf('=');
                            if (eqs > 0 && float.TryParse(body.Substring(eqs + 1).Trim(),
                                    NumberStyles.Float, CultureInfo.InvariantCulture, out var sp) && sp > 0.01f)
                            {
                                _tau = sp;
                                Log("缓动时间常数 → " + sp + " 秒");
                            }
                        }
                        continue;
                    }

                    if (line.StartsWith("emote", StringComparison.OrdinalIgnoreCase))
                    {
                        var namePart = line.Substring(5).Trim();
                        if (namePart.StartsWith("=")) namePart = namePart.Substring(1).Trim();
                        if (namePart.Length > 0) emoteRequests.Add(namePart);
                        continue;
                    }

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    var val = line.Substring(eq + 1).Trim();

                    if (key.Equals("speed", StringComparison.OrdinalIgnoreCase))
                    {
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var sp2) && sp2 > 0.01f)
                        {
                            _tau = sp2;
                            Log("缓动时间常数 → " + sp2 + " 秒");
                        }
                        continue;
                    }

                    if (key.Equals("exprhold", StringComparison.OrdinalIgnoreCase))
                    {
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var eh) && eh > 0f)
                        {
                            _exprHold = eh;
                            Log("表情保持时间 → " + eh + " 秒");
                        }
                        continue;
                    }

                    if (key.Equals("exprtime", StringComparison.OrdinalIgnoreCase))
                    {
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var et) && et > 0.01f)
                        {
                            _exprTime = et;
                            Log("表情过渡时间 → " + et + " 秒");
                        }
                        continue;
                    }

                    if (key.StartsWith("expr", StringComparison.OrdinalIgnoreCase))
                    {
                        var ch = key.Substring(4).Trim();
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var ev))
                            exprRequests.Add((ch, ev));
                        else unknown++;
                        continue;
                    }

                    if (_params.ContainsKey(key))
                    {
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                        {

                            f = Clamp(key, f);
                            Target[key] = f;
                        }
                        else unknown++;
                        continue;
                    }

                    bool on = val.Equals("on", StringComparison.OrdinalIgnoreCase) || val == "1"
                           || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                    PendingDrawables.Add((key, on));
                }

                Log($"已载入 {Target.Count} 个参数（缓动 {_tau:0.00}s）+ {PendingDrawables.Count} 个部件" +
                    (unknown > 0 ? $"（{unknown} 行数值非法）" : ""));

                var newIds = new HashSet<string>(PendingDrawables.Select(x => x.id));
                foreach (var oldId in _appliedDrawables)
                {
                    if (newIds.Contains(oldId)) continue;
                    try { Call(_ctrl, "ToggleDrawable", oldId, true); }
                    catch { }
                }
                _appliedDrawables.Clear();
                foreach (var (id, on) in PendingDrawables)
                {
                    try
                    {
                        Call(_ctrl, "ToggleDrawable", id, on);
                        _appliedDrawables.Add(id);
                    }
                    catch (Exception e) { Warn("部件 " + id + ": " + (e.InnerException ?? e).Message); }
                }

                foreach (var emo in emoteRequests) ApplyEmote(emo);

                var enumType = FindType("ExpressionModifierTypeEnum");
                var expr = GetMember(_ctrl, "Expression");

                if (exprRequests.Count == 0)
                {
                    if (_hadExpr)
                    {
                        try { Call(expr, "ClearExpression"); Log("表情已复位（文件里没有 expr 行了）"); }
                        catch { }
                        _hadExpr = false;
                    }
                }
                else if (enumType == null) Warn("找不到 ExpressionModifierTypeEnum");
                else if (expr == null) Warn("controller.Expression 为 null");
                else
                {
                    try { Call(expr, "ClearExpression"); } catch { }
                    foreach (var (ch, ev) in exprRequests)
                    {
                        if (ev <= 0.0001f) continue;
                        try
                        {
                            var ev2 = Enum.Parse(enumType, ch, true);

                            Call(expr, "AddModifier", ev2, ev, _exprHold);
                            Log($"表情通道 {ch} = {ev}（保持 {_exprHold}s）");
                        }
                        catch (Exception e)
                        {
                            Warn("表情通道 " + ch + " 失败: " + (e.InnerException ?? e).Message);
                        }
                    }
                    _hadExpr = true;
                }
            }
            catch (Exception e) { Warn("读 pose.txt 失败: " + e.Message); }
        }

        public override void OnLateUpdate()
        {
            if (!_ready) return;

            var now = DateTime.UtcNow;
            float dt = (float)(now - _lastFrame).TotalSeconds;
            _lastFrame = now;
            if (dt <= 0f || dt > 0.5f) dt = 0.016f;

            if ((now - _lastCheck).TotalSeconds > 0.25)
            {
                _lastCheck = now;
                ReloadFile();
            }

            if ((now - _lastRebind).TotalSeconds > 2)
            {
                _lastRebind = now;
                TryRebind();
            }

            if (Target.Count == 0 && Touched.Count == 0) return;
            float k = 1f - (float)Math.Exp(-dt / _tau);
            if (k < 0f) k = 0f;
            if (k > 1f) k = 1f;

            bool anyChange = false;

            foreach (var kv in Target)
            {
                if (!_params.ContainsKey(kv.Key)) continue;
                Touched.Add(kv.Key);
                if (!Current.TryGetValue(kv.Key, out var cur)) cur = kv.Value;
                float nv = cur + (kv.Value - cur) * k;
                if (Math.Abs(kv.Value - nv) < 0.0005f) nv = kv.Value;
                Current[kv.Key] = nv;
                try { SetMember(_params[kv.Key], "Value", nv); anyChange = true; } catch { }
            }

            foreach (var id in Touched.ToList())
            {
                if (Target.ContainsKey(id)) continue;
                if (!_params.ContainsKey(id)) continue;
                float def = _defaults.TryGetValue(id, out var d) ? d : 0f;
                if (!Current.TryGetValue(id, out var cur)) cur = def;
                float nv = cur + (def - cur) * k;
                if (Math.Abs(def - nv) < 0.0005f)
                {
                    Current[id] = def;
                    Touched.Remove(id);
                }
                else
                {
                    Current[id] = nv;
                }
                try { SetMember(_params[id], "Value", nv); anyChange = true; } catch { }
            }

            if (anyChange)
            {
                try { Call(_model, "ForceUpdateNow"); } catch { }
            }
        }
    }
}
