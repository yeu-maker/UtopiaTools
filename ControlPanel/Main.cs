using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(ControlPanel.Main), "Control Panel", "6.0.0", "UtopiaTools")]
[assembly: MelonGame("IncontinentCell", "My Dystopian Robot Girlfriend")]

namespace ControlPanel
{

    public class Main : MelonMod
    {
        private const string TAG = "[PANEL]";
        private static readonly string POSE_FILE = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "pose.txt");
        private static readonly string PANEL_FILE = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "panel.txt");
        private static readonly string CFG_FILE = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "utopia_menu.txt");

        private static bool _visible;
        private static int _page;
        private static float PX = 520f, PY = 140f;
        private const float PW = 500f;

        private static float _scale = 1f, _mz, _mx, _my;
        private static bool _dirty;

        private static Camera _cam;
        private static Transform _bot;
        private static Vector3 _botBase, _botScale;

        private static readonly (string key, string zh, string en, float min, float max, float def, string fmt)[] Page0 =
        {
            ("ParamArmRUp",      "右臂抬起", "Raise R arm",    0f, 1f, 0f, "0.00"),
            ("ParamArmRRot",     "右臂旋转", "Rotate R arm",  -1f, 1f, 0f, "0.00"),
            ("ParamArmRGesture", "右手手势", "R gesture",      1f, 3f, 1f, "0"),
            ("ParamArmLUp",      "左臂抬起", "Raise L arm",    0f, 1f, 0f, "0.00"),
            ("ParamArmLRot",     "左臂旋转", "Rotate L arm",  -1f, 1f, 0f, "0.00"),
            ("ParamArmLGesture", "左手手势", "L gesture",      1f, 3f, 1f, "0"),
            ("ParamHeadY",       "头 左右",  "Head L/R",      -1f, 1f, 0f, "0.00"),
            ("ParamHeadX",       "头 上下",  "Head U/D",      -1f, 1f, 0f, "0.00"),
            ("ParamHeadZ",       "头 侧倾",  "Head tilt",     -1f, 1f, 0f, "0.00"),
        };

        private static readonly (string key, string zh, string en, float min, float max, float def, string fmt)[] Page1 =
        {
            ("ParamEyeOpen",     "眼睛开合",   "Eye open",     -1f, 0f, 0f, "0.00"),
            ("ParamEyeRShock",   "右眼震惊",   "R eye shock",  -1f, 1f, 0f, "0.00"),
            ("ParamEyeLShock",   "左眼震惊",   "L eye shock",  -1f, 1f, 0f, "0.00"),
            ("ParamBrowRY",      "眉 右",      "Brow R",       -1f, 1f, 0f, "0.00"),
            ("ParamBrowLY",      "眉 左",      "Brow L",       -1f, 1f, 0f, "0.00"),
            ("ParamBrowRRot",    "右眉角度",   "Brow R rot",  -30f, 30f, 0f, "0"),
            ("ParamBrowLRot",    "左眉角度",   "Brow L rot",  -30f, 30f, 0f, "0"),
            ("ParamMouthOpen",   "张嘴",       "Mouth open",   -1f, 2f, 0f, "0.00"),
            ("ParamMouthForm",   "嘴角",       "Mouth form",   -1f, 1f, 0f, "0.00"),
            ("ParamIrisZoom",    "瞳孔缩放",   "Iris zoom",    -1f, 1f, 0f, "0.00"),
            ("ParamBlush",       "脸红强度",   "Blush",         0f, 2f, 0f, "0.00"),
            ("ParamHeart",       "爱心眼",     "Heart eyes",    0f, 1f, 0f, "0.00"),
        };

        private static readonly Dictionary<string, float> Values = new Dictionary<string, float>();
        private static readonly HashSet<string> _touched = new HashSet<string>();
        private static bool _poseDirty;
        private static DateTime _lastPoseWrite = DateTime.MinValue;

        private static object _kb;
        private static PropertyInfo _f9;
        private static GUIStyle _label, _title;
        private static bool _guiErrLogged, _eventErrLogged, _inputErrLogged;
        private static string _dragId;
        private static bool _eventWorks = true;
        private static bool _en;
        private static object _eventSystem;
        private static bool _gameUiDisabled;
        private static DateTime _lastCfgCheck = DateTime.MinValue;

        private static void Log(string m) { try { MelonLogger.Msg(TAG + " " + m); } catch { } }
        private static void Warn(string m) { try { MelonLogger.Warning(TAG + " " + m); } catch { } }
        private static string L(string zh, string en) => _en ? en : zh;

        public override void OnInitializeMelon()
        {
            foreach (var p in Page0) Values[p.key] = p.def;
            foreach (var p in Page1) Values[p.key] = p.def;
            LoadPoseFile();
            ReloadConfig();
            Log("loaded v6.0 — 2 pages, lang=" + (_en ? "EN" : "ZH"));
            MelonCoroutines.Start(BindLoop());
        }

        private static void ReloadConfig()
        {
            try
            {
                if (!File.Exists(CFG_FILE)) return;
                foreach (var raw in File.ReadAllLines(CFG_FILE))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("lang=en")) _en = true;
                    else if (line.StartsWith("lang=zh")) _en = false;
                    else if (line.StartsWith("panelx="))
                    {
                        if (float.TryParse(line.Substring(7), NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
                            PX = Math.Max(0f, Math.Min(Screen.width - 200f, px));
                    }
                    else if (line.StartsWith("panely="))
                    {
                        if (float.TryParse(line.Substring(7), NumberStyles.Float, CultureInfo.InvariantCulture, out var py))
                            PY = Math.Max(0f, Math.Min(Screen.height - 200f, py));
                    }
                }
            }
            catch { }
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
                    var p = t.GetProperties(SF).FirstOrDefault(z => z.Name == n && z.GetIndexParameters().Length == 0);
                    if (p != null) { try { return p.GetValue(null); } catch { } }
                    var f = t.GetFields(SF).FirstOrDefault(z => z.Name == n);
                    if (f != null) { try { return f.GetValue(null); } catch { } }
                }
                catch { }
            }
            return null;
        }

        private static object InvokeFlexible(object obj, string name, params object[] provided)
        {
            var t = obj.GetType();
            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == name).ToList();
            if (methods.Count == 0) throw new MissingMethodException(t.Name, name);
            var m = methods.Where(z => z.GetParameters().Length >= provided.Length)
                           .OrderBy(z => z.GetParameters().Length).FirstOrDefault()
                    ?? methods.OrderBy(z => z.GetParameters().Length).First();
            var ps = m.GetParameters();
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (i < provided.Length) args[i] = provided[i];
                else
                {
                    var pt = ps[i].ParameterType;
                    if (pt == typeof(int)) args[i] = 0;
                    else if (pt == typeof(float)) args[i] = 0f;
                    else if (pt == typeof(bool)) args[i] = false;
                    else args[i] = null;
                }
            }
            return m.Invoke(obj, args);
        }

        private IEnumerator BindLoop()
        {
            Log("waiting for save…");
            var muType = FindType("ModUtilities");
            var gvType = FindType("GameVariables");
            var kbType = FindType("Keyboard");
            object gv = null;

            while (true)
            {
                if (gv == null && muType != null)
                {
                    var mu = GetStatic(muType, "Instance", "LuaInstance", "_instance");
                    if (mu != null) { try { gv = InvokeFlexible(mu, "GetGameVariables"); } catch { } }
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

                if (gv != null && _bot == null)
                {
                    TryBind();
                    if (_bot != null)
                    {
                        if (kbType != null) { _kb = GetStatic(kbType, "current"); _f9 = kbType.GetProperty("f9Key"); }
                        Log("bound, press F9");
                    }
                }
                yield return null;
            }
        }

        private void TryBind()
        {
            try
            {
                _cam = Camera.main;
                var ctrl = GetController();
                if (ctrl == null) return;
                var go = ctrl.GetType().GetProperty("gameObject", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ctrl) as GameObject;
                if (go == null) return;
                _bot = go.transform;
                _botBase = _bot.position;
                _botScale = _bot.localScale;
                Log("base recorded");
            }
            catch (Exception e) { Warn("bind failed: " + (e.InnerException ?? e).Message); }
        }

        private static object GetController()
        {
            try
            {
                var singType = FindType("Live2DControllerSingleton");
                if (singType == null) return null;
                var single = GetStatic(singType, "Instance", "LuaInstance", "_instance");
                if (single == null) return null;
                return InvokeFlexible(single, "GetReadyController", "bot");
            }
            catch { return null; }
        }

        private static void LoadPoseFile()
        {
            try
            {
                if (!File.Exists(POSE_FILE)) return;
                foreach (var raw in File.ReadAllLines(POSE_FILE))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    if (Values.ContainsKey(key)
                        && float.TryParse(line.Substring(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                    {
                        Values[key] = f;
                        _touched.Add(key);
                    }
                }
            }
            catch { }
        }

        private static void SavePoseFile()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                
                sb.AppendLine("speed = 0.3");
                sb.AppendLine("exprtime = 0.6");
                sb.AppendLine("exprhold = 3600");
                sb.AppendLine();
                foreach (var p in Page0) WriteIfTouched(sb, p.key, p.def);
                foreach (var p in Page1) WriteIfTouched(sb, p.key, p.def);

                if (File.Exists(POSE_FILE))
                    foreach (var raw in File.ReadAllLines(POSE_FILE))
                        if (raw.Trim().StartsWith("expr ", StringComparison.OrdinalIgnoreCase)) sb.AppendLine(raw.Trim());

                File.WriteAllText(POSE_FILE, sb.ToString());
                _lastPoseWrite = DateTime.UtcNow;
                _poseDirty = false;
            }
            catch (Exception e) { Warn("write pose failed: " + e.Message); }
        }

        private static void WriteIfTouched(System.Text.StringBuilder sb, string key, float def)
        {
            if (!_touched.Contains(key)) return;
            var v = Values.TryGetValue(key, out var x) ? x : def;
            sb.AppendLine($"{key} = {v.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        public override void OnLateUpdate()
        {
            bool toggle = false;

            if (_kb != null && _f9 != null)
            {
                try
                {
                    var key = _f9.GetValue(_kb);
                    if (key != null)
                    {
                        var pi = key.GetType().GetProperty("wasPressedThisFrame");
                        if (pi != null && Convert.ToBoolean(pi.GetValue(key))) toggle = true;
                    }
                }
                catch { }
            }
            if (!toggle)
            {
                try { if (Input.GetKeyDown(KeyCode.F9)) toggle = true; } catch { }
            }
            try { if (File.Exists(PANEL_FILE)) { File.Delete(PANEL_FILE); toggle = true; } } catch { }

            if (toggle)
            {
                _visible = !_visible;
                SetGameUiBlocked(_visible);
                Log("panel " + (_visible ? "on" : "off"));
            }

            if ((DateTime.UtcNow - _lastCfgCheck).TotalSeconds > 1.0)
            {
                _lastCfgCheck = DateTime.UtcNow;
                ReloadConfig();
            }

            if (_poseDirty && (DateTime.UtcNow - _lastPoseWrite).TotalSeconds > 0.15) SavePoseFile();

            if (!_visible && _gameUiDisabled) SetGameUiBlocked(false);

            if (!_dirty || _bot == null) return;
            try
            {
                Vector3 fwd = Vector3.forward, right = Vector3.right, up = Vector3.up;
                if (_cam != null)
                {
                    fwd = (_cam.transform.position - _bot.position).normalized;
                    right = _cam.transform.right;
                    up = _cam.transform.up;
                }
                _bot.position = _botBase + fwd * _mz + right * _mx + up * _my;
                _bot.localScale = _botScale * _scale;
            }
            catch { }
        }

        public override void OnGUI()
        {
            if (!_visible) return;
            if (_bot == null) return;

            try
            {
                if (_label == null)
                {
                    _label = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = false };
                    _label.normal.textColor = Color.white;
                    _title = new GUIStyle(GUI.skin.label) { fontSize = 14 };
                    _title.normal.textColor = Color.white;
                }

                float ph = Math.Min(660f, Screen.height - PY - 40f);
                float viewTop = PY + 30f;
                float viewH = ph - 34f;

                var old = GUI.color;
                GUI.color = new Color(0.03f, 0.03f, 0.06f, 0.94f);
                GUI.Box(new Rect(PX, PY, PW, ph), "");
                GUI.color = old;

                GUI.Label(new Rect(PX + 10, PY + 5, PW - 20, 22),
                    L("控制面板（F9 关闭）", "Control Panel (F9 close)"), _title);

                float y = viewTop;

                if (_page == 0)
                {
                    GUI.Label(new Rect(PX + 10, y, PW - 26, 18), L("── 姿势 ──", "── Pose ──"), _label);
                    y += 20f;
                    foreach (var p in Page0) SliderRow(ref y, viewTop, viewH, p);

                    GUI.Label(new Rect(PX + 10, y, PW - 26, 18), L("── 模型位置 ──", "── Model ──"), _label);
                    y += 20f;

                    _scale = SliderRaw(ref y, viewTop, viewH, L("模型缩放", "Model scale"), _scale, 0.2f, 5f);
                    _mz = SliderRaw(ref y, viewTop, viewH, L("模型 前后", "Model fwd/back"), _mz, -8f, 8f);
                    _mx = SliderRaw(ref y, viewTop, viewH, L("模型 左右", "Model L/R"), _mx, -8f, 8f);
                    _my = SliderRaw(ref y, viewTop, viewH, L("模型 上下", "Model U/D"), _my, -6f, 6f);

                    if (Math.Abs(_scale - 1f) + Math.Abs(_mz) + Math.Abs(_mx) + Math.Abs(_my) > 0.001f) _dirty = true;
                }
                else
                {
                    GUI.Label(new Rect(PX + 10, y, PW - 26, 18), L("── 表情微调 ──", "── Expression ──"), _label);
                    y += 20f;
                    foreach (var p in Page1) SliderRow(ref y, viewTop, viewH, p);
                }

                float bw = (PW - 40f) / 3f;
                if (Button(new Rect(PX + 10f, y, bw, 24f), L("上一页", "Prev page")))
                    _page = Math.Max(0, _page - 1);
                if (Button(new Rect(PX + 10f + bw + 5f, y, bw, 24f), L("下一页", "Next page")))
                    _page = Math.Min(1, _page + 1);
                if (Button(new Rect(PX + 10f + (bw + 5f) * 2, y, bw, 24f), L("全部复位", "Reset all")))
                    ResetAll();

                y += 30f;
                GUI.Label(new Rect(PX + 10, y, PW - 26, 18),
                    L("第 ", "Page ") + (_page + 1) + L(" / 2 页", " / 2"), _label);
            }
            catch (Exception e)
            {
                if (!_guiErrLogged) { _guiErrLogged = true; Warn("draw failed: " + (e.InnerException ?? e).Message); }
            }
        }

        private static void SliderRow(ref float y, float viewTop, float viewH,
            (string key, string zh, string en, float min, float max, float def, string fmt) p)
        {
            float cur = Values.TryGetValue(p.key, out var v) ? v : p.def;
            float nv = SliderRaw(ref y, viewTop, viewH, L(p.zh, p.en), cur, p.min, p.max, p.fmt);
            if (Math.Abs(nv - cur) > 0.0001f)
            {
                Values[p.key] = nv;
                _touched.Add(p.key);
                _poseDirty = true;
            }
        }

        private static void SetGameUiBlocked(bool blocked)
        {
            try
            {
                if (_eventSystem == null)
                {
                    var esType = FindType("EventSystem");
                    if (esType == null) { Warn("no EventSystem type"); return; }
                    _eventSystem = GetStatic(esType, "current");
                }
                if (_eventSystem == null) return;

                var enabledProp = _eventSystem.GetType().GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance);
                if (enabledProp == null) return;

                enabledProp.SetValue(_eventSystem, !blocked);
                _gameUiDisabled = blocked;
                Log(blocked ? "game UI events blocked" : "game UI events restored");
            }
            catch (Exception e) { Warn("block ui failed: " + (e.InnerException ?? e).Message); }
        }

        private static bool Button(Rect r, string label)
        {
            GUI.Box(r, "");
            GUI.Label(new Rect(r.x + 6, r.y + 4, r.width - 12, r.height - 8), label, _label);

            try
            {
                var e = Event.current;
                if (e != null && e.type == EventType.MouseDown && r.Contains(e.mousePosition))
                    return true;
            }
            catch { }

            try
            {
                if (Input.GetMouseButtonDown(0))
                {
                    var mp = Input.mousePosition;
                    var mpos = new Vector2(mp.x, Screen.height - mp.y);
                    if (r.Contains(mpos)) return true;
                }
            }
            catch { }
            return false;
        }

        private static float SliderRaw(ref float y, float viewTop, float viewH, string label, float value, float min, float max, string fmt = "0.00")
        {
            string id = label;
            float tx = PX + 10f, tw = PW - 30f;
            bool visible = (y + 38f > viewTop) && (y < viewTop + viewH);

            if (!visible) { y += 38f; return value; }

            GUI.Label(new Rect(tx, y, tw, 18), label + "  " + value.ToString(fmt), _label);

            var track = new Rect(tx, y + 20f, tw, 10f);
            GUI.Box(track, "");
            float t = Mathf.Clamp01((value - min) / (max - min));
            var oc = GUI.color;
            GUI.color = new Color(0.55f, 0.45f, 0.85f, 0.95f);
            GUI.Box(new Rect(track.x, track.y, track.width * t, track.height), "");
            GUI.color = new Color(0.95f, 0.95f, 1f, 1f);
            GUI.Box(new Rect(track.x + track.width * t - 4f, track.y - 3f, 8f, track.height + 6f), "");
            GUI.color = oc;

            float nv = DragValue(track, min, max, value, id);
            y += 38f;
            return nv;
        }

        private static float DragValue(Rect track, float min, float max, float value, string id)
        {
            if (_eventWorks)
            {
                try
                {
                    var e = Event.current;
                    if (e != null)
                    {
                        var type = e.type;
                        if (type == EventType.MouseDown && track.Contains(e.mousePosition)) _dragId = id;
                        if (type == EventType.MouseUp) _dragId = null;
                        if ((type == EventType.MouseDown || type == EventType.MouseDrag) && _dragId == id)
                        {
                            float t = Mathf.Clamp01((e.mousePosition.x - track.x) / track.width);
                            return Mathf.Lerp(min, max, t);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _eventWorks = false;
                    if (!_eventErrLogged) { _eventErrLogged = true; Warn("Event.current unavailable: " + ex.Message); }
                }
            }

            try
            {
                if (Input.GetMouseButton(0))
                {
                    var mp = Input.mousePosition;
                    var mpos = new Vector2(mp.x, Screen.height - mp.y);
                    if (Input.GetMouseButtonDown(0) && track.Contains(mpos)) _dragId = id;
                    if (_dragId == id && track.Contains(mpos))
                    {
                        float t = Mathf.Clamp01((mpos.x - track.x) / track.width);
                        return Mathf.Lerp(min, max, t);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_inputErrLogged) { _inputErrLogged = true; Warn("Input unavailable: " + ex.Message); }
            }
            return value;
        }

        private static void ResetAll()
        {
            try
            {
                if (_bot != null)
                {
                    _bot.position = _botBase;
                    _bot.localScale = _botScale;
                }
            }
            catch { }
            _scale = 1f; _mz = 0f; _mx = 0f; _my = 0f;
            _dirty = false;

            _touched.Clear();
            foreach (var p in Page0) Values[p.key] = p.def;
            foreach (var p in Page1) Values[p.key] = p.def;

            try
            {
                File.WriteAllText(POSE_FILE,
                    "speed = 0.3\nexprtime = 0.6\nexprhold = 3600\n");
            }
            catch { }
            _poseDirty = false;
            Log("all reset");
        }
    }
}
