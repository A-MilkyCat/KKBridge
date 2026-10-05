// ================================================================
// 上下文
//
// === 單位四元數運算 ===
// 共軛等價於逆(必須是單位四元數)
// 共軛:    q = (-x, -y, -z, w);
// XY鏡像:  q = (-x, -y,  z, w);
// XZ鏡像:  q = (-x,  y, -z, w);
// YZ鏡像:  q = ( x, -y, -z, w);
// 坐標映射: C = A * B * A.conjugated();
//
// 共軛反交換律
// (A * B).conjugated() = B.conjugated() * A.conjugated()
// 坐標變換+共軛
// C = (A * B * A.conjugated()).conjugated()
// C = (B * A.conjugated()).conjugated() * A.conjugated()
// C = A * B.conjugated() * A.conjugated()
// C = A * (A * B).conjugated()
//
// Blender  坐標是Z向上 骨骼主軸是Y
// Koikatsu 坐標是Y向上 骨骼主軸是X
//
// Koikatsu的Unity版本太舊，Quaternion少了一些重要方法
// 所以自行實現QuaternionExtensions補足
//
// 專案使用.NET Framework 3.5
//
// 直接讀取FK骨的動作數據，不使用changeAmount
// 如果改成讀取changeAmount就沒辦法烘焙IK到FK了，等於沒有意義
//
// CharaStudio將人物改成T-pose靜止姿勢的方法:
// 關閉IK，打開所有FK將旋轉通通重置為0
// 眼睛難以調整成靜止姿勢，[正面][相機][閃躲][固定][操作]5個選項旋轉都不是0
//
// Koikatsu使用T-pose，且骨骼嚴格平行於三軸
// 右手手指是反的，扭轉成指甲朝下
// 眼睛是貼圖移動，將眼睛骨骼旋轉角度做線性變換得到貼圖坐標偏移
//
// PMX對照模型
// Tda式初音ミク・アペンドVer1.10
// https://bowlroll.net/file/4576
// PMX使用A-pose，部分骨骼有扭轉
//
// 1.在Blender中使用Python腳本預先計算出「T-Pose修正」和「坐標變換」所需的四元數數據
// 2.手動修正Koikatsu特殊軸向問題，套用針對特定骨骼的硬編碼鏡像或旋轉處理
// 3.使用預先計算好的「T-Pose修正」和「坐標變換」數據計算出正確VMD旋轉數據
//
// Koikatsu原始局部空間旋轉數據 -> 手動軸向修正 -> T-Pose修正 -> 坐標變換 -> VMD旋轉數據
//
// Python和C#腳本都手動正確處理了骨骼T-Pose朝向和扭轉問題
//
// Python腳本路徑: tools/generate_bone_map.py
//
// 坐標變換的四種方法:
// 1.歐拉角(有萬向鎖問題)
// 2.四元數運算
// 3.軸角表示法
// 4.旋轉矩陣
//
// 注意Blender四元數是wxyz，Unity和VMD四元數是xyzw
//
// ================================================================

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using KKBridge.Compatibility;
using KKBridge.Vmd;
using Studio;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace KKBridge
{
    /// <summary>
    /// 輔助類別，處理 UGUI 視窗的拖動
    /// </summary>
    public class DraggableWindow : MonoBehaviour, IPointerDownHandler, IDragHandler
    {
        private Vector2 _offset;
        public RectTransform TargetRect { get; set; }

        private void Awake()
        {
            // 如果未指定拖動目標，則預設為父物件
            if (TargetRect == null)
            {
                TargetRect = transform.parent.GetComponent<RectTransform>();
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData == null) return;

            RectTransformUtility.ScreenPointToLocalPointInRectangle(TargetRect, eventData.position, eventData.pressEventCamera, out _offset);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData == null) return;
            if (TargetRect == null) return;

            RectTransformUtility.ScreenPointToLocalPointInRectangle(TargetRect.parent as RectTransform, eventData.position, eventData.pressEventCamera, out Vector2 localPointerPosition);
            TargetRect.localPosition = localPointerPosition - _offset;
        }
    }

    /// <summary>
    /// Raw facial state sampled directly from the live Koikatsu character after Timeline/runtime updates.
    /// In addition to semantic/control channels, V2 records the final active facial BlendShapes and
    /// local transforms for every descendant whose name contains "hitomi".
    /// No PMX morph mapping is applied here.
    /// </summary>
    internal sealed class RawBlendShapeSample
    {
        public string RendererName;
        public string BlendShapeName;
        public float WeightPercent;
    }

    internal sealed class RawBlendShapeSource
    {
        public SkinnedMeshRenderer Renderer;
        public int Index;
        public string RendererName;
        public string BlendShapeName;
    }

    internal sealed class RawHitomiSample
    {
        public string Name;
        public Vector3 LocalPosition;
        public Quaternion LocalRotation;
        public Vector3 LocalEuler;
        public Vector3 LocalScale;
    }

    internal sealed class RawFacialCaptureContext
    {
        public readonly List<RawBlendShapeSource> BlendShapes = new List<RawBlendShapeSource>();
        public readonly List<Transform> HitomiTransforms = new List<Transform>();
    }

    internal sealed class RawFacialFrame
    {
        public uint FrameNumber;
        public float TimelineTime;
        public int EyebrowPattern;
        public float EyebrowOpen;
        public int EyesPattern;
        public float EyesOpen;
        public int MouthPattern;
        public float MouthOpen;
        public List<RawBlendShapeSample> BlendShapes;
        public List<RawHitomiSample> HitomiTransforms;
    }

    internal static class RawFacialExporter
    {
        private static bool IsFacialBlendShapeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            // Keep the raw face families broad on purpose. This captures the final KK facial state
            // before the existing config.json PMX mapping discards or merges channels.
            return name.StartsWith("eye_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("mayuge.", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("kuti_", StringComparison.OrdinalIgnoreCase);
        }

        public static RawFacialCaptureContext CreateContext(OCIChar ociChar)
        {
            var context = new RawFacialCaptureContext();
            if (ociChar == null || ociChar.charInfo == null) return context;

            Transform root = ociChar.charInfo.transform;
            if (root == null) return context;

            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer == null || renderer.sharedMesh == null) continue;

                int blendShapeCount = renderer.sharedMesh.blendShapeCount;
                for (int index = 0; index < blendShapeCount; index++)
                {
                    string blendShapeName = renderer.sharedMesh.GetBlendShapeName(index);
                    if (!IsFacialBlendShapeName(blendShapeName)) continue;

                    context.BlendShapes.Add(new RawBlendShapeSource
                    {
                        Renderer = renderer,
                        Index = index,
                        RendererName = renderer.name,
                        BlendShapeName = blendShapeName
                    });
                }
            }

            var transforms = root.GetComponentsInChildren<Transform>(true);
            foreach (var tf in transforms)
            {
                if (tf == null || string.IsNullOrEmpty(tf.name)) continue;
                if (tf.name.IndexOf("hitomi", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    context.HitomiTransforms.Add(tf);
                }
            }

            return context;
        }

        public static RawFacialFrame Capture(
            OCIChar ociChar,
            uint frameNumber,
            float timelineTime,
            RawFacialCaptureContext context)
        {
            if (ociChar == null || ociChar.charInfo == null) return null;
            if (context == null) context = CreateContext(ociChar);

            ChaControl cha = ociChar.charInfo;
            var blendShapes = new List<RawBlendShapeSample>();
            foreach (var source in context.BlendShapes)
            {
                if (source == null || source.Renderer == null) continue;

                float weightPercent = source.Renderer.GetBlendShapeWeight(source.Index);
                if (Mathf.Abs(weightPercent) <= 1E-04f) continue;

                blendShapes.Add(new RawBlendShapeSample
                {
                    RendererName = source.RendererName,
                    BlendShapeName = source.BlendShapeName,
                    WeightPercent = weightPercent
                });
            }

            var hitomiTransforms = new List<RawHitomiSample>();
            foreach (var tf in context.HitomiTransforms)
            {
                if (tf == null) continue;

                hitomiTransforms.Add(new RawHitomiSample
                {
                    Name = tf.name,
                    LocalPosition = tf.localPosition,
                    LocalRotation = tf.localRotation,
                    LocalEuler = tf.localEulerAngles,
                    LocalScale = tf.localScale
                });
            }

            return new RawFacialFrame
            {
                FrameNumber = frameNumber,
                TimelineTime = timelineTime,
                EyebrowPattern = cha.GetEyebrowPtn(),
                EyebrowOpen = cha.GetEyebrowOpenMax(),
                EyesPattern = cha.GetEyesPtn(),
                EyesOpen = cha.GetEyesOpenMax(),
                MouthPattern = cha.GetMouthPtn(),
                MouthOpen = cha.GetMouthOpenMax(),
                BlendShapes = blendShapes,
                HitomiTransforms = hitomiTransforms
            };
        }

        private static string EscapeJson(string value)
        {
            if (value == null) return string.Empty;
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

        private static void AppendFloat(StringBuilder sb, float value)
        {
            sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void AppendVector3(StringBuilder sb, Vector3 value)
        {
            sb.Append("[");
            AppendFloat(sb, value.x);
            sb.Append(",");
            AppendFloat(sb, value.y);
            sb.Append(",");
            AppendFloat(sb, value.z);
            sb.Append("]");
        }

        private static void AppendQuaternion(StringBuilder sb, Quaternion value)
        {
            sb.Append("[");
            AppendFloat(sb, value.x);
            sb.Append(",");
            AppendFloat(sb, value.y);
            sb.Append(",");
            AppendFloat(sb, value.z);
            sb.Append(",");
            AppendFloat(sb, value.w);
            sb.Append("]");
        }

        public static void Export(IList<RawFacialFrame> frames, int fps, string path)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"format\": \"KKBridgeRawFacialV2\",\n");
            sb.Append("  \"fps\": ").Append(fps.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"channels\": [\"eyebrow_pattern\", \"eyebrow_open\", \"eyes_pattern\", \"eyes_open\", \"mouth_pattern\", \"mouth_open\", \"facial_blendshapes\", \"hitomi_transforms\"],\n");
            sb.Append("  \"blendshape_weight_unit\": \"percent_0_100\",\n");
            sb.Append("  \"frames\": [\n");

            for (int i = 0; i < frames.Count; i++)
            {
                RawFacialFrame f = frames[i];
                sb.Append("    {");
                sb.Append("\"frame\":").Append(f.FrameNumber.ToString(CultureInfo.InvariantCulture)).Append(",");
                sb.Append("\"time\":");
                AppendFloat(sb, f.TimelineTime);
                sb.Append(",");
                sb.Append("\"eyebrow_pattern\":").Append(f.EyebrowPattern.ToString(CultureInfo.InvariantCulture)).Append(",");
                sb.Append("\"eyebrow_open\":");
                AppendFloat(sb, f.EyebrowOpen);
                sb.Append(",");
                sb.Append("\"eyes_pattern\":").Append(f.EyesPattern.ToString(CultureInfo.InvariantCulture)).Append(",");
                sb.Append("\"eyes_open\":");
                AppendFloat(sb, f.EyesOpen);
                sb.Append(",");
                sb.Append("\"mouth_pattern\":").Append(f.MouthPattern.ToString(CultureInfo.InvariantCulture)).Append(",");
                sb.Append("\"mouth_open\":");
                AppendFloat(sb, f.MouthOpen);

                sb.Append(",\"facial_blendshapes\":[");
                if (f.BlendShapes != null)
                {
                    for (int j = 0; j < f.BlendShapes.Count; j++)
                    {
                        RawBlendShapeSample sample = f.BlendShapes[j];
                        if (j > 0) sb.Append(",");
                        sb.Append("{\"renderer\":\"").Append(EscapeJson(sample.RendererName)).Append("\",");
                        sb.Append("\"name\":\"").Append(EscapeJson(sample.BlendShapeName)).Append("\",");
                        sb.Append("\"weight\":");
                        AppendFloat(sb, sample.WeightPercent);
                        sb.Append("}");
                    }
                }
                sb.Append("]");

                sb.Append(",\"hitomi_transforms\":[");
                if (f.HitomiTransforms != null)
                {
                    for (int j = 0; j < f.HitomiTransforms.Count; j++)
                    {
                        RawHitomiSample sample = f.HitomiTransforms[j];
                        if (j > 0) sb.Append(",");
                        sb.Append("{\"name\":\"").Append(EscapeJson(sample.Name)).Append("\",");
                        sb.Append("\"local_position\":");
                        AppendVector3(sb, sample.LocalPosition);
                        sb.Append(",\"local_rotation\":");
                        AppendQuaternion(sb, sample.LocalRotation);
                        sb.Append(",\"local_euler\":");
                        AppendVector3(sb, sample.LocalEuler);
                        sb.Append(",\"local_scale\":");
                        AppendVector3(sb, sample.LocalScale);
                        sb.Append("}");
                    }
                }
                sb.Append("]");

                sb.Append("}");
                if (i + 1 < frames.Count) sb.Append(",");
                sb.Append("\n");
            }

            sb.Append("  ]\n");
            sb.Append("}\n");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
    }


    /// <summary>
    /// Shenhe-facing VMD morph mapper.
    /// This intentionally uses the final live KK BlendShape state captured after Timeline/IK updates,
    /// and converts only the small set of facial controls that have been visually calibrated on Shenhe.
    /// Unknown KK shapes are ignored rather than guessed.
    ///
    /// V0 note: iris/hitomi translation is still preserved by RawFacialExporter JSON. The current
    /// Shenhe runtime controls iris translation as transform millimetres rather than a VMD morph,
    /// so this mapper does not invent a VMD channel for it yet.
    /// </summary>
    internal static class ShenheFacialVmdMapper
    {
        private static float SourceWeight(RawFacialFrame frame, string blendShapeName)
        {
            if (frame == null || frame.BlendShapes == null || string.IsNullOrEmpty(blendShapeName))
                return 0f;

            float maxWeight = 0f;
            foreach (var sample in frame.BlendShapes)
            {
                if (sample == null) continue;
                if (!string.Equals(sample.BlendShapeName, blendShapeName, StringComparison.Ordinal))
                    continue;
                maxWeight = Mathf.Max(maxWeight, sample.WeightPercent * 0.01f);
            }
            return Mathf.Clamp01(maxWeight);
        }

        private static void AddMax(Dictionary<string, VmdMorphFrame> frames, uint frameNumber, string morphName, float weight)
        {
            weight = Mathf.Clamp01(weight);
            if (weight <= 1E-04f || string.IsNullOrEmpty(morphName)) return;

            VmdMorphFrame existing;
            if (frames.TryGetValue(morphName, out existing))
            {
                existing.Weight = Mathf.Max(existing.Weight, weight);
            }
            else
            {
                frames[morphName] = new VmdMorphFrame
                {
                    MorphName = morphName,
                    FrameNumber = frameNumber,
                    Weight = weight
                };
            }
        }

        public static List<VmdMorphFrame> Map(RawFacialFrame frame)
        {
            var output = new Dictionary<string, VmdMorphFrame>();
            if (frame == null) return output.Values.ToList();

            uint n = frame.FrameNumber;

            // Eyes
            float eyeSmile = SourceWeight(frame, "eye_face.f00_egao_cl");
            float eyeDefaultClosed = SourceWeight(frame, "eye_face.f00_def_cl");

            // Treat only the very closed end of def_cl as a real blink/held-eye-close.
            // This deliberately ignores the common 0.4~0.6 half-lid values that looked nearly neutral on Shenhe.
            float blink = Mathf.Clamp01((eyeDefaultClosed - 0.85f) / 0.15f);
            AddMax(output, n, "まばたき", blink);
            AddMax(output, n, "笑い", eyeSmile);

            // Brows / mouths used by the calibrated anchors.
            float browWorried = Mathf.Max(
                SourceWeight(frame, "mayuge.mayu00_koma_op"),
                SourceWeight(frame, "mayuge.mayu00_koma_cl"));

            float mouthAkire = SourceWeight(frame, "kuti_face.f00_akire_op");
            float mouthOdoro = SourceWeight(frame, "kuti_face.f00_odoro_op");
            float mouthIS = SourceWeight(frame, "kuti_face.f00_i_s_op");
            float mouthKeno = SourceWeight(frame, "kuti_face.f00_keno_op");
            float mouthSinken03 = Mathf.Max(
                SourceWeight(frame, "kuti_face.f00_sinken03_cl"),
                SourceWeight(frame, "kuti_face.f00_sinken03_op"));
            float mouthIkariPair = Mathf.Clamp01(
                SourceWeight(frame, "kuti_face.f00_ikari_cl") +
                SourceWeight(frame, "kuti_face.f00_ikari02_op"));

            float anyCalibratedMouth = Mathf.Max(
                Mathf.Max(mouthAkire, mouthOdoro),
                Mathf.Max(mouthIS, Mathf.Max(mouthKeno, Mathf.Max(mouthSinken03, mouthIkariPair))));

            // 9.60s anchor: closed smile eye + worried brow + smiling E mouth.
            if (eyeSmile > 1E-04f)
            {
                AddMax(output, n, "困る", 0.50f * eyeSmile);
                AddMax(output, n, "口角上げ", 0.50f * eyeSmile);
                AddMax(output, n, "え", 1.00f * eyeSmile);
            }

            // 8.53s vs 5.82s share the same KK akire mouth but differ by eye state.
            // Interpolate between the two calibrated Shenhe anchors using the strong-eye-close factor.
            if (mouthAkire > 1E-04f)
            {
                float closedMix = blink;
                float openMix = 1f - closedMix;

                // 8.53s: mabataki 1, worried 0.2, narrow 0.4, O 0.35
                AddMax(output, n, "困る", mouthAkire * closedMix * 0.20f);
                AddMax(output, n, "口横狭め", mouthAkire * closedMix * 0.40f);
                AddMax(output, n, "お", mouthAkire * closedMix * 0.35f);

                // 5.82s: worried 0.5, corner-down 0.7, E 0.5, narrow 0.6
                AddMax(output, n, "困る", mouthAkire * openMix * 0.50f);
                AddMax(output, n, "口角下げ", mouthAkire * openMix * 0.70f);
                AddMax(output, n, "え", mouthAkire * openMix * 0.50f);
                AddMax(output, n, "口横狭め", mouthAkire * openMix * 0.60f);
            }

            // 9.88s anchor: large round open mouth.
            if (mouthOdoro > 1E-04f)
            {
                AddMax(output, n, "困る", 0.40f * mouthOdoro);
                AddMax(output, n, "お", 1.00f * mouthOdoro);
            }

            // 2.20s anchor: slight smile/teeth geometry, deliberately very little E.
            if (mouthIS > 1E-04f)
            {
                AddMax(output, n, "口角上げ", 0.60f * mouthIS);
                AddMax(output, n, "え", 0.10f * mouthIS);
            }

            // "I'm sorry" scene anchor: keno_op is primarily a small rounded-open mouth on Shenhe.
            // Tooth renderer animation is intentionally ignored; only the lip silhouette is transferred.
            if (mouthKeno > 1E-04f)
            {
                AddMax(output, n, "お", 0.40f * mouthKeno);
            }

            // 1.83s anchor: mixed ikari_cl + ikari02_op geometry.
            if (mouthIkariPair > 1E-04f)
            {
                AddMax(output, n, "困る", 0.50f * mouthIkariPair);
                AddMax(output, n, "口横広げ", 0.35f * mouthIkariPair);
                AddMax(output, n, "え", 0.50f * mouthIkariPair);
            }

            // 0.00s anchor: closed/tense baseline; the mouth itself was subtle enough to omit.
            if (mouthSinken03 > 1E-04f)
            {
                AddMax(output, n, "困る", 0.50f * mouthSinken03);
            }

            // If a scene only contributes the worried brow, preserve it with the established 0.5 cap.
            if (anyCalibratedMouth <= 1E-04f && eyeSmile <= 1E-04f && browWorried > 1E-04f)
            {
                AddMax(output, n, "困る", 0.50f * browWorried);
            }

            return output.Values.ToList();
        }
    }

    [BepInPlugin("com.rintrint.kkbridge", "KKBridge", "0.0.6")]
    public class KKBridgePlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private bool _isExporting = false;
        private GameObject _uiPanel;
        private static Vector2 _uiPanelPosition = new Vector2(-Screen.width * 0.5f + 300, 100f);
        private Text _exportButtonText;
        private ConfigEntry<KeyboardShortcut> _toggleWindowHotkey;
        private ConfigEntry<string> _outputDirectory;
        private ConfigEntry<bool> _normalizeRootToFirstFrame;
        private ConfigEntry<bool> _exportRawFacialJson;
        private ConfigEntry<bool> _exportShenheFacialVmd;

        private void Awake()
        {
            Log = base.Logger;

            // --- 設定快捷鍵等東西 ---
            {
                _toggleWindowHotkey = Config.Bind(
                    "Hotkeys Settings", // 設定的小分類
                    "Toggle Window", // 設定的名稱
                    new KeyboardShortcut(KeyCode.F7), // 預設值
                    "Toggles the KKBridge window." // 滑鼠懸停時顯示的說明文字
                );

                string defaultOutputPath = Path.Combine(BepInEx.Paths.PluginPath, "KKBridge");
                defaultOutputPath = Path.Combine(defaultOutputPath, "out");
                _outputDirectory = Config.Bind(
                    "Export Settings", // 設定的小分類
                    "Output Directory", // 設定的名稱
                    defaultOutputPath, // 預設值
                    "Export destination folder." // 滑鼠懸停時顯示的說明文字
                );

                _normalizeRootToFirstFrame = Config.Bind(
                    "Export Settings",
                    "Normalize Root To First Frame",
                    true,
                    "Remove the character's initial CharaStudio world placement from 全ての親 while preserving later root motion."
                );

                _exportRawFacialJson = Config.Bind(
                    "Export Settings",
                    "Export Raw Facial JSON",
                    true,
                    "Export raw KK facial control channels next to the VMD. No PMX/BlendShape remapping is applied."
                );

                _exportShenheFacialVmd = Config.Bind(
                    "Export Settings",
                    "Export Shenhe Facial VMD",
                    true,
                    "Bake the visually calibrated Shenhe facial morphs directly into the exported VMD. Unknown KK facial shapes are ignored."
                );
            }

            SceneManager.sceneLoaded += OnSceneLoaded;

            // Log.LogInfo("KKBridge Plugin loaded!");
        }

        /// <summary>
        /// 當插件被銷毀時，取消註冊事件以避免內存洩漏
        /// </summary>
        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        /// <summary>
        /// 當場景加載完成時會被呼叫的方法
        /// </summary>
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (Singleton<Studio.Studio>.Instance != null)
            {
                // 在正確的時機初始化按鈕
                InitializeButton();
            }
        }

        /// <summary>
        /// 插件載入後立即執行。這是 Unity 的生命週期函式，在腳本啟用時會被呼叫一次。
        /// </summary>
        private void Start()
        {
            // 當插件啟動時（包括熱重載），檢查 Studio 單例是否存在
            // 如果是，就立即嘗試初始化按鈕
            if (Singleton<Studio.Studio>.Instance != null)
            {
                InitializeButton();
            }
        }

        private void InitializeButton()
        {
            // 強制銷毀熱重載時可能殘留的舊視窗，執行兩次來避免自動打開新視窗
            ToggleKkBridgeWindow();
            ToggleKkBridgeWindow();

            StartCoroutine(CreateKKBridgeButton_Coroutine());
        }

        private void Update()
        {
            if (_toggleWindowHotkey.Value.IsDown())
            {
                ToggleKkBridgeWindow();
            }
        }

        private void ToggleKkBridgeWindow()
        {
            // 在打開/創建視窗前，先檢查並銷毀所有舊的
            GameObject[] existingPanels = GameObject.FindObjectsOfType<GameObject>().Where(go => go.name == "KKBridgeCanvas").ToArray();
            if (existingPanels.Length > 0)
            {
                // 記住最後一個面板的位置
                GameObject lastPanel = existingPanels[existingPanels.Length - 1];
                Transform panelTransform = lastPanel.transform.Find("KKBridgeBorderPanel");
                if (panelTransform != null)
                {
                    RectTransform panelRect = panelTransform.GetComponent<RectTransform>();
                    if (panelRect != null)
                    {
                        // 記住關閉前的視窗位置
                        _uiPanelPosition = panelRect.anchoredPosition;
                    }
                }

                foreach (GameObject panel in existingPanels)
                {
                    Destroy(panel);
                }
                _uiPanel = null; // 清空引用
                return;
            }

            // 如果面板不存在 (或剛被銷毀)，就創建它
            if (_uiPanel == null)
            {
                CreateUIPanel();
            }
            else // 這個分支在當前邏輯下不會被觸發，但保留以備未來修改
            {
                _uiPanel.SetActive(!_uiPanel.activeSelf);
            }
        }

        #region UI 圓角效果相關方法

        /// <summary>
        /// 為 UI 元素替換圓角 Sprite (純視覺，無裁剪功能)
        /// </summary>
        private void ApplyRoundedSprite(GameObject target, float cornerRadius)
        {
            // 獲取或添加 Image 元件
            Image image = target.GetComponent<Image>();
            if (image == null)
            {
                image = target.AddComponent<Image>();
            }

            // 將 Image 的 Sprite 設置為我們創建的圓角 Sprite
            image.sprite = CreateRoundedSprite((int)cornerRadius);
            image.type = Image.Type.Sliced; // 使用 Sliced 模式確保圓角在縮放時不變形
        }

        /// <summary>
        /// 創建圓角 Sprite
        /// </summary>
        private Sprite CreateRoundedSprite(int cornerRadius)
        {
            int size = cornerRadius * 4;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);

            // 創建像素陣列
            Color32[] pixels = new Color32[size * size];

            // 填充圓角矩形
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool shouldFill = true;

                    // 檢查四個角
                    Vector2 center = Vector2.zero;
                    float distance = 0.0f;

                    // 左下角
                    if (x < cornerRadius && y < cornerRadius)
                    {
                        center = new Vector2(cornerRadius - 0.5f, cornerRadius - 0.5f);
                        distance = Vector2.Distance(new Vector2(x, y), center);
                        shouldFill = distance <= cornerRadius;
                    }
                    // 右下角
                    else if (x >= size - cornerRadius && y < cornerRadius)
                    {
                        center = new Vector2(size - cornerRadius - 0.5f, cornerRadius - 0.5f);
                        distance = Vector2.Distance(new Vector2(x, y), center);
                        shouldFill = distance <= cornerRadius;
                    }
                    // 左上角
                    else if (x < cornerRadius && y >= size - cornerRadius)
                    {
                        center = new Vector2(cornerRadius - 0.5f, size - cornerRadius - 0.5f);
                        distance = Vector2.Distance(new Vector2(x, y), center);
                        shouldFill = distance <= cornerRadius;
                    }
                    // 右上角
                    else if (x >= size - cornerRadius && y >= size - cornerRadius)
                    {
                        center = new Vector2(size - cornerRadius - 0.5f, size - cornerRadius - 0.5f);
                        distance = Vector2.Distance(new Vector2(x, y), center);
                        shouldFill = distance <= cornerRadius;
                    }

                    // 直角部分設為完全透明
                    pixels[y * size + x] = shouldFill ? Color.white : Color.clear;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();

            // 創建帶有邊框設定的 Sprite
            Vector4 border = new Vector4(cornerRadius, cornerRadius, cornerRadius, cornerRadius);
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
        }
        #endregion

        #region UI 創建相關方法

        private void CreateUIPanel()
        {
            Color panelBackgroundColor = new Color32(214, 214, 214, 255);
            Color titleBarColor = new Color32(82, 82, 78, 255);
            Color borderColor = new Color32(60, 60, 60, 255);
            Color buttonBackgroundColor = new Color32(125, 125, 125, 255);
            Color titleTextColor = Color.white;
            Color buttonTextColor = Color.white;

            // 1. 創建 Canvas
            GameObject canvasObj = new GameObject("KKBridgeCanvas");
            Canvas canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 0;
            canvasObj.AddComponent<CanvasScaler>();
            canvasObj.AddComponent<GraphicRaycaster>();
            _uiPanel = canvasObj;

            // 視窗大小
            int panelWidth = 300;
            int panelHeight = 200;
            int titleBarHeight = 35;

            // 2. 創建外邊框 (使用最簡單的父子結構)
            GameObject borderObj = new GameObject("KKBridgeBorderPanel");
            borderObj.transform.SetParent(canvasObj.transform, false);
            Image borderImage = borderObj.AddComponent<Image>();
            borderImage.color = borderColor;
            RectTransform borderRect = borderObj.GetComponent<RectTransform>();
            borderRect.sizeDelta = new Vector2(panelWidth, panelHeight);
            borderRect.anchoredPosition = _uiPanelPosition;

            // 3. 創建主面板
            GameObject panelObj = new GameObject("KKBridgePanel");
            panelObj.transform.SetParent(borderObj.transform, false);
            Image panelImage = panelObj.AddComponent<Image>();
            panelImage.color = panelBackgroundColor;
            RectTransform panelRect = panelObj.GetComponent<RectTransform>();
            panelRect.sizeDelta = new Vector2(panelWidth - 6, panelHeight - 6);
            panelRect.anchoredPosition = Vector2.zero;

            // 4. 創建 TitleBar
            GameObject titleBarObj = new GameObject("TitleBar");
            titleBarObj.transform.SetParent(panelObj.transform, false);
            Image titleBarImage = titleBarObj.AddComponent<Image>();
            titleBarImage.color = titleBarColor;
            var dragger = titleBarObj.AddComponent<DraggableWindow>();
            dragger.TargetRect = borderRect;
            RectTransform titleBarRect = titleBarObj.GetComponent<RectTransform>();
            titleBarRect.anchorMin = new Vector2(0, 1);
            titleBarRect.anchorMax = new Vector2(1, 1);
            titleBarRect.pivot = new Vector2(0.5f, 1);
            titleBarRect.sizeDelta = new Vector2(0, titleBarHeight);
            titleBarRect.anchoredPosition = new Vector2(0, 0);

            // 標題文字
            GameObject titleTextObj = new GameObject("TitleText");
            titleTextObj.transform.SetParent(titleBarObj.transform, false);
            Text titleText = titleTextObj.AddComponent<Text>();
            titleText.text = "KKBridge";
            titleText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            titleText.resizeTextForBestFit = true;
            titleText.alignment = TextAnchor.MiddleCenter;
            titleText.color = titleTextColor;
            RectTransform titleTextRect = titleTextObj.transform as RectTransform;
            titleTextRect.anchorMin = Vector2.zero;
            titleTextRect.anchorMax = Vector2.one;
            titleTextRect.sizeDelta = Vector2.zero;
            titleTextRect.anchoredPosition = Vector2.zero;

            // 5. 創建按鈕
            var exportAnimButtonObj = CreateUIPanelButton("ExportAnimButton", panelObj.transform, "Export Timeline to VMD", new Vector2(0, 70 - titleBarHeight), buttonBackgroundColor, buttonTextColor, () => { StartCoroutine(ExportTimelineAnimation_Coroutine()); });
            _exportButtonText = exportAnimButtonObj.GetComponentInChildren<Text>();

            // 6. 創建關閉按鈕
            GameObject closeButtonObj = new GameObject("CloseButton");
            closeButtonObj.transform.SetParent(panelObj.transform, false);
            Image closeButtonImage = closeButtonObj.AddComponent<Image>();
            closeButtonImage.color = buttonBackgroundColor;
            Button closeButton = closeButtonObj.AddComponent<Button>();
            closeButton.onClick.AddListener(ToggleKkBridgeWindow);
            RectTransform closeButtonRect = closeButtonObj.GetComponent<RectTransform>();
            closeButtonRect.anchorMin = new Vector2(1, 1);
            closeButtonRect.anchorMax = new Vector2(1, 1);
            closeButtonRect.pivot = new Vector2(1, 1);
            closeButtonRect.sizeDelta = new Vector2(24, 24);
            closeButtonRect.anchoredPosition = new Vector2(-5, -5);

            GameObject closeTextObj = new GameObject("CloseText");
            closeTextObj.transform.SetParent(closeButtonObj.transform, false);
            Text closeText = closeTextObj.AddComponent<Text>();
            closeText.text = "X";
            closeText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            closeText.resizeTextForBestFit = true;
            closeText.alignment = TextAnchor.MiddleCenter;
            closeText.color = titleTextColor;
            RectTransform closeTextRect = closeTextObj.transform as RectTransform;
            closeTextRect.anchorMin = Vector2.zero;
            closeTextRect.anchorMax = Vector2.one;
            closeTextRect.sizeDelta = Vector2.zero;

            // 7. 應用圓角 Sprite
            ApplyRoundedSprite(borderObj, 6f); // 邊框圓角 (半徑可自行調整)
            ApplyRoundedSprite(panelObj, 4f); // 主面板圓角
            ApplyRoundedSprite(titleBarObj, 4f); // 標題欄圓角 (只會圓潤頂部，因為底部被面板覆蓋)
            ApplyRoundedSprite(closeButtonObj, 2f); // 關閉按鈕圓角
        }

        /// <summary>
        /// 創建面板按鈕的輔助方法
        /// </summary>
        private GameObject CreateUIPanelButton(string name, Transform parent, string buttonText, Vector2 position, Color bgColor, Color textColor, Action onClickAction)
        {
            GameObject buttonObj = new GameObject(name);
            buttonObj.transform.SetParent(parent, false);

            Image image = buttonObj.AddComponent<Image>();
            image.color = bgColor;

            Button button = buttonObj.AddComponent<Button>();
            button.onClick.AddListener(() => { onClickAction?.Invoke(); });

            RectTransform rect = buttonObj.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(280, 40);
            rect.anchoredPosition = position;

            GameObject textObj = new GameObject("Text");
            textObj.transform.SetParent(buttonObj.transform, false);
            Text text = textObj.AddComponent<Text>();
            text.text = buttonText;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.resizeTextForBestFit = true;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = textColor;
            RectTransform textRect = textObj.transform as RectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;

            // 為按鈕應用圓角 Sprite
            ApplyRoundedSprite(buttonObj, 6f);

            return buttonObj;
        }
        #endregion

        private System.Collections.IEnumerator CreateKKBridgeButton_Coroutine()
        {
            // 等其他插件，控制按鈕位置
            yield return new WaitForSeconds(0.3f);

            string buttonPath = "StudioScene/Canvas System Menu/01_Button/KKBridge Button";
            GameObject existingButton = GameObject.Find(buttonPath);
            if (existingButton != null)
            {
                Destroy(existingButton);
                yield return null;
            }

            try
            {
                Transform parentPanel = GameObject.Find("StudioScene/Canvas System Menu/01_Button")?.transform;
                if (parentPanel == null || parentPanel.childCount == 0)
                {
                    Log.LogError("[KKBridgeButton] ERROR: Could not find the UI panel '.../01_Button'.");
                    yield break;
                }

                Texture2D iconTexture = LoadImageFromAssembly("KKBridge.Resources.Icon.png");
                if (iconTexture == null)
                {
                    Log.LogError("[KKBridgeButton] ERROR: Failed to load embedded resource 'KKBridge.Resources.Icon.png' from DLL.");
                    yield break;
                }

                GameObject templateButtonObj = parentPanel.GetChild(0).gameObject;
                GameObject kkBridgeButtonObj = Instantiate(templateButtonObj);
                kkBridgeButtonObj.name = "KKBridge Button";

                Image kkBridgeButtonImage = kkBridgeButtonObj.GetComponent<Image>();
                if (kkBridgeButtonImage != null)
                {
                    kkBridgeButtonImage.sprite = Sprite.Create(iconTexture, new Rect(0, 0, iconTexture.width, iconTexture.height), new Vector2(0.5f, 0.5f));
                    kkBridgeButtonImage.color = Color.white;
                }

                kkBridgeButtonObj.transform.SetParent(parentPanel, true);
                kkBridgeButtonObj.transform.localScale = templateButtonObj.transform.localScale;

                var childButtons = new List<RectTransform>();
                foreach (Transform child in parentPanel) childButtons.Add(child as RectTransform);
                Vector2 basePosition = new Vector2(0f, float.MaxValue);
                foreach (RectTransform buttonRect in childButtons)
                {
                    if (buttonRect.anchoredPosition.x >= basePosition.x && buttonRect.anchoredPosition.y <= buttonRect.anchoredPosition.y)
                        basePosition = buttonRect.anchoredPosition;
                }
                while (childButtons.Any(c => c.name != kkBridgeButtonObj.name && (c.anchoredPosition - basePosition).sqrMagnitude < 4f))
                {
                    basePosition.y += 40f;
                }
                ((RectTransform)kkBridgeButtonObj.transform).anchoredPosition = basePosition;

                Button buttonComponent = kkBridgeButtonObj.GetComponent<Button>();
                if (buttonComponent != null)
                {
                    buttonComponent.interactable = true;

                    buttonComponent.onClick = new Button.ButtonClickedEvent();
                    buttonComponent.onClick.AddListener(ToggleKkBridgeWindow);
                }
            }
            catch (Exception e)
            {
                Log.LogError($"[KKBridgeButton] CRITICAL ERROR: An error occurred while adding the button: {e.ToString()}");
            }
        }

        /// <summary>
        /// 導出 Timeline 動畫的協程
        /// </summary>
        private System.Collections.IEnumerator ExportTimelineAnimation_Coroutine()
        {
            if (_isExporting)
            {
                // 如果正在導出時再次點擊，則應視為取消操作
                // 停止 Timeline 播放
                if (TimelineCompatibility.GetIsPlaying())
                {
                    TimelineCompatibility.Play(); // Play() 在播放時會觸發暫停
                }
                _isExporting = false;
                Log.LogInfo("Export cancelled by user.");
                yield break;
            }

            int? originalCaptureFramerate = null;
            try
            {
                // --- 階段一：準備工作 ---
                _isExporting = true;
                if (_exportButtonText != null) _exportButtonText.text = "Exporting...";
                Log.LogInfo("Starting Timeline animation export...");

                if (!TimelineCompatibility.Init())
                {
                    Log.LogError("Timeline plugin not found or failed to initialize. Cannot export animation.");
                    _isExporting = false;
                    if (_exportButtonText != null) _exportButtonText.text = "Export Timeline to VMD";
                    yield break;
                }

                var studioInstance = Singleton<Studio.Studio>.Instance;
                var characters = studioInstance.dicObjectCtrl.Values.OfType<OCIChar>().ToList();
                if (!characters.Any())
                {
                    Log.LogWarning("No characters in the scene, export cancelled.");
                    _isExporting = false;
                    if (_exportButtonText != null) _exportButtonText.text = "Export Timeline to VMD";
                    yield break;
                }

                // 設定通用導出參數
                const int fps = 30;
                float timelineDuration = TimelineCompatibility.GetDuration();
                if (timelineDuration <= 0)
                {
                    Log.LogWarning("Timeline duration is 0 or invalid, export cancelled.");
                    _isExporting = false;
                    if (_exportButtonText != null) _exportButtonText.text = "Export Timeline to VMD";
                    yield break;
                }
                Log.LogInfo($"Timeline duration: {timelineDuration:F2} seconds for {characters.Count} character(s).");
                Log.LogInfo($"Baking VMD at {fps}fps.");

                // 儲存並設定 Time.captureFramerate
                originalCaptureFramerate = Time.captureFramerate;
                Time.captureFramerate = fps;

                // --- 階段二：初始化資料結構並開始錄製 ---
                var boneProcessor = new VmdBoneProcessor(Log, _normalizeRootToFirstFrame.Value);
                var morphProcessor = new VmdMorphProcessor(Log);
                var cameraProcessor = new VmdCameraProcessor(Log);

                var allCharactersBoneFrames = new Dictionary<OCIChar, List<VmdBoneFrame>>();
                var allCharactersMorphFrames = new Dictionary<OCIChar, List<VmdMorphFrame>>();
                var allCharactersRawFacialFrames = new Dictionary<OCIChar, List<RawFacialFrame>>();
                var rawFacialCaptureContexts = new Dictionary<OCIChar, RawFacialCaptureContext>();
                var allCameraFrames = new List<VmdCameraFrame>();

                var characterBoneCaches = new Dictionary<OCIChar, Dictionary<string, Transform>>();
                foreach (var ociChar in characters)
                {
                    // 為每個角色創建 VMD 影格列表
                    allCharactersBoneFrames[ociChar] = new List<VmdBoneFrame>();
                    allCharactersMorphFrames[ociChar] = new List<VmdMorphFrame>();
                    allCharactersRawFacialFrames[ociChar] = new List<RawFacialFrame>();
                    if (_exportRawFacialJson.Value || _exportShenheFacialVmd.Value)
                    {
                        rawFacialCaptureContexts[ociChar] = RawFacialExporter.CreateContext(ociChar);
                    }

                    // 為每個角色預先建立並儲存骨骼快取
                    var boneCacheForChar = new Dictionary<string, Transform>();
                    Transform instanceRootTf = ociChar.charInfo.transform;
                    if (instanceRootTf != null)
                    {
                        foreach (var entry in BoneMapper.GetAllEntries())
                        {
                            Transform boneTf = FindDescendant(instanceRootTf, entry.KkName);
                            if (boneTf != null)
                            {
                                boneCacheForChar[entry.KkName] = boneTf;
                            }
                        }
                    }
                    characterBoneCaches[ociChar] = boneCacheForChar;
                }

                // 將 Timeline 開始播放
                if (!TimelineCompatibility.GetIsPlaying())
                {
                    TimelineCompatibility.Play();
                }

                // ** 核心循環：推進影格，並在每一影格內處理所有角色 **
                int currentFrame = 0;
                float previousTime = -1f;

                while (_isExporting) // 增加一個開關，以便可以從外部停止
                {
                    // 1. 等待下一影格
                    yield return new WaitForEndOfFrame();

                    // 2. 獲取當前播放時間
                    float currentTime = TimelineCompatibility.GetPlaybackTime();

                    // 如果用戶在錄製過程中點擊了取消，_isExporting 會變為 false
                    if (!_isExporting)
                    {
                        Log.LogInfo("Recording stopped by cancellation.");
                        break;
                    }

                    // 3. 檢查播放是否暫停、結束、循環、倒退
                    if (currentTime <= previousTime)
                    {
                        Log.LogInfo($"Detected Timeline pause/end/loop/rewind. Stopping recording.");
                        break;
                    }

                    // 4. 在這一影格內，遍歷所有角色並收集數據
                    foreach (var ociChar in characters)
                    {
                        Transform instanceRootTf = ociChar.charInfo.transform;

                        if (!characterBoneCaches.TryGetValue(ociChar, out var BoneCache) || instanceRootTf == null)
                        {
                            // 快速失敗：記錄詳細錯誤並跳過這個角色
                            string charName = ociChar.charInfo.chaFile.parameter.fullname;
                            Log.LogError($"[KKBridge] Failed to process character '{charName}'. Bone cache or root transform not found. Skipping this character.");
                            continue; // 跳到下一個角色
                        }

                        // 呼叫處理器來獲取當前影格的數據
                        List<VmdBoneFrame> singleFrameBoneData = boneProcessor.ProcessCharacter(instanceRootTf, BoneCache);
                        foreach (var boneFrame in singleFrameBoneData)
                        {
                            boneFrame.FrameNumber = (uint)currentFrame;
                        }
                        // 將當前影格的數據添加到對應角色的列表中
                        allCharactersBoneFrames[ociChar].AddRange(singleFrameBoneData);

                        RawFacialFrame rawFaceFrame = null;
                        if (_exportRawFacialJson.Value || _exportShenheFacialVmd.Value)
                        {
                            rawFaceFrame = RawFacialExporter.Capture(
                                ociChar,
                                (uint)currentFrame,
                                currentTime,
                                rawFacialCaptureContexts[ociChar]);

                            if (_exportRawFacialJson.Value && rawFaceFrame != null)
                            {
                                allCharactersRawFacialFrames[ociChar].Add(rawFaceFrame);
                            }
                        }

                        List<VmdMorphFrame> singleFrameMorphData;
                        if (_exportShenheFacialVmd.Value)
                        {
                            singleFrameMorphData = ShenheFacialVmdMapper.Map(rawFaceFrame);
                        }
                        else
                        {
                            singleFrameMorphData = morphProcessor.ProcessCharacter(ociChar, (uint)currentFrame);
                        }
                        allCharactersMorphFrames[ociChar].AddRange(singleFrameMorphData);
                    }

                    // 錄製相機
                    var camFrame = cameraProcessor.ProcessFrame((uint)currentFrame);
                    if (camFrame != null)
                    {
                        allCameraFrames.Add(camFrame);
                    }

                    previousTime = currentTime;
                    currentFrame++;
                }

                Log.LogInfo($"Finished collecting frame data for all characters ({currentFrame} frames recorded).");

                // --- 階段三：對所有收集到的幀進行後處理優化 ---
                Log.LogInfo("Post-processing keyframes to remove redundant keyframes...");
                foreach (var ociChar in characters)
                {
                    string charName = ociChar.charInfo.chaFile.parameter.fullname;

                    // --- 處理表情幀 (Morph Frames) ---
                    var originalMorphFrames = allCharactersMorphFrames[ociChar];
                    Log.LogInfo($"Character '{charName}': Processing {originalMorphFrames.Count} raw morph frames...");

                    var processedMorphFrames = VmdExporter.PostProcessKeyframes(
                        originalMorphFrames,
                        frame => frame.MorphName,
                        frame => frame.FrameNumber,
                        (a, b) => Math.Abs(a.Weight - b.Weight) < 1E-06f,
                        frame => Math.Abs(frame.Weight) < 1E-06f
                    );
                    allCharactersMorphFrames[ociChar] = processedMorphFrames;
                    Log.LogInfo($"Character '{charName}': {processedMorphFrames.Count} optimized morph frames remaining.");


                    // --- 處理骨骼幀 (Bone Frames) ---
                    var originalBoneFrames = allCharactersBoneFrames[ociChar];
                    Log.LogInfo($"Character '{charName}': Processing {originalBoneFrames.Count} raw bone frames...");

                    var processedBoneFrames = VmdExporter.PostProcessKeyframes(
                        originalBoneFrames,
                        frame => frame.BoneName,
                        frame => frame.FrameNumber,
                        (a, b) =>
                        {
                            bool positionEqual = (a.Position - b.Position).sqrMagnitude < 1E-06f;
                            bool rotationEqual = Mathf.Abs(Quaternion.Dot(a.Rotation, b.Rotation)) > 0.999999f;
                            return positionEqual && rotationEqual;
                        },
                        frame =>
                        {
                            bool positionIsZero = frame.Position.sqrMagnitude < 1E-06f;
                            bool rotationIsIdentity = Mathf.Abs(Quaternion.Dot(frame.Rotation, Quaternion.identity)) > 0.999999f;
                            return positionIsZero && rotationIsIdentity;
                        }
                    );
                    allCharactersBoneFrames[ociChar] = processedBoneFrames;
                    Log.LogInfo($"Character '{charName}': {processedBoneFrames.Count} optimized bone frames remaining.");
                }

                // --- 處理相機幀 (Camera Frames) ---
                if (allCameraFrames.Count > 0)
                {
                    Log.LogInfo($"Camera: Processing {allCameraFrames.Count} raw frames...");

                    var processedCameraFrames = VmdExporter.PostProcessKeyframes(
                        allCameraFrames,
                        frame => "Camera", // 相機只有一軌，給定固定名稱分組
                        frame => frame.FrameNumber,
                        (a, b) =>
                        {
                            // 判斷兩個幀是否實質相等
                            // 1. 距離相等
                            bool distEq = Mathf.Abs(a.Distance - b.Distance) < 1E-06f;
                            // 2. 目標點位置相等
                            bool posEq = (a.TargetPosition - b.TargetPosition).sqrMagnitude < 1E-06f;
                            // 3. 旋轉相等 (歐拉角比較)
                            bool rotEq = (a.Rotation - b.Rotation).sqrMagnitude < 1E-06f;
                            // 4. FOV 相等
                            bool fovEq = a.Fov == b.Fov;

                            return distEq && posEq && rotEq && fovEq;
                        },
                        // IsDefault 判斷：相機軌道即使是靜止的也不應該被完全刪除，
                        // 所以這裡總是回傳 false，確保即使全場靜止也會保留至少一幀。
                        frame => false
                    );

                    allCameraFrames = processedCameraFrames;
                    Log.LogInfo($"Camera: {processedCameraFrames.Count} optimized frames remaining.");
                }

                // --- 階段四：導出所有角色的 VMD 檔案 ---
                string outputDirectory = _outputDirectory.Value;
                Directory.CreateDirectory(outputDirectory);

                int charIndex = 1;
                foreach (var ociChar in characters)
                {
                    ChaControl chaCtrl = ociChar.charInfo;
                    string charName = chaCtrl.chaFile.parameter.fullname;
                    List<VmdBoneFrame> boneFramesForThisChar = allCharactersBoneFrames[ociChar];
                    List<VmdMorphFrame> morphFramesForThisChar = allCharactersMorphFrames[ociChar];

                    Log.LogInfo($"--- Exporting VMD for character {charIndex}: {charName} ---");

                    if (boneFramesForThisChar.Count == 0)
                    {
                        Log.LogWarning($"No frames were recorded for character {charName}. Skipping VMD export for this character.");
                        charIndex++;
                        continue;
                    }

                    var ikFrames = new List<VmdIkFrame> { VmdIkFrame.CreateDefault() };

                    string vmdFileName = CreateSafeFileName(charIndex, "_", charName, "_timeline", ".vmd");
                    string vmdFilePath = Path.Combine(outputDirectory, vmdFileName);

                    try
                    {
                        VmdExporter.Export(boneFramesForThisChar, morphFramesForThisChar, ikFrames, "KoikatsuModel", vmdFilePath);
                        Log.LogInfo($"Successfully exported VMD animation to: {vmdFilePath}");
                    }
                    catch (Exception e)
                    {
                        Log.LogError($"Failed to export VMD for character {charName}: {e.Message}");
                    }

                    if (_exportRawFacialJson.Value)
                    {
                        try
                        {
                            string faceFileName = CreateSafeFileName(charIndex, "_", charName, "_face_raw", ".json");
                            string faceFilePath = Path.Combine(outputDirectory, faceFileName);
                            RawFacialExporter.Export(allCharactersRawFacialFrames[ociChar], fps, faceFilePath);
                            Log.LogInfo($"Successfully exported raw facial data to: {faceFilePath}");
                        }
                        catch (Exception e)
                        {
                            Log.LogError($"Failed to export raw facial data for character {charName}: {e.Message}");
                        }
                    }

                    charIndex++;
                }

                // 導出相機 VMD
                if (allCameraFrames.Count > 0)
                {
                    string camPath = Path.Combine(outputDirectory, "camera.vmd");
                    try
                    {
                        Log.LogInfo($"Exporting Camera VMD ({allCameraFrames.Count} frames)...");
                        VmdCameraProcessor.Export(allCameraFrames, camPath);
                        Log.LogInfo($"Success: {camPath}");
                    }
                    catch (Exception ex)
                    {
                        Log.LogError($"Camera Export Error: {ex.Message}");
                    }
                }
            }
            finally
            {
                // --- 階段五：無論成功或失敗，都執行清理工作 ---
                if (originalCaptureFramerate.HasValue)
                {
                    Time.captureFramerate = originalCaptureFramerate.Value; // 恢復影格率
                }

                // 確保 Timeline 停止播放
                if (TimelineCompatibility.GetIsPlaying())
                {
                    TimelineCompatibility.Play();
                }

                // 重置狀態
                _isExporting = false;
                if (_exportButtonText != null)
                {
                    _exportButtonText.text = "Export Timeline to VMD";
                }
                Log.LogInfo("Timeline animation export process finished.");
            }
        }

        #region Helper Methods (輔助函式區)

        /// <summary>
        /// 從當前 Assembly (DLL) 的嵌入式資源中載入圖片
        /// </summary>
        /// <param name="resourceName">資源的完整名稱 (專案名稱.檔案名稱)</param>
        /// <returns>載入後的 Texture2D 物件</returns>
        private Texture2D LoadImageFromAssembly(string resourceName)
        {
            try
            {
                // 獲取當前正在執行的 Assembly (也就是 KKBridge.dll)
                Assembly assembly = Assembly.GetExecutingAssembly();

                // 讀取嵌入式資源的數據流
                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return null;

                    // 將數據流讀入位元組陣列
                    byte[] buffer = new byte[stream.Length];
                    stream.Read(buffer, 0, buffer.Length);

                    // 從位元組陣列創建 Texture2D
                    // 尺寸參數 (2, 2) 不重要，LoadImage 會自動調整
                    Texture2D texture = new Texture2D(2, 2);
                    texture.LoadImage(buffer); // 這會自動解析PNG並載入

                    return texture;
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"Error loading image from Assembly: {ex.ToString()}");
                return null;
            }
        }

        /// <summary>
        /// 尋找指定名稱的後代 Transform (廣度優先 BFS)，更穩健。
        /// </summary>
        private Transform FindDescendant(Transform parent, string name)
        {
            if (parent == null) return null;

            var queue = new Queue<Transform>();
            queue.Enqueue(parent);

            while (queue.Count > 0)
            {
                Transform current = queue.Dequeue();

                if (current.name == name)
                {
                    return current;
                }

                foreach (Transform child in current)
                {
                    queue.Enqueue(child);
                }
            }
            return null; // 沒找到
        }

        /// <summary>
        /// 將一系列物件 (字串、數字等) 組合並清理成一個安全的檔案名稱。
        /// </summary>
        /// <param name="parts">要組合成檔名的各個部分。</param>
        /// <returns>一個不包含任何無效檔案字元的安全檔名。</returns>
        private static string CreateSafeFileName(params object[] parts)
        {
            // --- 步驟 1: 將所有傳入的參數組合成一個單一字串 ---
            // 使用 StringBuilder 提高效率
            var rawNameBuilder = new StringBuilder();
            foreach (object part in parts)
            {
                if (part != null)
                {
                    // 使用 CultureInfo.InvariantCulture 確保數字等類型轉換為字串時格式一致
                    rawNameBuilder.Append(string.Format(CultureInfo.InvariantCulture, "{0}", part));
                }
            }
            string rawFileName = rawNameBuilder.ToString();

            // --- 步驟 2: 移除組合後字串中的所有無效檔案字元 ---
            var safeNameBuilder = new StringBuilder(rawFileName.Length);
            // 預先獲取一次無效字元陣列
            char[] invalidChars = Path.GetInvalidFileNameChars();
            foreach (char c in rawFileName)
            {
                // 如果字元不是無效字元，則附加到最終結果中
                if (Array.IndexOf(invalidChars, c) < 0)
                {
                    safeNameBuilder.Append(c);
                }
            }

            return safeNameBuilder.ToString();
        }
        #endregion
    }
}
