using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using MelonLoader;
using HarmonyLib;
using UnityEngine;

[assembly: MelonInfo(typeof(MenuMod.Main), "Utopia Menu", "3.1.0", "UtopiaTools")]
[assembly: MelonGame("IncontinentCell", "My Dystopian Robot Girlfriend")]

namespace MenuMod
{

    public class Main : MelonMod
    {
        private const string TAG = "[MENU]";
        private static readonly string POSE_FILE = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "pose.txt");
        private static readonly string SPAWN_FILE = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "spawn.txt");
        private static readonly string SAY_FILE = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "say.txt");
        private static readonly string CFG_FILE = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "utopia_menu.txt");

        private static object _ui, _script, _gv, _kb;
        private static PropertyInfo _f8;
        private static bool _ready, _menuOpen;
        private static bool _en;

        private static Type _itemEnumType, _itemType, _gameUtilsType;
        private static readonly List<object> _keepAlive = new List<object>();

        private static void Log(string m) { try { MelonLogger.Msg(TAG + " " + m); } catch { } }
        private static void Warn(string m) { try { MelonLogger.Warning(TAG + " " + m); } catch { } }

        private static string L(string zh, string en) => _en ? en : zh;

        public override void OnInitializeMelon()
        {
            LoadConfig();
            Log("loaded v3.1 — F8 menu, lang=" + (_en ? "EN" : "ZH") + ", say scope gate ON");
            MelonCoroutines.Start(InitLoop());
            MelonCoroutines.Start(StateProbe());
        }

        // 诊断：启动早期打两行当前游戏状态。
        // 目的：日志里出现 state probe 说明「读状态」这条链路在原版游戏上也通；
        // 若显示「未知」，就是游戏版本对不上，而不是门控写错了。
        private static IEnumerator StateProbe()
        {
            var t0 = DateTime.UtcNow;
            foreach (var sec in new[] { 5f, 20f })
            {
                while ((DateTime.UtcNow - t0).TotalSeconds < sec) yield return null;
                Log("state probe @" + (int)sec + "s: " + (GetStateName() ?? "未知")
                    + "  script=" + (GameScriptObj() != null ? "ok" : "null"));
            }
        }

        private static void LoadConfig()
        {
            try
            {
                if (!File.Exists(CFG_FILE)) return;
                _en = File.ReadAllText(CFG_FILE).Contains("lang=en");
            }
            catch { }
        }

        private static void SaveConfig()
        {
            try { File.WriteAllText(CFG_FILE, "lang=" + (_en ? "en" : "zh") + "\n"); }
            catch (Exception e) { Warn("write config failed: " + e.Message); }
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

        private static object InvokeFlexible(object obj, string name, params object[] provided)
        {
            if (obj == null) return null;
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
                    else if (pt.IsEnum) args[i] = Activator.CreateInstance(pt);
                    else args[i] = null;
                }
            }
            return m.Invoke(obj, args);
        }

        private static object FindUnityObject(Type t)
        {
            try
            {

                var resType = FindType("Resources");
                if (resType != null)
                {
                    var mi = resType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "FindObjectsOfTypeAll" && m.GetParameters().Length == 1);
                    if (mi != null)
                    {
                        var arr = mi.Invoke(null, new object[] { t });
                        if (arr is Array a && a.Length > 0) return a.GetValue(0);
                    }
                }

                var ot = FindType("UnityEngine.Object");
                if (ot != null)
                {
                    var mi2 = ot.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "FindObjectsOfType" && m.GetParameters().Length == 1);
                    if (mi2 != null)
                    {
                        var arr = mi2.Invoke(null, new object[] { t });
                        if (arr is Array a && a.Length > 0) return a.GetValue(0);
                    }
                }
            }
            catch { }
            return null;
        }

        private IEnumerator InitLoop()
        {
            Log("waiting for save…");
            var muType = FindType("ModUtilities");
            var gvType = FindType("GameVariables");
            var uiType = FindType("UiOverlay");
            var scriptType = FindType("GameScript");
            var kbType = FindType("Keyboard");

            while (true)
            {
                if (_gv == null && muType != null)
                {
                    var mu = GetStatic(muType, "Instance", "LuaInstance", "_instance") ?? FindUnityObject(muType);
                    if (mu != null) { try { _gv = InvokeFlexible(mu, "GetGameVariables"); } catch { } }
                }
                if (_gv == null && gvType != null)
                {
                    try
                    {
                        var p = gvType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
                        if (p != null) _gv = p.GetValue(null);
                    }
                    catch { }
                }

                if (_gv != null && !_ready)
                {
                    _script = scriptType != null ? FindUnityObject(scriptType) : null;
                    _ui = uiType != null ? (GetStatic(uiType, "Instance", "LuaInstance", "_instance") ?? FindUnityObject(uiType)) : null;
                    if (kbType != null)
                    {
                        _kb = GetStatic(kbType, "current");
                        _f8 = kbType.GetProperty("f8Key");
                    }
                    if (_ui != null) { _ready = true; Log("ready"); TryPatchChat(); TryPatchSay(); MelonCoroutines.Start(FirstRunCheck()); }
                }
                yield return null;
            }
        }

        private static bool KeyPressed(PropertyInfo prop)
        {
            if (prop == null || _kb == null) return false;
            try
            {
                var key = prop.GetValue(_kb);
                if (key == null) return false;
                var pi = key.GetType().GetProperty("wasPressedThisFrame", BindingFlags.Public | BindingFlags.Instance);
                return pi != null && Convert.ToBoolean(pi.GetValue(key));
            }
            catch { return false; }
        }

        public override void OnLateUpdate()
        {
            if (!_ready) return;
            if (!KeyPressed(_f8)) return;
            if (_menuOpen) CloseMenu();
            else ShowMainMenu();
        }

        private static void CloseMenu()
        {
            try
            {
                var mi = _ui?.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "CloseUiOverlay" && m.GetParameters().Length == 0);
                mi?.Invoke(_ui, null);
            }
            catch { }
            _menuOpen = false;
        }

        private Action Wrap(Action inner)
        {
            return () =>
            {
                _menuOpen = false;
                try { inner(); }
                catch (Exception e) { Warn("action failed: " + (e.InnerException ?? e).Message); }
            };
        }

        private static object ToIl2CppAction(Action a)
        {
            try
            {
                var t = AllTypes().FirstOrDefault(z => z.FullName == "Il2CppSystem.Action");
                if (t == null) return null;
                var dsType = FindType("Il2CppInterop.Runtime.DelegateSupport");
                var mi = dsType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "ConvertDelegate" && m.IsGenericMethod);
                var converted = mi.MakeGenericMethod(t).Invoke(null, new object[] { a });
                if (converted != null) _keepAlive.Add(converted);
                return converted;
            }
            catch (Exception e) { Warn("delegate failed: " + (e.InnerException ?? e).Message); return null; }
        }

        private void ShowMenu(string title, string text, List<(string text, Action act)> items)
        {
            try
            {
                var popupMethod = _ui.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "Popup" && m.GetParameters().Length == 3);
                if (popupMethod == null) { Warn("no Popup"); return; }

                var listType = popupMethod.GetParameters()[2].ParameterType;
                var list = Activator.CreateInstance(listType);
                var add = listType.GetMethod("Add");

                var choiceType = FindType("PopupChoice");
                var ctor = choiceType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(c =>
                    {
                        var p = c.GetParameters();
                        return p.Length >= 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType.Name.Contains("Action");
                    });
                if (ctor == null) { Warn("no PopupChoice ctor"); return; }
                var cps = ctor.GetParameters();

                foreach (var (t, act) in items)
                {
                    var del = ToIl2CppAction(Wrap(act));
                    if (del == null) continue;
                    var args = new object[cps.Length];
                    args[0] = t;
                    args[1] = del;
                    for (int i = 2; i < cps.Length; i++)
                    {
                        var pt = cps[i].ParameterType;
                        if (pt.IsEnum)
                        {
                            object ev;
                            try { ev = Enum.Parse(pt, "Normal", true); } catch { ev = Activator.CreateInstance(pt); }
                            args[i] = ev;
                        }
                        else args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                    }
                    add.Invoke(list, new[] { ctor.Invoke(args) });
                }

                popupMethod.Invoke(_ui, new object[] { title, text, list });
                _menuOpen = true;
            }
            catch (Exception e) { Warn("popup failed: " + (e.InnerException ?? e).Message); }
        }

        private void Defer(Action a) => MelonCoroutines.Start(DeferCo(a));

        private IEnumerator DeferCo(Action a)
        {
            var until = DateTime.UtcNow.AddSeconds(0.15);
            while (DateTime.UtcNow < until) yield return null;
            try { a(); } catch (Exception e) { Warn("defer failed: " + e.Message); }
        }

        private void ShowMainMenu()
        {
            ShowMenu(L("乌托邦菜单", "Utopia Menu"), L("选一类", "Pick a category"), new List<(string, Action)>
            {
                (L("资源", "Resources"), () => Defer(ShowResource)),
                (L("时间", "Time"), () => Defer(ShowTime)),
                (L("状态", "State"), () => Defer(ShowState)),
                (L("姿态", "Pose"), () => Defer(ShowPose)),
                (L("表情", "Expression"), () => Defer(ShowExpr)),
                (L("设置", "Settings"), () => Defer(ShowSettings)),
                (L("更多", "More"), () => Defer(ShowMore)),
            });
        }

        private void ShowSettings()
        {
            ShowMenu(L("设置", "Settings"), L("语言 / Language", "Language / 语言"), new List<(string, Action)>
            {
                (L("中文 ✔", "Chinese"), () => { _en = false; SaveConfig(); Log("lang=zh"); }),
                (L("English", "English ✔"), () => { _en = true; SaveConfig(); Log("lang=en"); }),
                (L("返回", "Back"), () => Defer(ShowMainMenu)),
            });
        }

        private void ShowResource()
        {
            ShowMenu(L("资源", "Resources"), L("数值类", "Values"), new List<(string, Action)>
            {
                (L("钱+100万", "+1M money"), () => SetVar("money", ReadInt("money") + 1000000)),
                (L("钱+1000万", "+10M money"), () => SetVar("money", ReadInt("money") + 10000000)),
                (L("体力+500", "+500 stamina"), () => AddVar("Stamina", 500f)),
                (L("精神+500", "+500 mental"), () => AddVar("MentalHealth", 500f)),
                (L("返回", "Back"), () => Defer(ShowMainMenu)),
            });
        }

        private void ShowTime()
        {
            ShowMenu(L("时间", "Time"), L("跳时间", "Skip time"), new List<(string, Action)>
            {
                (L("+1小时", "+1 hour"), () => InvokeFlexible(_script, "AddTime", 60)),
                (L("+6小时", "+6 hours"), () => InvokeFlexible(_script, "AddTime", 360)),
                (L("+1天", "+1 day"), () => InvokeFlexible(_script, "AddTime", 1440)),
                (L("睡8小时", "Sleep 8h"), () => InvokeFlexible(_script, "Sleep", 480, null, false)),
                (L("返回", "Back"), () => Defer(ShowMainMenu)),
            });
        }

        private void ShowState()
        {
            ShowMenu(L("状态", "State"), L("切换场景", "Change scene"), new List<(string, Action)>
            {
                (L("进互动", "Enter interact"), () => InvokeFlexible(_script, "StartInteract", null, false)),
                (L("退互动", "Exit interact"), () => InvokeFlexible(_script, "EndInteracting")),
                (L("进色色", "Enter sex scene"), () => InvokeFlexible(_script, "PrepareFricking")),
                (L("退色色", "Exit sex scene"), () => InvokeFlexible(_script, "EndFricking")),
                (L("做饭", "Cook"), () => InvokeFlexible(_script, "StartCooking")),
                (L("改数值…", "Edit values..."), () => Defer(ShowValues)),
                (L("返回", "Back"), () => Defer(ShowMainMenu)),
            });
        }

        private void ShowPose()
        {
            ShowMenu(L("姿态", "Pose"), L("手臂 / 手势", "Arms / gestures"), new List<(string, Action)>
            {
                (L("右抬", "Raise R arm"), () => PoseSet("ParamArmRUp", 1f)),
                (L("右放", "Lower R arm"), () => PoseSet("ParamArmRUp", 0f)),
                (L("左抬", "Raise L arm"), () => PoseSet("ParamArmLUp", 1f)),
                (L("左放", "Lower L arm"), () => PoseSet("ParamArmLUp", 0f)),
                (L("手势1", "Gesture 1"), () => PoseSet("ParamArmRGesture", 1f)),
                (L("手势2", "Gesture 2"), () => PoseSet("ParamArmRGesture", 2f)),
                (L("手势3", "Gesture 3"), () => PoseSet("ParamArmRGesture", 3f)),
            });
        }

        private void ShowExpr()
        {
            ShowMenu(L("表情", "Expression"), L("游戏表情通道", "Game expression channels"), new List<(string, Action)>
            {
                (L("脸红", "Blush"), () => ExprSet("Lewdness", 1f)),
                (L("弱脸红", "Light blush"), () => ExprSet("Lewdness", 0.5f)),
                (L("清空", "Clear"), () => ExprSet("", 0f)),
                (L("震惊", "Shock"), () => ExprSet("Shock", 1f)),
                (L("开心", "Happy"), () => ExprSet("Happiness", 1f)),
                (L("生气", "Angry"), () => ExprSet("Angry", 1f)),
                (L("返回", "Back"), () => Defer(ShowMainMenu)),
            });
        }

        private void ShowValues()
        {
            ShowMenu(L("改数值", "Edit values"), L("点一项输入新值", "Tap to type a new value"), new List<(string, Action)>
            {
                (L("好感 sympathy", "sympathy"), (Action)(() => InputValue("sympathy", "好感度", "Sympathy"))),
                (L("lust", "lust"), (Action)(() => InputValue("lust", "lust", "Lust"))),
                (L("兴奋 0~1", "Horniness 0-1"), (Action)(() => InputValue("CurrentHorniness", "兴奋度", "Horniness"))),
                (L("心情 Mood", "Mood"), (Action)(() => InputValue("Mood", "心情", "Mood"))),
                (L("钱 money", "money"), (Action)(() => InputValue("money", "金钱", "Money"))),
                (L("体力 Stamina", "Stamina"), (Action)(() => InputValue("Stamina", "体力", "Stamina"))),
                (L("精神 Mental", "MentalHealth"), (Action)(() => InputValue("MentalHealth", "精神", "Mental"))),
            });
        }

        private void InputValue(string field, string zhLabel, string enLabel)
        {
            try
            {
                var mi = _ui.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "InputPopup" && m.GetParameters().Length == 5);
                if (mi == null) { Warn("no InputPopup"); return; }

                object cur = GetMember(_gv, field);
                string curStr = cur == null ? "0" : Convert.ToString(cur, CultureInfo.InvariantCulture);

                Action<string> cb = txt =>
                {
                    try
                    {
                        WriteField(field, txt);
                        Log($"{field} <- {txt}");
                    }
                    catch (Exception e) { Warn($"write {field} failed: {(e.InnerException ?? e).Message}"); }
                };

                var del = ToIl2CppActionString(cb);
                var ps = mi.GetParameters();
                var args = new object[5];
                args[0] = L(zhLabel, enLabel);
                args[1] = L("输入新数值（当前 " + curStr + "）", "Enter new value (current " + curStr + ")");
                args[2] = del;
                args[3] = curStr;
                args[4] = ps[4].ParameterType.IsEnum
                    ? Enum.ToObject(ps[4].ParameterType, 0)
                    : Activator.CreateInstance(ps[4].ParameterType);
                mi.Invoke(_ui, args);
            }
            catch (Exception e) { Warn("InputPopup failed: " + (e.InnerException ?? e).Message); }
        }

        private static void WriteField(string field, string raw)
        {
            var t = _gv.GetType();
            var prop = t.GetProperty(field, BindingFlags.Public | BindingFlags.Instance);
            var fld = prop == null ? t.GetField(field, BindingFlags.Public | BindingFlags.Instance) : null;
            if (prop == null && fld == null) throw new Exception("no field " + field);
            var vt = prop != null ? prop.PropertyType : fld.FieldType;

            object val;
            if (vt == typeof(int)) val = int.Parse(raw.Trim(), CultureInfo.InvariantCulture);
            else if (vt == typeof(float)) val = float.Parse(raw.Trim(), CultureInfo.InvariantCulture);
            else if (vt == typeof(double)) val = double.Parse(raw.Trim(), CultureInfo.InvariantCulture);
            else if (vt == typeof(bool)) val = raw.Trim() == "1" || raw.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            else val = raw.Trim();

            if (prop != null) prop.SetValue(_gv, val); else fld.SetValue(_gv, val);
        }

        private static object ToIl2CppActionString(Action<string> a)
        {
            try
            {
                var openGeneric = AllTypes().FirstOrDefault(z => z.FullName == "Il2CppSystem.Action`1");
                if (openGeneric == null) { Warn("no Il2CppSystem.Action`1"); return null; }
                var closed = openGeneric.MakeGenericType(typeof(string));

                var dsType = FindType("Il2CppInterop.Runtime.DelegateSupport");
                var mi = dsType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "ConvertDelegate" && m.IsGenericMethod);
                var converted = mi.MakeGenericMethod(closed).Invoke(null, new object[] { a });
                if (converted != null) _keepAlive.Add(converted);
                return converted;
            }
            catch (Exception e) { Warn("string delegate failed: " + (e.InnerException ?? e).Message); return null; }
        }

        private void ShowMore()
        {
            ShowMenu(L("更多", "More"), L("工作台 / 复位 / 其他", "Workbench / Reset / Misc"), new List<(string, Action)>
            {
                (L("游戏工作台", "Game workbench"), () => UiOv("OpenAddItemCheat")),
                (L("物品界面", "Items menu"), () => UiOv("OpenItemsMenu")),
                (L("复位菜单", "Reset menu"), () => Defer(ShowReset)),
                                (L("对话替换（say.txt）", "Replace dialogue (say.txt)"), () => Defer(ShowSayCtrl)),
                (L("弹幕控制", "Stream chat"), () => Defer(ShowChat)),
                (L("返回", "Back"), () => Defer(ShowMainMenu)),
            });
        }

        private static bool _chatOn;
        private static bool _chatReplace;
        private static bool _sayReplace;
        private static readonly string SAY_CFG = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "say_override.txt");
        private static readonly string SAY_SCOPE_CFG = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "say_scope.txt");
        // 只有这些游戏状态里才替换对话（默认：房间 + 房间内互动）
        private static HashSet<string> _sayScope = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "RoomState", "InteractState" };
        private static bool _sayScopeAll;
        private static string _sayLogKey;
        private static readonly System.Random _rng = new System.Random();
        private static readonly string REPLACE_CFG = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "chat_override.txt");
        private static DateTime _lastChat = DateTime.MinValue;
        private static readonly string CHAT_FILE = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "chat.txt");

        private static void EnsureConfigFiles()
        {
            var dir = MelonLoader.Utils.MelonEnvironment.GameRootDirectory;
            void Make(string name, string content)
            {
                try
                {
                    var path = System.IO.Path.Combine(dir, name);
                    if (!File.Exists(path)) File.WriteAllText(path, content);
                }
                catch { }
            }

            Make("pose.txt", "speed = 0.3\nexprtime = 0.6\nexprhold = 3600\n");
            Make("say.txt", "Hello, {player}.\n");
            Make("chat.txt", "Cute stream\nLook at the camera!\n");
            Make("utopia_menu.txt", "lang=zh\npanelx=520\npanely=140\n");
            Make("chat_override.txt", "off\n");
            Make("say_override.txt", "off\n");
            Make("say_scope.txt",
                "# say.txt 只在这些游戏状态下生效，其余场景一律保留原版台词。\n" +
                "#   room        = 房间 + 房间内互动（推荐，默认）\n" +
                "#   room+       = 再加上 抱抱 / 换衣间\n" +
                "#   room+story  = 再加上 剧情对话\n" +
                "#   all         = 全部状态（旧行为，会顶掉人格模块的色色台词）\n" +
                "# 也可以直接写状态名，逗号分隔，例如：RoomState,InteractState\n" +
                "room\n");
        }

        private static string GameVersion()
        {
            try { return UnityEngine.Application.version; }
            catch { return "unknown"; }
        }

        private static IEnumerator FirstRunCheck()
        {
            var until = DateTime.UtcNow.AddSeconds(2.5);
            while (DateTime.UtcNow < until) yield return null;

            var dir = MelonLoader.Utils.MelonEnvironment.GameRootDirectory;
            var flag = System.IO.Path.Combine(dir, ".utopia_installed");
            EnsureConfigFiles();

            bool first = !File.Exists(flag);
            var ver = GameVersion();
            Log("game version: " + ver);

            if (first)
            {
                var text = L(
                    "安装成功。\n\n" +
                    "F8 = 主菜单\nF9 = 滑块面板（姿势/表情）\n\n" +
                    "配置文件都在游戏目录：\n" +
                    "say.txt（自定义台词）\n" +
                    "chat.txt（自定义弹幕）\n" +
                    "utopia_menu.txt（语言/面板位置）\n\n" +
                    "游戏版本：" + ver,
                    "Installed successfully.\n\n" +
                    "F8 = main menu\nF9 = slider panel (pose / expression)\n\n" +
                    "Config files are in the game folder:\n" +
                    "say.txt (custom dialogue)\n" +
                    "chat.txt (custom stream chat)\n" +
                    "utopia_menu.txt (language / panel position)\n\n" +
                    "Game version: " + ver);

                try
                {
                    var mi = _ui.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "OkPopup" && m.GetParameters().Length == 3);
                    if (mi != null)
                    {
                        var noop = ToIl2CppAction(() => { });
                        mi.Invoke(_ui, new object[] { L("乌托邦工具", "Utopia Tools"), text, noop });
                    }
                }
                catch (Exception e) { Warn("first-run popup failed: " + (e.InnerException ?? e).Message); }

                try { File.WriteAllText(flag, "installed " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\n"); } catch { }
            }
            else if (!ver.Contains("0.97"))
            {
                Warn("version mismatch: " + ver);
            }
        }

        private void ShowChat()
        {
            ShowMenu(L("弹幕控制", "Stream chat"),
                     (_chatOn ? L("自动：开", "Auto: ON") : L("自动：关", "Auto: OFF")) + " / " + (_chatReplace ? L("替换：开", "Replace: ON") : L("替换：关", "Replace: OFF")),
                     new List<(string, Action)>
            {
                (L("开启自动弹幕", "Turn ON"), (Action)(() => { _chatOn = true; Log("chat spam ON"); })),
                (L("关闭自动弹幕", "Turn OFF"), (Action)(() => { _chatOn = false; Log("chat spam OFF"); })),
                                (L("立刻发一条", "Send one now"), (Action)(() => SendChatLine())),
                (L("替换原版弹幕", "Replace vanilla chat"), (Action)(() => SetChatReplace(true))),
                (L("恢复原版弹幕", "Restore vanilla chat"), (Action)(() => SetChatReplace(false))),
                (L("返回", "Back"), (Action)(() => Defer(ShowMore))),
            });
        }

        private static object FindStreamManager()
        {
            try
            {
                if (_gv == null) return null;
                var t = _gv.GetType();
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    var pt = p.PropertyType;
                    if (!pt.Name.Contains("Frick")) continue;
                    object state = null;
                    try { state = p.GetValue(_gv); } catch { }
                    if (state == null) continue;
                    var mgrProp = state.GetType().GetProperty("CurrentStreamManager", BindingFlags.Public | BindingFlags.Instance);
                    if (mgrProp == null) continue;
                    var mgr = mgrProp.GetValue(state);
                    if (mgr != null) return mgr;
                }

                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!f.FieldType.Name.Contains("Frick")) continue;
                    object state = null;
                    try { state = f.GetValue(_gv); } catch { }
                    if (state == null) continue;
                    var mgrProp = state.GetType().GetProperty("CurrentStreamManager", BindingFlags.Public | BindingFlags.Instance);
                    if (mgrProp == null) continue;
                    var mgr = mgrProp.GetValue(state);
                    if (mgr != null) return mgr;
                }
            }
            catch (Exception e) { Warn("find stream manager failed: " + (e.InnerException ?? e).Message); }
            return null;
        }

        private static void SendChatLine()
        {
            try
            {
                var mgr = FindStreamManager();
                if (mgr == null) { Warn("not streaming (no StreamManager)"); return; }

                var mi = mgr.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "AddInteraction"
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType == typeof(string));
                if (mi == null) { Warn("no AddInteraction(string)"); return; }

                string line = PickChatLine();
                mi.Invoke(mgr, new object[] { line });
                Log("chat: " + line);
            }
            catch (Exception e) { Warn("send chat failed: " + (e.InnerException ?? e).Message); }
        }

        private static string PickChatLine()
        {
            var defaults = new[]
            {
                "她今天怎么这么可爱",
                "主播看镜头一下",
                "感觉她有点不对劲…",
                "这表情绝了",
            };
            try
            {
                if (File.Exists(CHAT_FILE))
                {
                    var lines = File.ReadAllLines(CHAT_FILE).Select(l => l.Trim())
                        .Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
                    if (lines.Count > 0)
                        return lines[new System.Random().Next(lines.Count)];
                }
            }
            catch { }
            return defaults[new System.Random().Next(defaults.Length)];
        }

        private static HarmonyLib.Harmony _harmony;

        private static void SetChatReplace(bool on)
        {
            _chatReplace = on;
            try { File.WriteAllText(REPLACE_CFG, on ? "on" : "off"); } catch { }
            Log("chat replace " + (on ? "ON" : "OFF"));
        }

        private static void LoadChatReplaceCfg()
        {
            try
            {
                if (!File.Exists(REPLACE_CFG)) return;
                _chatReplace = File.ReadAllText(REPLACE_CFG).Trim().ToLowerInvariant().StartsWith("on");
            }
            catch { }
        }

        private static void TryPatchChat()
        {
            try
            {
                LoadChatReplaceCfg();
                var smType = FindType("StreamManager");
                if (smType == null) { Warn("no StreamManager type"); return; }
                var target = smType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GenerateRandomComment");
                if (target == null) { Warn("no GenerateRandomComment"); return; }

                _harmony = new HarmonyLib.Harmony("utopia.chat.patch");
                var prefix = typeof(Main).GetMethod(nameof(ChatPrefix), BindingFlags.Static | BindingFlags.NonPublic);
                _harmony.Patch(target, new HarmonyMethod(prefix));
                Log("chat patched: " + target.Name + "(" +
                    string.Join(",", target.GetParameters().Select(x => x.ParameterType.Name)) + ")");
            }
            catch (Exception e) { Warn("chat patch failed: " + (e.InnerException ?? e).Message); }
        }

        private static bool ChatPrefix(object __instance)
        {
            try
            {
                if (!_chatReplace || __instance == null) return true;
                var add = __instance.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "AddInteraction"
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType == typeof(string));
                if (add == null) return true;
                add.Invoke(__instance, new object[] { PickChatLine() });
                return false;
            }
            catch { return true; }
        }

        private void ShowSayCtrl()
        {
            LoadSayScopeCfg();
            var state = GetStateName() ?? "?";
            var head = (_sayReplace ? L("当前：开", "ON") : L("当前：关", "OFF"))
                     + "  " + L("状态", "state") + "=" + state
                     + "  " + L("范围", "scope") + "=" + ScopeLabel();
            ShowMenu(L("对话替换", "Dialogue replace"), head,
                     new List<(string, Action)>
            {
                (L("开启替换", "Turn ON"), (Action)(() => SetSayReplace(true))),
                (L("关闭替换", "Turn OFF"), (Action)(() => SetSayReplace(false))),
                (L("范围：房间（推荐）", "Scope: room"), (Action)(() => SetScopePreset("room"))),
                (L("范围：房间+抱抱/换衣", "Scope: room+"), (Action)(() => SetScopePreset("room+"))),
                (L("范围：房间+剧情", "Scope: room+story"), (Action)(() => SetScopePreset("room+story"))),
                (L("范围：全部（会顶掉色色台词）", "Scope: all"), (Action)(() => SetScopePreset("all"))),
                (L("返回", "Back"), (Action)(() => Defer(ShowMore))),
            });
        }

        private static void SetScopePreset(string preset)
        {
            try { File.WriteAllText(SAY_SCOPE_CFG, preset + "\n"); } catch { }
            _sayLogKey = null;
            LoadSayScopeCfg();
        }

        private static void SetSayReplace(bool on)
        {
            _sayReplace = on;
            _sayLogKey = null;
            try { File.WriteAllText(SAY_CFG, on ? "on" : "off"); } catch { }
            Log("say replace " + (on ? "ON" : "OFF") + "  scope=" + ScopeLabel());
        }

        private static void LoadSayReplaceCfg()
        {
            try
            {
                if (!File.Exists(SAY_CFG)) return;
                _sayReplace = File.ReadAllText(SAY_CFG).Trim().ToLowerInvariant().StartsWith("on");
            }
            catch { }
        }

        private static Type FindFungusType(string name)
        {
            var t = AllTypes().FirstOrDefault(x => x.Name == name && x.Namespace == "Il2CppFungus");
            if (t != null) return t;
            try
            {
                var asm = System.Reflection.Assembly.Load("Il2CppFungus");
                if (asm != null) t = asm.GetType("Il2CppFungus." + name);
            }
            catch { }
            return t;
        }

        private static void TryPatchSay()
        {
            try
            {
                LoadSayReplaceCfg();
                LoadSayScopeCfg();
                var sayType = FindFungusType("SayDialog");
                if (sayType == null) { Warn("no SayDialog type"); return; }

                var target = sayType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "Say"
                        && m.GetParameters().Length == 6
                        && m.GetParameters()[0].ParameterType == typeof(string));
                if (target == null) { Warn("no Say(string,...)"); return; }

                if (_harmony == null) _harmony = new HarmonyLib.Harmony("utopia.say.patch");
                var prefix = typeof(Main).GetMethod(nameof(SayPrefix), BindingFlags.Static | BindingFlags.NonPublic);
                _harmony.Patch(target, new HarmonyMethod(prefix));
                Log("say patched: Say(" +
                    string.Join(",", target.GetParameters().Select(x => x.ParameterType.Name)) + ")");
            }
            catch (Exception e) { Warn("say patch failed: " + (e.InnerException ?? e).Message); }
        }

        // ---- 当前游戏状态（GameScript.GameState → 类型名，例如 "RoomState" / "NewFrickState"）----
        private static object _gameScriptCached;
        private static bool _gsTried;
        private static DateTime _gsLastTry = DateTime.MinValue;

        private static object GameScriptObj()
        {
            if (_script != null) return _script;
            if (_gameScriptCached != null) return _gameScriptCached;
            // 找不到时别每句台词都去全网反射（很贵）：最多每 2 秒重试一次
            if (_gsTried && (DateTime.UtcNow - _gsLastTry).TotalSeconds < 2.0) return null;
            _gsTried = true;
            _gsLastTry = DateTime.UtcNow;
            try
            {
                var t = FindType("GameScript");
                if (t != null)
                {
                    _gameScriptCached = FindUnityObject(t) ?? GetStatic(t, "Instance", "instance", "_instance");
                }
            }
            catch { }
            return _gameScriptCached;
        }

        private static string GetStateName()
        {
            try
            {
                var st = GetMember(GameScriptObj(), "GameState");
                if (st == null)
                {
                    var t = GameScriptObj() != null ? GameScriptObj().GetType() : FindType("GameScript");
                    if (t != null)
                    {
                        var inst = GetStatic(t, "Instance", "instance", "_instance");
                        st = GetMember(inst, "GameState");
                    }
                }
                if (st == null) return null;

                var real = RealClassName(st);          // 真实类名：RoomState / NewFrickState …
                if (!string.IsNullOrEmpty(real)) return real;
                return st.GetType().Name;              // 兜底（可能只拿到基类名 "GameState"）
            }
            catch { }
            return null;
        }

        // Il2CppInterop 按【方法声明的返回类型】包装对象，所以 GameState 属性永远返回 `GameState` 包装，
        // GetType().Name 拿不到派生类。必须问 il2cpp 要对象真实类指针里的类名。
        private static string RealClassName(object o)
        {
            try
            {
                var b = o as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                if (b == null) return null;
                var cls = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(b.Pointer);
                if (cls == IntPtr.Zero) return null;

                try
                {
                    var name = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name_(cls);
                    if (!string.IsNullOrEmpty(name)) return name;
                }
                catch { }

                var p = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name(cls);
                if (p != IntPtr.Zero) return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(p);
            }
            catch { }
            return null;
        }

        private static void LoadSayScopeCfg()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool all = false;
            string src = "内置默认";
            try
            {
                if (File.Exists(SAY_SCOPE_CFG))
                {
                    src = "say_scope.txt";
                    foreach (var raw in File.ReadAllLines(SAY_SCOPE_CFG))
                    {
                        var s = raw.Trim();
                        if (s.Length == 0 || s.StartsWith("#")) continue;
                        foreach (var part in s.Split(','))
                        {
                            var p = part.Trim();
                            if (p.Length == 0) continue;
                            var low = p.ToLowerInvariant();
                            if (low == "all" || low == "*")
                            {
                                all = true;
                            }
                            else if (low == "room")
                            {
                                set.Add("RoomState"); set.Add("InteractState");
                            }
                            else if (low == "room+")
                            {
                                set.Add("RoomState"); set.Add("InteractState");
                                set.Add("CuddleState"); set.Add("ChangingRoomState");
                            }
                            else if (low == "room+story")
                            {
                                set.Add("RoomState"); set.Add("InteractState");
                                set.Add("StoryState");
                            }
                            else
                            {
                                set.Add(p);   // 直接写状态名
                            }
                        }
                    }
                }
            }
            catch (Exception e) { Warn("read say_scope failed: " + e.Message); }

            if (set.Count == 0 && !all) { set.Add("RoomState"); set.Add("InteractState"); }
            _sayScope = set;
            _sayScopeAll = all;
            Log("say scope = " + ScopeLabel() + " (" + src + ")");
        }

        private static string ScopeLabel()
        {
            if (_sayScopeAll) return "ALL";
            try { return string.Join(",", _sayScope.OrderBy(x => x).ToArray()); }
            catch { return "?"; }
        }

        // 状态名容错：运行时类型名可能是 RoomState / GameScript_RoomState / GameScript.RoomState，
        // 三种都认，免得白名单写对了却被类型名写法坑掉。
        private static bool IsStateAllowed(string state)
        {
            if (string.IsNullOrEmpty(state)) return false;
            if (_sayScopeAll) return true;
            foreach (var sc in _sayScope)
            {
                if (state.Equals(sc, StringComparison.OrdinalIgnoreCase)) return true;
                if (state.EndsWith("_" + sc, StringComparison.OrdinalIgnoreCase)) return true;
                if (state.EndsWith("/" + sc, StringComparison.OrdinalIgnoreCase)) return true;
                if (state.EndsWith("." + sc, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void SayPrefix(ref string __0)
        {
            try
            {
                if (!_sayReplace) return;

                var state = GetStateName();
                if (state == null)
                {
                    if (_sayLogKey != "nostate")
                    {
                        _sayLogKey = "nostate";
                        Warn("SAY: 读不到 GameScript.GameState，为避免顶掉别人台词，这次不替换");
                    }
                    return;
                }

                bool allow = IsStateAllowed(state);
                var key = state + (allow ? "+" : "-");
                if (_sayLogKey != key)
                {
                    _sayLogKey = key;
                    Log("SAY state=" + state + (allow ? " -> 替换" : " -> 跳过（保留原版/其他人格模组台词）"));
                }
                if (!allow) return;

                var line = PickSayLine();
                if (!string.IsNullOrEmpty(line)) __0 = line;
            }
            catch { }
        }

        private static string PickSayLine()
        {
            try
            {
                if (!File.Exists(SAY_FILE)) return null;
                var lines = File.ReadAllLines(SAY_FILE).Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
                if (lines.Count == 0) return null;
                return lines[_rng.Next(lines.Count)];
            }
            catch { return null; }
        }

        private void ShowReset()
        {
            ShowMenu(L("复位", "Reset"), L("清状态", "Clear state"), new List<(string, Action)>
            {
                (L("表情复位", "Reset expression"), () => ExprSet("", 0f)),
                (L("肢体复位", "Reset body"), () => PoseReset()),
                (L("全部复位", "Reset all"), () => { ExprSet("", 0f); PoseReset(); }),
                (L("紧急复位", "Emergency reset"), () => EmergencyReset()),
                (L("返回", "Back"), () => Defer(ShowMore)),
            });
        }

        private static int ReadInt(string name)
        {
            try { return Convert.ToInt32(GetMember(_gv, name)); }
            catch { return 0; }
        }

        private static void SetVar(string name, object value)
        {
            try
            {
                var t = _gv.GetType();
                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                var f = p == null ? t.GetField(name, BindingFlags.Public | BindingFlags.Instance) : null;
                if (p != null) p.SetValue(_gv, Convert.ChangeType(value, p.PropertyType));
                else if (f != null) f.SetValue(_gv, Convert.ChangeType(value, f.FieldType));
                Log($"{name} -> {value}");
            }
            catch (Exception e) { Warn("set var failed: " + e.Message); }
        }

        private static void AddVar(string name, float delta)
        {
            try
            {
                var t = _gv.GetType();
                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                var f = p == null ? t.GetField(name, BindingFlags.Public | BindingFlags.Instance) : null;
                if (p == null && f == null) { Warn("no var " + name); return; }
                var vt = p != null ? p.PropertyType : f.FieldType;
                object cur = p != null ? p.GetValue(_gv) : f.GetValue(_gv);
                object nv = vt == typeof(int) ? Convert.ToInt32(cur) + (int)delta : Convert.ToSingle(cur) + delta;
                if (p != null) p.SetValue(_gv, nv); else f.SetValue(_gv, nv);
                Log($"{name} {cur} -> {nv}");
            }
            catch (Exception e) { Warn("add var failed: " + e.Message); }
        }

        private static void UiOv(string method)
        {
            try
            {
                var mi = _ui?.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == method && m.GetParameters().Length == 0);
                if (mi == null) { Warn("UiOverlay has no " + method); return; }
                mi.Invoke(_ui, null);
                Log("UiOverlay." + method);
            }
            catch (Exception e) { Warn(method + " failed: " + (e.InnerException ?? e).Message); }
        }

        private static void PoseSet(string key, float value)
        {
            try
            {
                var lines = File.Exists(POSE_FILE) ? File.ReadAllLines(POSE_FILE).ToList() : new List<string>();
                bool found = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    var t = lines[i].Trim();
                    if (t.StartsWith("#")) continue;
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    if (t.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = key + " = " + value.ToString("0.###", CultureInfo.InvariantCulture);
                        found = true;
                        break;
                    }
                }
                if (!found) lines.Add(key + " = " + value.ToString("0.###", CultureInfo.InvariantCulture));
                File.WriteAllLines(POSE_FILE, lines);
                Log($"{key} = {value:0.###}");
            }
            catch (Exception e) { Warn("pose write failed: " + e.Message); }
        }

        private static void ExprSet(string channel, float value)
        {
            try
            {
                var lines = File.Exists(POSE_FILE) ? File.ReadAllLines(POSE_FILE).ToList() : new List<string>();
                lines = lines.Where(l =>
                {
                    var t = l.Trim();
                    return !(t.StartsWith("expr", StringComparison.OrdinalIgnoreCase)
                          || t.StartsWith("emote", StringComparison.OrdinalIgnoreCase));
                }).ToList();
                if (!string.IsNullOrEmpty(channel))
                    lines.Add($"expr {channel} = {value.ToString("0.###", CultureInfo.InvariantCulture)}");
                File.WriteAllLines(POSE_FILE, lines);
                Log("expr: " + (string.IsNullOrEmpty(channel) ? "clear" : channel + "=" + value));
            }
            catch (Exception e) { Warn("expr write failed: " + e.Message); }
        }

        private static void PoseReset()
        {
            try
            {
                if (!File.Exists(POSE_FILE)) return;
                var keep = File.ReadAllLines(POSE_FILE).Where(l =>
                {
                    var t = l.Trim();
                    if (t.Length == 0 || t.StartsWith("#")) return true;
                    if (t.StartsWith("speed", StringComparison.OrdinalIgnoreCase)) return true;
                    if (t.StartsWith("exprtime", StringComparison.OrdinalIgnoreCase)) return true;
                    if (t.StartsWith("exprhold", StringComparison.OrdinalIgnoreCase)) return true;
                    if (t.StartsWith("expr", StringComparison.OrdinalIgnoreCase)) return true;
                    if (t.StartsWith("emote", StringComparison.OrdinalIgnoreCase)) return true;
                    return false;
                }).ToList();
                File.WriteAllLines(POSE_FILE, keep);
                Log("body reset");
            }
            catch (Exception e) { Warn("body reset failed: " + e.Message); }
        }

        private static void EmergencyReset()
        {
            try
            {
                ExprSet("", 0f);
                PoseReset();
                var ctrl = GetController();
                if (ctrl != null)
                {
                    try { InvokeFlexible(ctrl, "LetGoOfCurrentlyHeld"); } catch { }
                    try { InvokeFlexible(ctrl, "FinishAllMoves"); } catch { }
                    var expr = GetMember(ctrl, "Expression");
                    if (expr != null) { try { InvokeFlexible(expr, "ClearExpression"); } catch { } }
                }
                Log("emergency reset done");
            }
            catch (Exception e) { Warn("emergency reset failed: " + e.Message); }
        }

        private static object _ctrlCache;

        private static object GetController()
        {
            try
            {
                if (_ctrlCache != null) return _ctrlCache;
                var singType = FindType("Live2DControllerSingleton");
                if (singType == null) return null;
                var single = GetStatic(singType, "Instance", "LuaInstance", "_instance");
                if (single == null) return null;
                _ctrlCache = InvokeFlexible(single, "GetReadyController", "bot");
                return _ctrlCache;
            }
            catch { return null; }
        }

        private static void SpawnItem(string name, int count)
        {
            try
            {
                if (_itemEnumType == null) _itemEnumType = FindType("Il2CppGameItems.ItemEnum");
                if (_itemType == null) _itemType = AllTypes().FirstOrDefault(t => t.FullName == "Il2Cpp.Item");
                if (_gameUtilsType == null) _gameUtilsType = FindType("GameUtilities");
                if (_itemEnumType == null || _itemType == null || _gameUtilsType == null) { Warn("item types missing"); return; }

                var ev = Enum.Parse(_itemEnumType, name, true);
                var ctor = _itemType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(c =>
                    {
                        var p = c.GetParameters();
                        return p.Length == 2 && p[0].ParameterType.IsEnum && p[1].ParameterType == typeof(float);
                    });
                var add = _gameUtilsType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "AddItemToInventory");
                if (ctor == null || add == null) { Warn("item api missing"); return; }

                for (int i = 0; i < count; i++)
                    add.Invoke(null, new[] { ctor.Invoke(new object[] { ev, 1f }) });
                Log($"spawn {name} x{count}");
            }
            catch (Exception e) { Warn("spawn failed: " + (e.InnerException ?? e).Message); }
        }

        private static void SpawnFromFile()
        {
            try
            {
                if (!File.Exists(SPAWN_FILE)) { Warn("no spawn.txt"); return; }
                foreach (var raw in File.ReadAllLines(SPAWN_FILE))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var sp = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    int c = 1;
                    if (sp.Length > 1) int.TryParse(sp[1], out c);
                    SpawnItem(sp[0], c);
                }
                Log("spawn.txt done");
            }
            catch (Exception e) { Warn("spawn.txt failed: " + e.Message); }
        }

        private static void SayFromFile()
        {
            try
            {
                if (!File.Exists(SAY_FILE)) { Warn("no say.txt"); return; }
                var lines = File.ReadAllLines(SAY_FILE).Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
                if (lines.Count == 0) { Warn("say.txt empty"); return; }
                var text = string.Join("\n", lines);

                var mgrType = FindType("BetterConversationManager");
                if (mgrType == null) { Warn("no BetterConversationManager type"); return; }
                Log("mgrType = " + mgrType.FullName + " @ " + mgrType.Assembly.GetName().Name);

                var settingsType = FindType("ConversationItemWriteSettings");
                if (settingsType == null) { Warn("no write settings type"); return; }
                var settings = GetStatic(settingsType, "DefaultWriteSettings")
                            ?? GetStatic(settingsType, "NoWaitForInputWriteSettings");
                if (settings == null) { Warn("no write settings preset"); return; }

                object mgr;
                try { mgr = Activator.CreateInstance(mgrType); }
                catch (Exception ex) { Warn("new manager failed: " + (ex.InnerException ?? ex).Message); return; }
                if (mgr == null) { Warn("manager instance null"); return; }

                var all = mgrType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var mi = all.FirstOrDefault(m => m.Name == "DoModdableConversation" && m.GetParameters().Length == 2);
                if (mi == null)
                    mi = all.FirstOrDefault(m => m.Name == "DoConversation" && m.GetParameters().Length == 2
                                                 && m.GetParameters()[0].ParameterType == typeof(string));
                if (mi == null)
                {
                    var names = string.Join(" | ", all.Select(m => m.Name + "(" +
                        string.Join(",", m.GetParameters().Select(x => x.ParameterType.Name)) + ")").Take(8));
                    Warn("no conversation method. available: " + names);

                    try
                    {
                        InvokeFlexible(_script, "ShowText", text);
                        Log("fallback ShowText used");
                    }
                    catch (Exception ex) { Warn("ShowText fallback failed: " + (ex.InnerException ?? ex).Message); }
                    return;
                }
                Log("using method: " + mi.Name + "(" +
                    string.Join(",", mi.GetParameters().Select(x => x.ParameterType.Name)) + ")");

                object co;
                try { co = mi.Invoke(mgr, new object[] { text, settings }); }
                catch (Exception ex) { Warn("start conversation failed: " + (ex.InnerException ?? ex).Message); return; }
                if (co == null) { Warn("coroutine null"); return; }

                var runner = _script;
                if (runner == null) { Warn("no MonoBehaviour runner"); return; }
                var sc = runner.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "StartCoroutine" && m.GetParameters().Length == 1);
                if (sc == null) { Warn("no StartCoroutine"); return; }
                sc.Invoke(runner, new[] { co });

                Log("conversation started: " + text.Replace("\n", " / "));
            }
            catch (Exception e) { Warn("say failed: " + (e.InnerException ?? e).Message); }
        }

        private static void BringCharacter(string id)
        {
            try
            {
                var singType = FindType("SimpleCharacterSingleton");
                if (singType == null) { Warn("no character singleton"); return; }
                var single = GetStatic(singType, "Instance", "LuaInstance", "_instance") ?? FindUnityObject(singType);
                if (single == null) { Warn("no character instance"); return; }
                object ch = null;
                try { ch = InvokeFlexible(single, "GetCharacterByDefaultId", id); } catch { }
                if (ch == null) { Warn("no character " + id); return; }
                InvokeFlexible(single, "MoveCharacterIntoTheView", ch, 1f, 0f);
                Log("summon: " + id);
            }
            catch (Exception e) { Warn("summon failed: " + (e.InnerException ?? e).Message); }
        }
    }
}
