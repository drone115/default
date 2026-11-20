// Placement Tool for Unity 6 (6000.3)
// Tools > Placement Tool で開くエディタ拡張。
//
// モード
//   ① 配置     : 選んだプレハブをシーンビューのクリック位置に配置
//   ② ライン   : 始点 → 終点をクリックし、その間を一定間隔で一直線に配置
//   ③ グリッド : グリッドにスナップして配置（間隔・原点を設定可能）
//   ④ 連結     : 直前に置いたパーツの端（バウンズの面）に次のパーツをくっつけて配置
//   ⑤ 上にコピー: 選択オブジェクトを指定の高さ（既定 4m）上に複製
//
// 共通ルール
//   - 回転とスケールはプレハブ（コピー元）の値のまま変更しない
//   - すべての操作は Undo（Ctrl+Z）で 1 操作単位で戻せる

using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace PlacementTool.Editor
{
    public class PlacementToolWindow : EditorWindow
    {
        enum Mode { Place, Line, Grid, Connect, CopyUp, Utility, ObjectSnap, Scatter }
        enum SnapYMode { SameAsBase, Step, Cursor }
        static readonly GUIContent[] SnapYLabels = { new GUIContent("基準と同じ高さ"), new GUIContent("ステップでスナップ"), new GUIContent("カーソル位置") };

        static readonly string[] ModeLabels = { "① 配置", "② ライン", "③ グリッド", "④ 連結", "⑤ 上にコピー", "⑥ 選択・名前", "⑦ オブジェクトスナップ", "⑧ ランダム散布" };
        const string Pref = "PlacementTool.";
        const int MaxLineCount = 1000;
        const int MaxPreviewCount = 300;
        static readonly int ControlHash = "PlacementToolWindow".GetHashCode();

        static readonly Color PreviewColor = new Color(0.30f, 0.80f, 1.00f, 1f);
        static readonly Color AnchorColor = new Color(1.00f, 0.60f, 0.20f, 1f);
        static readonly Color TargetColor = new Color(0.40f, 1.00f, 0.40f, 1f);
        static readonly Color GridColor = new Color(1f, 1f, 1f, 0.25f);

        // ---- 設定（ウィンドウに保持 + EditorPrefs に保存） ----
        [SerializeField] Mode mode;
        [SerializeField] GameObject prefab;
        [SerializeField] List<GameObject> palette = new List<GameObject>();
        [SerializeField] bool followProjectSelection;
        [SerializeField] bool sceneInputEnabled = true;
        [SerializeField] Transform parent;
        [SerializeField] bool alignBottom;
        [SerializeField] float groundHeight;
        [SerializeField] bool selectPlaced = true;
        [SerializeField] float placeYaw;          // 配置時に追加するY回転（0 ならプレハブのまま）
        [SerializeField] float rotateStep = 90f;

        [SerializeField] float lineSpacing = 2f;
        [SerializeField] bool lineSnapToGrid;
        [SerializeField] bool lineChain;

        [SerializeField] float gridSize = 1f;
        [SerializeField] Vector3 gridOrigin;
        [SerializeField] bool gridSnapY;

        [SerializeField] GameObject anchor;
        [SerializeField] float connectGap;
        [SerializeField] bool connectAllowVertical;

        [SerializeField] Vector3 objSnapStep = new Vector3(3f, 4f, 3f);
        [SerializeField] SnapYMode objSnapY = SnapYMode.SameAsBase;
        [SerializeField] bool objSnapLocalAxes;
        [SerializeField] bool objSnapUsePrefabRoot = true;
        [SerializeField] bool objSnapSkipBase = true;
        [SerializeField] bool objSnapShowCandidates = true;

        [SerializeField] float copyHeight = 4f;

        [SerializeField] bool renameIncludeChildren;
        [SerializeField] bool renameRemoveClone = true;
        [SerializeField] bool selectMatchVariantBase;
        [SerializeField] bool selectIncludeInactive = true;

        // ---- 一時状態 ----
        bool hasHover;
        Vector3 hoverPoint;

        bool lineHasStart;
        Vector3 lineStart;
        bool lineSkipFirst;

        bool connectHasTarget;

        GameObject snapBase;
        bool snapHasTarget;
        Vector3 snapTarget;
        Vector3 connectTargetCenter;
        Bounds connectAnchorBounds;
        int connectAxis;
        float connectSign;

        GameObject cachedBoundsPrefab;
        bool hasCachedBounds;
        Bounds prefabLocalBounds; // ピボット基準のバウンズ

        Vector2 scroll;

        static readonly MethodInfo UnityDuplicateMethod = typeof(Unsupported).GetMethod(
            "DuplicateGameObjectsUsingPasteboard", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);

        // =====================================================================
        // ウィンドウ
        // =====================================================================

        [MenuItem("Tools/Placement Tool")]
        static void Open()
        {
            var w = GetWindow<PlacementToolWindow>();
            w.titleContent = new GUIContent("Placement Tool", EditorGUIUtility.IconContent("Prefab Icon").image);
            w.minSize = new Vector2(320, 340);
            w.Show();
        }

        void OnEnable()
        {
            LoadPrefs();
            SceneView.duringSceneGui += OnSceneGUI;
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        void OnDisable()
        {
            SavePrefs();
            SceneView.duringSceneGui -= OnSceneGUI;
            Undo.undoRedoPerformed -= OnUndoRedo;
            SceneView.RepaintAll();
        }

        void OnFocus() => hasCachedBounds = false;
        void OnProjectChange()
        {
            hasCachedBounds = false;
            scatterBoundsCache.Clear();
            scatterPreviewDirty = true;
        }

        void OnSelectionChange()
        {
            if (followProjectSelection && Selection.activeObject is GameObject g && IsPrefabAsset(g))
                SetPrefab(g);
            scatterPreviewDirty = true;
            if (mode == Mode.Scatter) SceneView.RepaintAll();
            Repaint();
        }

        void OnUndoRedo()
        {
            // 連結モード: 直前のパーツが Undo で消えたら、復元された選択を基準にする
            if (!anchor && Selection.activeGameObject && !EditorUtility.IsPersistent(Selection.activeGameObject))
                anchor = Selection.activeGameObject;
            connectHasTarget = false;
            scatterPreviewDirty = true;
            Repaint();
            SceneView.RepaintAll();
        }

        void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            var newMode = (Mode)GUILayout.SelectionGrid((int)mode, ModeLabels, 4, GUILayout.Height(48));
            if (EditorGUI.EndChangeCheck())
            {
                mode = newMode;
                ResetTransientState();
                SavePrefs();
                SceneView.RepaintAll();
            }

            EditorGUILayout.Space(4);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUI.BeginChangeCheck();

            if (mode == Mode.CopyUp)
            {
                DrawCopyUpGUI();
            }
            else if (mode == Mode.Utility)
            {
                DrawUtilityGUI();
            }
            else if (mode == Mode.Scatter)
            {
                DrawScatterGUI();
            }
            else
            {
                DrawSceneInputToggle();
                EditorGUILayout.Space(4);
                DrawPrefabGUI();
                EditorGUILayout.Space(6);
                DrawCommonGUI();
                EditorGUILayout.Space(6);
                switch (mode)
                {
                    case Mode.Line: DrawLineGUI(); break;
                    case Mode.Grid: DrawGridGUI(); break;
                    case Mode.Connect: DrawConnectGUI(); break;
                    case Mode.ObjectSnap: DrawObjectSnapGUI(); break;
                }
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(GetHelpText(), MessageType.Info);

            if (EditorGUI.EndChangeCheck())
            {
                scatterPreviewDirty = true;
                SavePrefs();
                SceneView.RepaintAll();
            }
            EditorGUILayout.EndScrollView();
        }

        void DrawSceneInputToggle()
        {
            var prevColor = GUI.backgroundColor;
            GUI.backgroundColor = sceneInputEnabled ? new Color(0.5f, 1f, 0.5f) : Color.white;
            string label = sceneInputEnabled ? "シーンでのクリック配置：ON" : "シーンでのクリック配置：OFF（通常の選択操作）";
            if (GUILayout.Button(label, GUILayout.Height(24)))
            {
                sceneInputEnabled = !sceneInputEnabled;
                ResetTransientState();
            }
            GUI.backgroundColor = prevColor;
        }

        void DrawPrefabGUI()
        {
            EditorGUILayout.LabelField("プレハブ", EditorStyles.boldLabel);

            var picked = (GameObject)EditorGUILayout.ObjectField("配置するプレハブ", prefab, typeof(GameObject), false);
            if (picked != prefab)
            {
                if (picked == null || IsPrefabAsset(picked)) SetPrefab(picked);
                else ShowNotification(new GUIContent("プロジェクト内のプレハブを指定してください"));
            }

            followProjectSelection = EditorGUILayout.ToggleLeft("Project ウィンドウの選択に追従", followProjectSelection);

            if (prefab)
            {
                var size = GetPrefabLocalBounds().size;
                EditorGUILayout.LabelField("バウンズサイズ (X × Y × Z)",
                    $"{size.x:0.###} × {size.y:0.###} × {size.z:0.###} m", EditorStyles.miniLabel);
            }

            DrawPalette();
        }

        void DrawPalette()
        {
            EditorGUILayout.LabelField("パレット（D&Dで追加 / クリックで選択 / 右クリックで削除）", EditorStyles.miniLabel);
            palette.RemoveAll(p => p == null);

            const float cell = 64f;
            const float labelH = 16f;
            const float pad = 4f;
            float width = Mathf.Max(cell + pad * 2, EditorGUIUtility.currentViewWidth - 30f);
            int cols = Mathf.Max(1, Mathf.FloorToInt((width - pad) / (cell + pad)));
            int rows = Mathf.Max(1, Mathf.CeilToInt(palette.Count / (float)cols));
            Rect area = GUILayoutUtility.GetRect(width, rows * (cell + labelH + pad) + pad, GUILayout.ExpandWidth(true));

            GUI.Box(area, palette.Count == 0 ? new GUIContent("ここにプレハブをドラッグ＆ドロップ") : GUIContent.none);
            HandlePaletteDrop(area);

            Event e = Event.current;
            int removeIndex = -1;
            for (int i = 0; i < palette.Count; i++)
            {
                var item = palette[i];
                int r = i / cols, c = i % cols;
                var iconRect = new Rect(area.x + pad + c * (cell + pad), area.y + pad + r * (cell + labelH + pad), cell, cell);
                var cellRect = new Rect(iconRect.x, iconRect.y, cell, cell + labelH);

                if (item == prefab)
                    EditorGUI.DrawRect(new Rect(cellRect.x - 2, cellRect.y - 2, cellRect.width + 4, cellRect.height + 4),
                        new Color(0.24f, 0.49f, 0.90f, 0.8f));

                Texture tex = AssetPreview.GetAssetPreview(item);
                if (tex == null) tex = AssetPreview.GetMiniThumbnail(item);
                if (tex != null) GUI.DrawTexture(iconRect, tex, ScaleMode.ScaleToFit);
                GUI.Label(new Rect(cellRect.x, iconRect.yMax, cell, labelH), new GUIContent(item.name, item.name), EditorStyles.miniLabel);

                if (e.type == EventType.MouseDown && cellRect.Contains(e.mousePosition))
                {
                    if (e.button == 0)
                    {
                        SetPrefab(item);
                        if (e.clickCount == 2) EditorGUIUtility.PingObject(item);
                        e.Use();
                    }
                    else if (e.button == 1)
                    {
                        removeIndex = i;
                        e.Use();
                    }
                }
            }

            if (removeIndex >= 0)
            {
                palette.RemoveAt(removeIndex);
                SavePrefs();
            }

            if (AssetPreview.IsLoadingAssetPreviews()) Repaint();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Projectで選択中のプレハブを追加"))
                {
                    foreach (var g in Selection.objects.OfType<GameObject>().Where(IsPrefabAsset))
                        if (!palette.Contains(g)) palette.Add(g);
                    if (!prefab && palette.Count > 0) SetPrefab(palette[0]);
                    SavePrefs();
                }
                if (GUILayout.Button("クリア", GUILayout.Width(60)) && palette.Count > 0 &&
                    EditorUtility.DisplayDialog("Placement Tool", "パレットを空にしますか？", "OK", "キャンセル"))
                {
                    palette.Clear();
                    SavePrefs();
                }
            }
        }

        void HandlePaletteDrop(Rect area)
        {
            Event e = Event.current;
            if ((e.type != EventType.DragUpdated && e.type != EventType.DragPerform) || !area.Contains(e.mousePosition))
                return;

            var items = DragAndDrop.objectReferences.OfType<GameObject>().Where(IsPrefabAsset).ToList();
            if (items.Count == 0) return;

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (var g in items)
                    if (!palette.Contains(g)) palette.Add(g);
                SetPrefab(items[0]);
                SavePrefs();
            }
            e.Use();
        }

        void DrawCommonGUI()
        {
            EditorGUILayout.LabelField("配置設定", EditorStyles.boldLabel);
            parent = (Transform)EditorGUILayout.ObjectField(
                new GUIContent("親（任意）", "配置したオブジェクトをこの Transform の子にします"), parent, typeof(Transform), true);
            if (mode != Mode.Connect && mode != Mode.ObjectSnap)
                alignBottom = EditorGUILayout.Toggle(
                    new GUIContent("底面を接地", "ピボットではなくバウンズの底面をクリックした面に合わせます"), alignBottom);
            groundHeight = EditorGUILayout.FloatField(
                new GUIContent("地面の高さ", "何もない場所をクリックしたときに使う水平面の高さ (Y)"), groundHeight);
            selectPlaced = EditorGUILayout.Toggle("配置後に選択する", selectPlaced);
            using (new EditorGUILayout.HorizontalScope())
            {
                placeYaw = Mathf.Repeat(EditorGUILayout.FloatField(
                    new GUIContent("配置時の回転 Y (°)", "シーンビューで ←/→ キーで回転、↓ キーで 0 に戻す。0 ならプレハブの回転のまま"),
                    placeYaw), 360f);
                if (GUILayout.Button("0", GUILayout.Width(28))) placeYaw = 0f;
            }
            rotateStep = Mathf.Clamp(EditorGUILayout.FloatField(
                new GUIContent("回転ステップ (°)", "←/→ キー 1 回あたりの角度。Shift 押しながらで 1/6（90°なら15°）"), rotateStep), 1f, 180f);
        }

        void DrawLineGUI()
        {
            EditorGUILayout.LabelField("ライン設定", EditorStyles.boldLabel);
            lineSpacing = Mathf.Max(0.01f, EditorGUILayout.FloatField("間隔 (m)", lineSpacing));
            if (prefab)
            {
                var size = GetPrefabLocalBounds().size;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel(" ");
                    if (GUILayout.Button($"= 幅X ({size.x:0.##})", EditorStyles.miniButtonLeft)) lineSpacing = Mathf.Max(0.01f, size.x);
                    if (GUILayout.Button($"= 奥行Z ({size.z:0.##})", EditorStyles.miniButtonRight)) lineSpacing = Mathf.Max(0.01f, size.z);
                }
            }
            lineChain = EditorGUILayout.Toggle(new GUIContent("連続モード", "配置後、最後に置いた位置を次の始点にします"), lineChain);
            lineSnapToGrid = EditorGUILayout.Toggle("始点・終点をグリッドにスナップ", lineSnapToGrid);
            if (lineSnapToGrid) DrawGridFields();

            using (new EditorGUI.DisabledScope(!lineHasStart))
            {
                if (GUILayout.Button(lineHasStart ? "始点をリセット (Esc)" : "始点：未設定"))
                    ResetTransientState();
            }
        }

        void DrawGridGUI()
        {
            EditorGUILayout.LabelField("グリッド設定", EditorStyles.boldLabel);
            DrawGridFields();
        }

        void DrawGridFields()
        {
            gridSize = Mathf.Max(0.001f, EditorGUILayout.FloatField("グリッド間隔 (m)", gridSize));
            gridOrigin = EditorGUILayout.Vector3Field("グリッド原点", gridOrigin);
            gridSnapY = EditorGUILayout.Toggle("Y もスナップ", gridSnapY);
        }

        void DrawConnectGUI()
        {
            EditorGUILayout.LabelField("連結設定", EditorStyles.boldLabel);
            anchor = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("基準パーツ", "このパーツの端に次のパーツをくっつけます。配置するたびに自動で更新されます"),
                anchor, typeof(GameObject), true);
            if (anchor && EditorUtility.IsPersistent(anchor)) anchor = null; // アセットは不可

            using (new EditorGUILayout.HorizontalScope())
            {
                var sel = Selection.activeGameObject;
                using (new EditorGUI.DisabledScope(!sel || EditorUtility.IsPersistent(sel)))
                {
                    if (GUILayout.Button("選択中を基準にする")) anchor = sel;
                }
                if (GUILayout.Button("基準をクリア", GUILayout.Width(100))) anchor = null;
            }
            connectGap = EditorGUILayout.FloatField(new GUIContent("隙間 (m)", "マイナスで重ねる"), connectGap);
            connectAllowVertical = EditorGUILayout.Toggle("上下方向にも連結", connectAllowVertical);
        }

        void DrawCopyUpGUI()
        {
            EditorGUILayout.LabelField("上にコピー", EditorStyles.boldLabel);
            copyHeight = EditorGUILayout.FloatField("高さ (m)", copyHeight);

            int count = Selection.transforms.Length;
            EditorGUILayout.LabelField("選択中のオブジェクト", $"{count} 個（親子で選んだ場合は親のみ）");

            using (new EditorGUI.DisabledScope(count == 0))
            {
                if (GUILayout.Button($"選択したオブジェクトを {copyHeight:0.##}m 上にコピー", GUILayout.Height(32)))
                    CopySelectionUp();
            }
        }

        string GetHelpText()
        {
            switch (mode)
            {
                case Mode.Place: return "シーンビューを左クリックした位置にプレハブを配置します。\nAlt+ドラッグでの視点操作はそのまま使えます。";
                case Mode.Line: return "1回目のクリックで始点、2回目で終点。その間に一定間隔で並べます。\nEsc で始点をキャンセル。";
                case Mode.Grid: return "クリック位置をグリッドにスナップして配置します。\nカーソル付近にグリッド線を表示します。";
                case Mode.Scatter: return "シーンで地面にしたいオブジェクト（メッシュ / Terrain）を選択し、「散布」を押すと、その上面にプレハブをランダムに配置します。\nシーンビューの点がプレビュー（シードが同じなら結果も同じ）です。\n結果は 1 つのグループにまとまり、Ctrl+Z 1 回で消せます。";
                case Mode.ObjectSnap: return "カーソルが指しているオブジェクトのピボット位置を基準に、「ステップ」の倍数（+3, -3, +6 …）の位置にだけスナップして配置します。\n例: ステップ X=3 なら、基準の壁の左右 3m ごとの位置に吸着します。\nステップ 0 の軸は動かず、基準と同じ位置に固定されます。\n配置したパーツにカーソルを合わせれば、そこから続けて置けます。";
                case Mode.Connect: return "基準パーツの端（カーソルに近い面）に次のパーツをくっつけて配置します。\n基準が無いときは最初のクリックで普通に配置し、それが基準になります。\n配置するたびに基準は最新のパーツに切り替わります。";
                case Mode.Utility: return "【名前の (1) を削除】選択オブジェクトの名前末尾の \" (1)\" \"(2)\" などを消します。\n【同じプレハブを一括選択】選択オブジェクトと同じプレハブのインスタンスを、開いているシーンからまとめて選択します。";
                case Mode.CopyUp: return "ヒエラルキー／シーンで選んだオブジェクトを、回転・スケールそのままで指定の高さ上に複製します。\nプレハブのリンクも保持されます。";
            }
            return "";
        }

        // =====================================================================
        // シーンビュー
        // =====================================================================

        void OnSceneGUI(SceneView sv)
        {
            if (mode == Mode.Scatter)
            {
                if (Event.current.type == EventType.Repaint) DrawScatterPreview();
                return;
            }
            if (!sceneInputEnabled || mode == Mode.CopyUp || mode == Mode.Utility) return;

            Event e = Event.current;
            if (e.type == EventType.Repaint) DrawHud();
            if (!prefab) return;

            int id = GUIUtility.GetControlID(ControlHash, FocusType.Passive);
            switch (e.GetTypeForControl(id))
            {
                case EventType.Layout:
                    HandleUtility.AddDefaultControl(id);
                    break;

                case EventType.MouseMove:
                    UpdateHover(e.mousePosition);
                    sv.Repaint();
                    break;

                case EventType.MouseDown:
                    if (e.button == 0 && !e.alt && HandleUtility.nearestControl == id)
                    {
                        UpdateHover(e.mousePosition);
                        OnClick();
                        GUIUtility.hotControl = id;
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id) e.Use();
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;

                case EventType.MouseLeaveWindow:
                    hasHover = false;
                    connectHasTarget = false;
                    sv.Repaint();
                    break;

                case EventType.KeyDown:
                    if (e.keyCode == KeyCode.Escape && mode == Mode.Line && lineHasStart)
                    {
                        ResetTransientState();
                        Repaint();
                        e.Use();
                    }
                    else if (!e.alt && !e.control && !e.command &&
                             (e.keyCode == KeyCode.LeftArrow || e.keyCode == KeyCode.RightArrow || e.keyCode == KeyCode.DownArrow))
                    {
                        // ←/→: 回転、↓: リセット（シーンビューのカメラ移動より優先）
                        float step = e.shift ? rotateStep / 6f : rotateStep;
                        if (e.keyCode == KeyCode.LeftArrow) placeYaw -= step;
                        else if (e.keyCode == KeyCode.RightArrow) placeYaw += step;
                        else placeYaw = 0f;
                        placeYaw = Mathf.Repeat(Mathf.Round(placeYaw * 1000f) / 1000f, 360f);
                        SavePrefs();
                        UpdateHover(e.mousePosition);
                        Repaint();
                        sv.Repaint();
                        e.Use();
                    }
                    else if (e.keyCode == KeyCode.UpArrow && !e.alt && !e.control && !e.command)
                    {
                        e.Use(); // ↑ でカメラが動かないように
                    }
                    break;

                case EventType.Repaint:
                    DrawPreview();
                    break;
            }
        }

        void DrawHud()
        {
            string state;
            if (!prefab) state = "プレハブが未設定です";
            else
            {
                switch (mode)
                {
                    case Mode.Line: state = lineHasStart ? "終点をクリック（Escでキャンセル）" : "始点をクリック"; break;
                    case Mode.ObjectSnap: state = snapBase ? $"基準: {snapBase.name}" : "オブジェクトにカーソルを合わせてください"; break;
                    case Mode.Connect: state = anchor ? $"基準: {anchor.name}" : "最初のパーツをクリックで配置"; break;
                    default: state = $"左クリックで配置: {prefab.name}"; break;
                }
                state += $"\n回転 Y: {placeYaw:0.#}°（←/→ で回転・↓ でリセット）";
            }

            var content = new GUIContent($"Placement Tool  {ModeLabels[(int)mode]}\n{state}");
            var style = EditorStyles.helpBox;
            Vector2 size = style.CalcSize(content);
            Handles.BeginGUI();
            GUI.Label(new Rect(10, 10, size.x + 8, size.y + 4), content, style);
            Handles.EndGUI();
        }

        void UpdateHover(Vector2 mousePosition)
        {
            hasHover = RaycastScene(mousePosition, out hoverPoint);
            UpdateConnectTarget();
            UpdateObjectSnapTarget(mousePosition);
        }

        bool RaycastScene(Vector2 mousePosition, out Vector3 point)
        {
            // コライダー不要でメッシュに当たる Unity 標準の配置用レイキャスト
            if (HandleUtility.PlaceObject(mousePosition, out point, out _))
                return true;

            float planeY = groundHeight;
            if (mode == Mode.Connect && anchor) planeY = GetWorldBounds(anchor).center.y;

            Ray ray = HandleUtility.GUIPointToWorldRay(mousePosition);
            var plane = new Plane(Vector3.up, new Vector3(0f, planeY, 0f));
            if (plane.Raycast(ray, out float t))
            {
                point = ray.GetPoint(t);
                return true;
            }
            point = default;
            return false;
        }

        void OnClick()
        {
            if (!hasHover || !prefab) return;

            switch (mode)
            {
                case Mode.Place:
                    PlaceMany(new[] { PivotForSurfacePoint(hoverPoint) }, "Place Prefab");
                    break;

                case Mode.Grid:
                    PlaceMany(new[] { SnapToGrid(PivotForSurfacePoint(hoverPoint)) }, "Place Prefab (Grid)");
                    break;

                case Mode.Line:
                {
                    Vector3 p = LinePoint(hoverPoint);
                    if (!lineHasStart)
                    {
                        lineStart = p;
                        lineHasStart = true;
                        lineSkipFirst = false;
                    }
                    else
                    {
                        var points = ComputeLinePositions(lineStart, p, lineSkipFirst, out bool clamped);
                        if (clamped) Debug.LogWarning($"[Placement Tool] 個数が上限 {MaxLineCount} を超えたため打ち切りました。間隔を広げてください。");
                        PlaceMany(points, "Place Prefab (Line)");
                        if (lineChain)
                        {
                            // 最後に置いた位置を次の始点に（同じ位置に二重配置しないよう先頭はスキップ）
                            if (points.Count > 0) lineStart = points[points.Count - 1];
                            lineSkipFirst = true;
                        }
                        else
                        {
                            lineHasStart = false;
                        }
                    }
                    Repaint();
                    break;
                }

                case Mode.Connect:
                    if (!anchor)
                    {
                        var created = PlaceMany(new[] { hoverPoint }, "Place Prefab (Connect)");
                        if (created.Count > 0) anchor = created[0];
                    }
                    else if (connectHasTarget)
                    {
                        Vector3 pivot = connectTargetCenter - GetPlacementLocalBounds().center;
                        var created = PlaceMany(new[] { pivot }, "Place Prefab (Connect)", connectTargetCenter);
                        if (created.Count > 0) anchor = created[0];
                    }
                    UpdateConnectTarget();
                    Repaint();
                    break;

                case Mode.ObjectSnap:
                    if (snapHasTarget)
                        PlaceMany(new[] { snapTarget }, "Place Prefab (Object Snap)");
                    break;
            }
        }

        void UpdateConnectTarget()
        {
            connectHasTarget = false;
            if (mode != Mode.Connect || !anchor || !hasHover || !prefab) return;

            Bounds a = GetWorldBounds(anchor);
            Bounds lb = GetPlacementLocalBounds();
            Vector3 d = hoverPoint - a.center;
            Vector3 ext = a.extents;

            // カーソルがどの面の方向にあるか（バウンズの大きさで正規化して比較）
            float nx = Mathf.Abs(d.x) / Mathf.Max(ext.x, 0.001f);
            float ny = Mathf.Abs(d.y) / Mathf.Max(ext.y, 0.001f);
            float nz = Mathf.Abs(d.z) / Mathf.Max(ext.z, 0.001f);
            int axis = nx >= nz ? 0 : 2;
            if (connectAllowVertical && ny > Mathf.Max(nx, nz)) axis = 1;
            float sign = d[axis] >= 0f ? 1f : -1f;

            Vector3 c = a.center;
            c[axis] = a.center[axis] + sign * (ext[axis] + lb.extents[axis] + connectGap);
            if (axis != 1) c.y = a.min.y + lb.extents.y; // 横に連結するときは底面を揃える

            connectAnchorBounds = a;
            connectAxis = axis;
            connectSign = sign;
            connectTargetCenter = c;
            connectHasTarget = true;
        }

        // ---------------------------------------------------------------------
        // プレビュー描画
        // ---------------------------------------------------------------------

        void DrawPreview()
        {
            if (!hasHover) return;
            Bounds lb = GetPrefabLocalBounds();

            switch (mode)
            {
                case Mode.Place:
                    DrawPivotBox(PivotForSurfacePoint(hoverPoint), lb, PreviewColor);
                    DrawMarker(hoverPoint, PreviewColor);
                    break;

                case Mode.Grid:
                {
                    Vector3 snapped = SnapToGrid(PivotForSurfacePoint(hoverPoint));
                    DrawGridLines(snapped);
                    DrawPivotBox(snapped, lb, PreviewColor);
                    break;
                }

                case Mode.Line:
                {
                    Vector3 end = LinePoint(hoverPoint);
                    if (lineSnapToGrid) DrawGridLines(end);
                    if (!lineHasStart)
                    {
                        DrawPivotBox(end, lb, PreviewColor);
                        break;
                    }
                    Handles.color = Color.yellow;
                    Handles.DrawDottedLine(lineStart, end, 4f);
                    DrawMarker(lineStart, Color.yellow);
                    var points = ComputeLinePositions(lineStart, end, lineSkipFirst, out _);
                    for (int i = 0; i < points.Count && i < MaxPreviewCount; i++)
                        DrawPivotBox(points[i], lb, PreviewColor);
                    Handles.Label(end + Vector3.up * 0.3f,
                        $"{points.Count} 個 / {Vector3.Distance(lineStart, end):0.##} m", EditorStyles.whiteBoldLabel);
                    break;
                }

                case Mode.Connect:
                    if (!anchor)
                    {
                        DrawPivotBox(hoverPoint, lb, PreviewColor);
                        break;
                    }
                    Bounds a = connectHasTarget ? connectAnchorBounds : GetWorldBounds(anchor);
                    Handles.color = AnchorColor;
                    Handles.DrawWireCube(a.center, a.size);
                    if (connectHasTarget)
                    {
                        DrawFace(a, connectAxis, connectSign, AnchorColor);
                        Handles.color = TargetColor;
                        DrawPivotBox(connectTargetCenter - GetPlacementLocalBounds().center, lb, TargetColor);
                    }
                    break;

                case Mode.ObjectSnap:
                    DrawObjectSnapPreview(lb);
                    break;
            }
        }

        // 配置後の向き（追加のY回転込み）で箱を描く。矢印は Z+（前方向）
        void DrawPivotBox(Vector3 pivot, Bounds localBounds, Color color)
        {
            Quaternion q = PlacementRotation;
            Handles.color = color;
            using (new Handles.DrawingScope(color, Matrix4x4.TRS(pivot, q, Vector3.one)))
            {
                Handles.DrawWireCube(localBounds.center, localBounds.size);
            }
            DrawMarker(pivot, color);

            if (Mathf.Abs(placeYaw) > 0.01f || mode != Mode.Line)
            {
                Vector3 fwd = q * (prefab ? prefab.transform.forward : Vector3.forward);
                fwd.y = 0f;
                if (fwd.sqrMagnitude > 1e-6f)
                {
                    float len = HandleUtility.GetHandleSize(pivot) * 0.4f;
                    Handles.color = color;
                    Handles.DrawLine(pivot, pivot + fwd.normalized * len);
                    Handles.ConeHandleCap(0, pivot + fwd.normalized * len, Quaternion.LookRotation(fwd), len * 0.25f, EventType.Repaint);
                }
            }
        }

        Quaternion PlacementRotation => Quaternion.Euler(0f, placeYaw, 0f);

        // 追加回転を反映したピボット基準のAABB（連結モード用）
        Bounds GetPlacementLocalBounds()
        {
            Bounds lb = GetPrefabLocalBounds();
            if (Mathf.Abs(placeYaw) < 0.001f) return lb;
            bool has = false;
            Bounds r = default;
            Encapsulate(ref has, ref r, lb, Matrix4x4.Rotate(PlacementRotation));
            return r;
        }

        static void DrawMarker(Vector3 p, Color color)
        {
            Handles.color = color;
            float s = HandleUtility.GetHandleSize(p) * 0.08f;
            Handles.DrawLine(p - Vector3.right * s, p + Vector3.right * s);
            Handles.DrawLine(p - Vector3.forward * s, p + Vector3.forward * s);
            Handles.DrawLine(p - Vector3.up * s, p + Vector3.up * s);
        }

        static void DrawFace(Bounds b, int axis, float sign, Color color)
        {
            Vector3 c = b.center;
            c[axis] += sign * b.extents[axis];
            int u = (axis + 1) % 3, v = (axis + 2) % 3;
            Vector3 du = Vector3.zero, dv = Vector3.zero;
            du[u] = b.extents[u];
            dv[v] = b.extents[v];
            var verts = new[] { c - du - dv, c + du - dv, c + du + dv, c - du + dv };
            Handles.DrawSolidRectangleWithOutline(verts, new Color(color.r, color.g, color.b, 0.25f), color);
        }

        void DrawGridLines(Vector3 center)
        {
            const int half = 10;
            float s = Mathf.Max(0.001f, gridSize);
            float y = center.y;
            Handles.color = GridColor;
            for (int i = -half; i <= half; i++)
            {
                float x = center.x + i * s;
                float z = center.z + i * s;
                Handles.DrawLine(new Vector3(x, y, center.z - half * s), new Vector3(x, y, center.z + half * s));
                Handles.DrawLine(new Vector3(center.x - half * s, y, z), new Vector3(center.x + half * s, y, z));
            }
        }

        // ---------------------------------------------------------------------
        // 位置計算
        // ---------------------------------------------------------------------

        Vector3 PivotForSurfacePoint(Vector3 surfacePoint)
        {
            if (alignBottom) surfacePoint.y -= GetPrefabLocalBounds().min.y;
            return surfacePoint;
        }

        Vector3 SnapToGrid(Vector3 p)
        {
            float s = Mathf.Max(0.001f, gridSize);
            Vector3 r = p - gridOrigin;
            r.x = Mathf.Round(r.x / s) * s;
            r.z = Mathf.Round(r.z / s) * s;
            if (gridSnapY) r.y = Mathf.Round(r.y / s) * s;
            return r + gridOrigin;
        }

        Vector3 LinePoint(Vector3 surfacePoint)
        {
            Vector3 p = PivotForSurfacePoint(surfacePoint);
            return lineSnapToGrid ? SnapToGrid(p) : p;
        }

        List<Vector3> ComputeLinePositions(Vector3 start, Vector3 end, bool skipFirst, out bool clamped)
        {
            var list = new List<Vector3>();
            float spacing = Mathf.Max(0.01f, lineSpacing);
            float dist = Vector3.Distance(start, end);
            int count = Mathf.FloorToInt(dist / spacing + 1e-4f) + 1;
            clamped = count > MaxLineCount;
            count = Mathf.Min(count, MaxLineCount);
            Vector3 dir = dist > 1e-6f ? (end - start) / dist : Vector3.zero;
            for (int i = skipFirst ? 1 : 0; i < count; i++)
                list.Add(start + dir * (spacing * i));
            return list;
        }

        // =====================================================================
        // 配置（Undo 対応）
        // =====================================================================

        List<GameObject> PlaceMany(IList<Vector3> pivots, string undoName, Vector3? desiredBoundsCenter = null)
        {
            var created = new List<GameObject>();
            if (!prefab || pivots.Count == 0) return created;

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);

            foreach (var p in pivots)
            {
                var go = Spawn(p, undoName, desiredBoundsCenter);
                if (go) created.Add(go);
            }

            if (selectPlaced && created.Count > 0)
                Selection.objects = created.Cast<Object>().ToArray();

            Undo.CollapseUndoOperations(group);
            return created;
        }

        GameObject Spawn(Vector3 pivot, string undoName, Vector3? desiredBoundsCenter)
        {
            Transform par = parent;
            Scene scene;
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null)
            {
                scene = stage.scene;
                if (!par || par.gameObject.scene != stage.scene) par = stage.prefabContentsRoot.transform;
            }
            else
            {
                scene = par ? par.gameObject.scene : SceneManager.GetActiveScene();
            }

            // プレハブのリンクを保ったままインスタンス化（回転・スケールはプレハブの値のまま）
            var go = PrefabUtility.InstantiatePrefab(prefab, scene) as GameObject;
            if (!go) return null;

            if (par) go.transform.SetParent(par, true); // ワールドの回転・スケールを維持
            go.transform.position = pivot;
            if (Mathf.Abs(placeYaw) > 0.001f)
                go.transform.rotation = PlacementRotation * go.transform.rotation; // ←/→ キーで指定したY回転を追加

            // 親のスケールなどでズレた分をバウンズで補正（位置のみ）
            if (desiredBoundsCenter.HasValue && TryGetWorldBounds(go, out var b))
                go.transform.position += desiredBoundsCenter.Value - b.center;

            Undo.RegisterCreatedObjectUndo(go, undoName);
            return go;
        }

        // =====================================================================
        // ⑤ 上にコピー
        // =====================================================================

        void CopySelectionUp()
        {
            // Selection.transforms は「シーン内・最上位のみ」（子を重複コピーしない）
            var sources = Selection.transforms.Select(t => t.gameObject).ToArray();
            if (sources.Length == 0)
            {
                ShowNotification(new GUIContent("シーン内のオブジェクトを選択してください"));
                return;
            }

            string undoName = $"Copy Up {copyHeight:0.##}m";
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);

            GameObject[] copies = TryDuplicateWithUnity(sources) ?? DuplicateManually(sources, undoName);

            Vector3 offset = Vector3.up * copyHeight;
            foreach (var c in copies)
            {
                Undo.RecordObject(c.transform, undoName);
                c.transform.position += offset; // 位置だけ動かす（回転・スケールはそのまま）
            }

            Selection.objects = copies.Cast<Object>().ToArray();
            Undo.CollapseUndoOperations(group);
        }

        // Edit > Duplicate と同じ処理。プレハブのオーバーライドやネストも完全に保持される。
        static GameObject[] TryDuplicateWithUnity(GameObject[] sources)
        {
            if (UnityDuplicateMethod == null) return null;

            var originals = new HashSet<GameObject>(sources);
            Selection.objects = sources.Cast<Object>().ToArray();
            try
            {
                UnityDuplicateMethod.Invoke(null, null);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Placement Tool] 標準の複製に失敗したため代替処理を使います: {ex.Message}");
                return null;
            }

            var copies = Selection.transforms.Select(t => t.gameObject).Where(g => !originals.Contains(g)).ToArray();
            return copies.Length > 0 ? copies : null;
        }

        // 予備の複製処理（プレハブ: 同じプレハブ＋プロパティのオーバーライドを再現）
        static GameObject[] DuplicateManually(GameObject[] sources, string undoName)
        {
            var result = new List<GameObject>();
            foreach (var src in sources)
            {
                Transform st = src.transform;
                GameObject copy = null;

                if (PrefabUtility.IsOutermostPrefabInstanceRoot(src))
                {
                    var asset = PrefabUtility.GetCorrespondingObjectFromSource(src);
                    if (asset)
                    {
                        copy = PrefabUtility.InstantiatePrefab(asset, src.scene) as GameObject;
                        var mods = PrefabUtility.GetPropertyModifications(src);
                        if (copy && mods != null) PrefabUtility.SetPropertyModifications(copy, mods);
                        if (copy) copy.transform.SetParent(st.parent, false);
                    }
                }

                if (!copy)
                {
                    copy = Instantiate(src, st.parent);
                    if (!st.parent && copy.scene != src.scene) SceneManager.MoveGameObjectToScene(copy, src.scene);
                }

                copy.name = src.name;
                copy.transform.localPosition = st.localPosition;
                copy.transform.localRotation = st.localRotation;
                copy.transform.localScale = st.localScale;
                copy.transform.SetSiblingIndex(st.GetSiblingIndex() + 1);

                Undo.RegisterCreatedObjectUndo(copy, undoName);
                result.Add(copy);
            }
            return result.ToArray();
        }

        // =====================================================================
        // ⑧ ランダム散布
        // =====================================================================

        [Serializable]
        class ScatterItem
        {
            public GameObject prefab;
            public float weight = 1f;
        }

        struct ScatterPoint
        {
            public Vector3 position;
            public Vector3 normal;
            public int item;
            public float yaw;
            public float scale;
        }

        struct ScatterTri
        {
            public Vector3 a, b, c, n;
            public Terrain terrain;
        }

        const int MaxScatterCount = 5000;
        const int MaxScatterPreview = 3000;

        [SerializeField] List<ScatterItem> scatterItems = new List<ScatterItem>();
        [SerializeField] bool scatterUseDensity;
        [SerializeField] int scatterCount = 50;
        [SerializeField] float scatterDensity = 0.5f;
        [SerializeField] float scatterMinDistance = 0.3f;
        [SerializeField] float scatterMaxSlope = 30f;
        [SerializeField] bool scatterIncludeChildren = true;
        [SerializeField] bool scatterRandomYaw = true;
        [SerializeField] bool scatterAlignNormal;
        [SerializeField] bool scatterRandomScale;
        [SerializeField] Vector2 scatterScaleRange = new Vector2(0.8f, 1.2f);
        [SerializeField] bool scatterAlignBottom = true;
        [SerializeField] float scatterHeightOffset;
        [SerializeField] int scatterSeed = 12345;
        [SerializeField] bool scatterShowPreview = true;
        [SerializeField] GameObject scatterLastGroup;

        List<ScatterPoint> scatterPreview = new List<ScatterPoint>();
        bool scatterPreviewDirty = true;
        string scatterStatus = "";
        readonly Dictionary<GameObject, Bounds> scatterBoundsCache = new Dictionary<GameObject, Bounds>();

        void DrawScatterGUI()
        {
            var grounds = GetScatterGrounds();
            EditorGUILayout.LabelField("地面", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("選択中", grounds.Count > 0
                ? $"{grounds.Count} 個（{string.Join(", ", grounds.Take(3).Select(g => g.name))}{(grounds.Count > 3 ? " …" : "")}）"
                : "シーンで地面オブジェクトを選択してください");
            scatterIncludeChildren = EditorGUILayout.Toggle("子オブジェクトも地面にする", scatterIncludeChildren);
            scatterMaxSlope = EditorGUILayout.Slider(new GUIContent("傾斜の上限 (°)", "これより急な面には置きません（0 = 真上向きの面だけ）"), scatterMaxSlope, 0f, 89f);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("散布するプレハブ（比率が大きいほど多く出る）", EditorStyles.boldLabel);
            DrawScatterItems();

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("量・間隔", EditorStyles.boldLabel);
            scatterUseDensity = EditorGUILayout.Popup("指定方法", scatterUseDensity ? 1 : 0, new[] { "個数", "密度（1m² あたり）" }) == 1;
            if (scatterUseDensity) scatterDensity = Mathf.Max(0f, EditorGUILayout.FloatField("密度 (個/m²)", scatterDensity));
            else scatterCount = Mathf.Clamp(EditorGUILayout.IntField("個数", scatterCount), 0, MaxScatterCount);
            scatterMinDistance = Mathf.Max(0f, EditorGUILayout.FloatField(new GUIContent("最小間隔 (m)", "オブジェクト同士がこれより近づかないようにします"), scatterMinDistance));

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("ばらつき", EditorStyles.boldLabel);
            scatterRandomYaw = EditorGUILayout.Toggle("Y 回転をランダム", scatterRandomYaw);
            scatterAlignNormal = EditorGUILayout.Toggle(new GUIContent("面の傾きに合わせる", "坂の上では坂に沿って傾けます"), scatterAlignNormal);
            scatterRandomScale = EditorGUILayout.Toggle("スケールをランダム", scatterRandomScale);
            if (scatterRandomScale)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel("  倍率 最小 / 最大");
                    scatterScaleRange.x = Mathf.Max(0.01f, EditorGUILayout.FloatField(scatterScaleRange.x));
                    scatterScaleRange.y = Mathf.Max(scatterScaleRange.x, EditorGUILayout.FloatField(scatterScaleRange.y));
                }
            }
            scatterAlignBottom = EditorGUILayout.Toggle(new GUIContent("底面を接地", "ピボットではなくバウンズの底面を地面に合わせます"), scatterAlignBottom);
            scatterHeightOffset = EditorGUILayout.FloatField(new GUIContent("高さオフセット (m)", "マイナスで少し埋める"), scatterHeightOffset);

            using (new EditorGUILayout.HorizontalScope())
            {
                scatterSeed = EditorGUILayout.IntField("シード", scatterSeed);
                if (GUILayout.Button("ランダム", GUILayout.Width(70))) scatterSeed = NewSeed();
            }
            scatterShowPreview = EditorGUILayout.Toggle("シーンにプレビュー表示", scatterShowPreview);

            EditorGUILayout.Space(6);
            if (scatterPreviewDirty) RebuildScatterPreview();
            if (!string.IsNullOrEmpty(scatterStatus))
                EditorGUILayout.HelpBox(scatterStatus, scatterPreview.Count > 0 ? MessageType.None : MessageType.Warning);

            using (new EditorGUI.DisabledScope(scatterPreview.Count == 0))
            {
                if (GUILayout.Button("散布", GUILayout.Height(32)))
                    ExecuteScatter(false);
            }
            using (new EditorGUI.DisabledScope(!scatterLastGroup))
            {
                if (GUILayout.Button("前回の結果を消して、別のシードで再散布"))
                {
                    scatterSeed = NewSeed();
                    scatterPreviewDirty = true;
                    ExecuteScatter(true);
                }
            }
        }

        static int NewSeed() => UnityEngine.Random.Range(1, int.MaxValue);

        void DrawScatterItems()
        {
            scatterItems.RemoveAll(i => i == null);
            int remove = -1;
            for (int i = 0; i < scatterItems.Count; i++)
            {
                var it = scatterItems[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    var picked = (GameObject)EditorGUILayout.ObjectField(it.prefab, typeof(GameObject), false);
                    if (picked != it.prefab && (picked == null || IsPrefabAsset(picked))) it.prefab = picked;
                    EditorGUILayout.LabelField("比率", GUILayout.Width(30));
                    it.weight = Mathf.Max(0f, EditorGUILayout.FloatField(it.weight, GUILayout.Width(45)));
                    if (GUILayout.Button("×", GUILayout.Width(22))) remove = i;
                }
            }
            if (remove >= 0) { scatterItems.RemoveAt(remove); GUI.changed = true; }

            Rect drop = GUILayoutUtility.GetRect(0, 34, GUILayout.ExpandWidth(true));
            GUI.Box(drop, "ここにプレハブをドラッグ＆ドロップ（複数可）");
            Event e = Event.current;
            if ((e.type == EventType.DragUpdated || e.type == EventType.DragPerform) && drop.Contains(e.mousePosition))
            {
                var items = DragAndDrop.objectReferences.OfType<GameObject>().Where(IsPrefabAsset).ToList();
                if (items.Count > 0)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    if (e.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        AddScatterItems(items);
                    }
                    e.Use();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Projectで選択中を追加"))
                    AddScatterItems(Selection.objects.OfType<GameObject>().Where(IsPrefabAsset));
                if (GUILayout.Button("パレットから追加"))
                    AddScatterItems(palette);
                if (GUILayout.Button("クリア", GUILayout.Width(60)) && scatterItems.Count > 0)
                {
                    scatterItems.Clear();
                    GUI.changed = true;
                }
            }
        }

        void AddScatterItems(IEnumerable<GameObject> prefabs)
        {
            foreach (var g in prefabs)
                if (g && scatterItems.All(i => i.prefab != g))
                    scatterItems.Add(new ScatterItem { prefab = g, weight = 1f });
            GUI.changed = true;
            scatterPreviewDirty = true;
            SavePrefs();
            SceneView.RepaintAll();
        }

        static List<GameObject> GetScatterGrounds()
        {
            return Selection.gameObjects.Where(g => g && !EditorUtility.IsPersistent(g)).ToList();
        }

        void RebuildScatterPreview()
        {
            scatterPreview = GenerateScatter(out scatterStatus);
            scatterPreviewDirty = false;
        }

        // 地面の上向きの面（傾斜の上限以内）を三角形リストとして集める。Terrain は 1 要素として扱う
        bool BuildScatterSurface(List<GameObject> grounds, List<ScatterTri> tris, List<float> cumulative, out float totalArea, out int unreadable)
        {
            totalArea = 0f;
            unreadable = 0;
            float cosMax = Mathf.Cos(scatterMaxSlope * Mathf.Deg2Rad) - 1e-4f;

            var filters = new HashSet<MeshFilter>();
            var terrains = new HashSet<Terrain>();
            foreach (var g in grounds)
            {
                if (scatterIncludeChildren)
                {
                    foreach (var mf in g.GetComponentsInChildren<MeshFilter>()) filters.Add(mf);
                    foreach (var t in g.GetComponentsInChildren<Terrain>()) terrains.Add(t);
                }
                else
                {
                    if (g.TryGetComponent(out MeshFilter mf)) filters.Add(mf);
                    if (g.TryGetComponent(out Terrain t)) terrains.Add(t);
                }
            }

            foreach (var mf in filters)
            {
                var mesh = mf.sharedMesh;
                if (!mesh) continue;
                if (mf.TryGetComponent(out MeshRenderer mr) && !mr.enabled) continue;

                Vector3[] v;
                int[] idx;
                try
                {
                    v = mesh.vertices;
                    idx = mesh.triangles;
                }
                catch (Exception)
                {
                    unreadable++;
                    continue;
                }
                if (v == null || v.Length == 0 || idx == null || idx.Length == 0)
                {
                    unreadable++;
                    continue;
                }

                Matrix4x4 m = mf.transform.localToWorldMatrix;
                bool flip = m.determinant < 0f;
                for (int i = 0; i < v.Length; i++) v[i] = m.MultiplyPoint3x4(v[i]);

                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    Vector3 a = v[idx[i]], b = v[idx[i + 1]], c = v[idx[i + 2]];
                    Vector3 cross = Vector3.Cross(b - a, c - a);
                    float area = cross.magnitude * 0.5f;
                    if (area < 1e-6f) continue;
                    Vector3 n = cross.normalized;
                    if (flip) n = -n;
                    if (n.y < cosMax) continue; // 下向き・急斜面は除外

                    totalArea += area;
                    tris.Add(new ScatterTri { a = a, b = b, c = c, n = n });
                    cumulative.Add(totalArea);
                }
            }

            foreach (var t in terrains)
            {
                if (!t.terrainData || !t.enabled) continue;
                var size = t.terrainData.size;
                totalArea += size.x * size.z;
                tris.Add(new ScatterTri { terrain = t });
                cumulative.Add(totalArea);
            }
            return totalArea > 0f;
        }

        List<ScatterPoint> GenerateScatter(out string status)
        {
            var result = new List<ScatterPoint>();
            var items = scatterItems.Where(i => i != null && i.prefab && i.weight > 0f).ToList();
            var grounds = GetScatterGrounds();

            if (items.Count == 0) { status = "散布するプレハブを追加してください。"; return result; }
            if (grounds.Count == 0) { status = "シーンで地面にするオブジェクトを選択してください。"; return result; }

            var tris = new List<ScatterTri>();
            var cumulative = new List<float>();
            if (!BuildScatterSurface(grounds, tris, cumulative, out float totalArea, out int unreadable))
            {
                status = unreadable > 0
                    ? "メッシュを読み取れませんでした（モデルの Import Settings で Read/Write を ON にしてください）。"
                    : "置ける上向きの面がありません（「傾斜の上限」や選択を確認してください）。";
                return result;
            }

            int target = scatterUseDensity ? Mathf.RoundToInt(totalArea * scatterDensity) : scatterCount;
            target = Mathf.Clamp(target, 0, MaxScatterCount);

            var rng = new System.Random(scatterSeed);
            float Next() => (float)rng.NextDouble();

            float weightSum = items.Sum(i => i.weight);
            float cosMax = Mathf.Cos(scatterMaxSlope * Mathf.Deg2Rad) - 1e-4f;
            float minD = scatterMinDistance;
            float minD2 = minD * minD;
            var hash = new Dictionary<Vector3Int, List<Vector3>>();
            Vector3Int Cell(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / minD), Mathf.FloorToInt(p.y / minD), Mathf.FloorToInt(p.z / minD));

            int attempts = target * 30 + 100;
            while (result.Count < target && attempts-- > 0)
            {
                float r = Next() * totalArea;
                int ti = cumulative.BinarySearch(r);
                if (ti < 0) ti = ~ti;
                ti = Mathf.Min(ti, tris.Count - 1);
                var tri = tris[ti];

                Vector3 pos, normal;
                if (tri.terrain)
                {
                    var td = tri.terrain.terrainData;
                    Vector3 tp = tri.terrain.transform.position;
                    float u = Next(), w = Next();
                    pos = tp + new Vector3(u * td.size.x, 0f, w * td.size.z);
                    pos.y = tp.y + tri.terrain.SampleHeight(pos);
                    normal = td.GetInterpolatedNormal(u, w);
                    if (normal.y < cosMax) continue;
                }
                else
                {
                    float r1 = Next(), r2 = Next();
                    if (r1 + r2 > 1f) { r1 = 1f - r1; r2 = 1f - r2; }
                    pos = tri.a + r1 * (tri.b - tri.a) + r2 * (tri.c - tri.a);
                    normal = tri.n;
                }

                if (minD > 1e-4f)
                {
                    var c = Cell(pos);
                    bool tooClose = false;
                    for (int dx = -1; dx <= 1 && !tooClose; dx++)
                    for (int dy = -1; dy <= 1 && !tooClose; dy++)
                    for (int dz = -1; dz <= 1 && !tooClose; dz++)
                    {
                        if (!hash.TryGetValue(new Vector3Int(c.x + dx, c.y + dy, c.z + dz), out var list)) continue;
                        foreach (var q in list)
                            if ((q - pos).sqrMagnitude < minD2) { tooClose = true; break; }
                    }
                    if (tooClose) continue;
                    if (!hash.TryGetValue(c, out var cellList)) hash[c] = cellList = new List<Vector3>();
                    cellList.Add(pos);
                }

                float pick = Next() * weightSum;
                int item = 0;
                for (; item < items.Count - 1; item++)
                {
                    pick -= items[item].weight;
                    if (pick < 0f) break;
                }

                result.Add(new ScatterPoint
                {
                    position = pos,
                    normal = normal,
                    item = scatterItems.IndexOf(items[item]),
                    yaw = scatterRandomYaw ? Next() * 360f : 0f,
                    scale = scatterRandomScale ? Mathf.Lerp(scatterScaleRange.x, scatterScaleRange.y, Next()) : 1f,
                });
            }

            status = $"{result.Count} / {target} 個（置ける面積 {totalArea:0.#} m²）";
            if (result.Count < target) status += "\n最小間隔が大きいため、全部は置けませんでした。";
            if (unreadable > 0) status += $"\n読み取れないメッシュが {unreadable} 個ありました（Read/Write を ON にすると対象になります）。";
            return result;
        }

        Bounds GetScatterLocalBounds(GameObject pf)
        {
            if (!scatterBoundsCache.TryGetValue(pf, out var lb))
            {
                lb = TryGetWorldBounds(pf, out var b)
                    ? new Bounds(b.center - pf.transform.position, b.size)
                    : new Bounds(Vector3.zero, Vector3.one * 0.2f);
                scatterBoundsCache[pf] = lb;
            }
            return lb;
        }

        void GetScatterPose(ScatterPoint p, GameObject pf, out Vector3 position, out Quaternion extraRotation)
        {
            Quaternion basis = scatterAlignNormal ? Quaternion.FromToRotation(Vector3.up, p.normal) : Quaternion.identity;
            extraRotation = basis * Quaternion.Euler(0f, p.yaw, 0f);
            float lift = scatterHeightOffset;
            if (scatterAlignBottom) lift -= GetScatterLocalBounds(pf).min.y * p.scale;
            position = p.position + basis * Vector3.up * lift;
        }

        void DrawScatterPreview()
        {
            if (!scatterShowPreview) return;
            if (scatterPreviewDirty) RebuildScatterPreview();

            int n = Mathf.Min(scatterPreview.Count, MaxScatterPreview);
            for (int i = 0; i < n; i++)
            {
                var p = scatterPreview[i];
                if (p.item < 0 || p.item >= scatterItems.Count || !scatterItems[p.item].prefab) continue;
                var pf = scatterItems[p.item].prefab;
                var lb = GetScatterLocalBounds(pf);
                float radius = Mathf.Max(0.05f, Mathf.Max(lb.extents.x, lb.extents.z) * p.scale);
                Color col = Color.HSVToRGB((p.item * 0.618f) % 1f, 0.7f, 1f);
                Handles.color = col;
                Handles.DrawWireDisc(p.position, scatterAlignNormal ? p.normal : Vector3.up, radius);
                Handles.DrawLine(p.position, p.position + (scatterAlignNormal ? p.normal : Vector3.up) * radius);
            }

            Handles.BeginGUI();
            var content = new GUIContent($"Placement Tool  ⑧ ランダム散布\n{scatterStatus}");
            Vector2 size = EditorStyles.helpBox.CalcSize(content);
            GUI.Label(new Rect(10, 10, size.x + 8, size.y + 4), content, EditorStyles.helpBox);
            Handles.EndGUI();
        }

        void ExecuteScatter(bool replaceLast)
        {
            var grounds = GetScatterGrounds();
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Scatter Prefabs");

            if (replaceLast && scatterLastGroup && !grounds.Contains(scatterLastGroup))
                Undo.DestroyObjectImmediate(scatterLastGroup);

            var points = GenerateScatter(out scatterStatus);
            scatterPreview = points;
            scatterPreviewDirty = false;
            if (points.Count == 0)
            {
                ShowNotification(new GUIContent(scatterStatus));
                Undo.CollapseUndoOperations(group);
                return;
            }

            // まとめ用の空オブジェクト（地面と同じシーン / Prefab モード中はそのルート配下）
            var root = new GameObject($"Scatter_{grounds[0].name}");
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null) root.transform.SetParent(stage.prefabContentsRoot.transform, false);
            else if (root.scene != grounds[0].scene) SceneManager.MoveGameObjectToScene(root, grounds[0].scene);
            Undo.RegisterCreatedObjectUndo(root, "Scatter Prefabs");

            foreach (var p in points)
            {
                var pf = scatterItems[p.item].prefab;
                var go = PrefabUtility.InstantiatePrefab(pf, root.scene) as GameObject;
                if (!go) continue;
                go.transform.SetParent(root.transform, true);
                GetScatterPose(p, pf, out var pos, out var extra);
                go.transform.SetPositionAndRotation(pos, extra * go.transform.rotation);
                if (!Mathf.Approximately(p.scale, 1f)) go.transform.localScale *= p.scale;
                Undo.RegisterCreatedObjectUndo(go, "Scatter Prefabs");
            }

            scatterLastGroup = root;
            Undo.CollapseUndoOperations(group);
            EditorGUIUtility.PingObject(root);
            SceneView.RepaintAll();
        }

        void SaveScatterPrefs()
        {
            var ci = CultureInfo.InvariantCulture;
            EditorPrefs.SetString(Pref + "ScatterItems", string.Join(";",
                scatterItems.Where(i => i != null && i.prefab).Select(i => GuidOf(i.prefab) + "|" + i.weight.ToString(ci))));
            EditorPrefs.SetBool(Pref + "ScatterUseDensity", scatterUseDensity);
            EditorPrefs.SetInt(Pref + "ScatterCount", scatterCount);
            EditorPrefs.SetFloat(Pref + "ScatterDensity", scatterDensity);
            EditorPrefs.SetFloat(Pref + "ScatterMinDist", scatterMinDistance);
            EditorPrefs.SetFloat(Pref + "ScatterSlope", scatterMaxSlope);
            EditorPrefs.SetBool(Pref + "ScatterChildren", scatterIncludeChildren);
            EditorPrefs.SetBool(Pref + "ScatterYaw", scatterRandomYaw);
            EditorPrefs.SetBool(Pref + "ScatterNormal", scatterAlignNormal);
            EditorPrefs.SetBool(Pref + "ScatterRandScale", scatterRandomScale);
            EditorPrefs.SetFloat(Pref + "ScatterScaleMin", scatterScaleRange.x);
            EditorPrefs.SetFloat(Pref + "ScatterScaleMax", scatterScaleRange.y);
            EditorPrefs.SetBool(Pref + "ScatterBottom", scatterAlignBottom);
            EditorPrefs.SetFloat(Pref + "ScatterOffset", scatterHeightOffset);
            EditorPrefs.SetInt(Pref + "ScatterSeed", scatterSeed);
            EditorPrefs.SetBool(Pref + "ScatterPreview", scatterShowPreview);
        }

        void LoadScatterPrefs()
        {
            string s = EditorPrefs.GetString(Pref + "ScatterItems", "");
            if (!string.IsNullOrEmpty(s))
            {
                scatterItems = new List<ScatterItem>();
                foreach (var entry in s.Split(';'))
                {
                    var parts = entry.Split('|');
                    var pf = LoadByGuid(parts[0]);
                    if (!pf) continue;
                    float w = 1f;
                    if (parts.Length > 1) float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out w);
                    scatterItems.Add(new ScatterItem { prefab = pf, weight = w });
                }
            }
            scatterItems ??= new List<ScatterItem>();
            scatterUseDensity = EditorPrefs.GetBool(Pref + "ScatterUseDensity", scatterUseDensity);
            scatterCount = EditorPrefs.GetInt(Pref + "ScatterCount", scatterCount);
            scatterDensity = EditorPrefs.GetFloat(Pref + "ScatterDensity", scatterDensity);
            scatterMinDistance = EditorPrefs.GetFloat(Pref + "ScatterMinDist", scatterMinDistance);
            scatterMaxSlope = EditorPrefs.GetFloat(Pref + "ScatterSlope", scatterMaxSlope);
            scatterIncludeChildren = EditorPrefs.GetBool(Pref + "ScatterChildren", scatterIncludeChildren);
            scatterRandomYaw = EditorPrefs.GetBool(Pref + "ScatterYaw", scatterRandomYaw);
            scatterAlignNormal = EditorPrefs.GetBool(Pref + "ScatterNormal", scatterAlignNormal);
            scatterRandomScale = EditorPrefs.GetBool(Pref + "ScatterRandScale", scatterRandomScale);
            scatterScaleRange = new Vector2(
                EditorPrefs.GetFloat(Pref + "ScatterScaleMin", scatterScaleRange.x),
                EditorPrefs.GetFloat(Pref + "ScatterScaleMax", scatterScaleRange.y));
            scatterAlignBottom = EditorPrefs.GetBool(Pref + "ScatterBottom", scatterAlignBottom);
            scatterHeightOffset = EditorPrefs.GetFloat(Pref + "ScatterOffset", scatterHeightOffset);
            scatterSeed = EditorPrefs.GetInt(Pref + "ScatterSeed", scatterSeed);
            scatterShowPreview = EditorPrefs.GetBool(Pref + "ScatterPreview", scatterShowPreview);
            scatterPreviewDirty = true;
        }

        // =====================================================================
        // ⑦ オブジェクトスナップ
        // =====================================================================

        void DrawObjectSnapGUI()
        {
            EditorGUILayout.LabelField("オブジェクトスナップ設定", EditorStyles.boldLabel);
            objSnapStep = EditorGUILayout.Vector3Field(
                new GUIContent("ステップ (m)", "基準オブジェクトからこの距離の倍数の位置にスナップします。\n0 の軸は動かさず基準と同じ位置に固定します。\nマイナスも入力できます（倍数なので ±どちら向きにもスナップします）"),
                objSnapStep);
            if (objSnapY != SnapYMode.Step && Mathf.Abs(objSnapStep.y) > 1e-4f)
                EditorGUILayout.LabelField(" ", "※ Y は「高さ」が「ステップでスナップ」のときだけ使われます", EditorStyles.miniLabel);

            if (prefab)
            {
                var size = GetPrefabLocalBounds().size;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel(" ");
                    if (GUILayout.Button("ステップ = プレハブのサイズ", EditorStyles.miniButton))
                        objSnapStep = new Vector3(Round3(size.x), Round3(size.y), Round3(size.z));
                }
            }

            objSnapY = (SnapYMode)EditorGUILayout.Popup(
                new GUIContent("高さ (Y)", "基準と同じ高さ: 基準のピボットと同じY\nステップでスナップ: Y もステップの倍数\nカーソル位置: 当たった面の高さ"),
                (int)objSnapY, SnapYLabels);
            objSnapLocalAxes = EditorGUILayout.Toggle(
                new GUIContent("基準のローカル軸で並べる", "ON: 基準オブジェクトの向きに沿ってステップを取ります（置くプレハブの回転は変えません）"),
                objSnapLocalAxes);
            objSnapUsePrefabRoot = EditorGUILayout.Toggle(
                new GUIContent("プレハブのルートを基準にする", "ON: 子メッシュを指していても、そのプレハブのルートのピボットを基準にします"),
                objSnapUsePrefabRoot);
            objSnapSkipBase = EditorGUILayout.Toggle("基準と同じ位置には置かない", objSnapSkipBase);
            objSnapShowCandidates = EditorGUILayout.Toggle("スナップ候補を表示", objSnapShowCandidates);
        }

        static float Round3(float v) => Mathf.Round(v * 1000f) / 1000f;

        GameObject PickSnapBase(Vector2 mousePosition)
        {
            var picked = HandleUtility.PickGameObject(mousePosition, false);
            if (!picked) return null;
            if (objSnapUsePrefabRoot)
            {
                var root = PrefabUtility.GetNearestPrefabInstanceRoot(picked);
                if (root) return root;
            }
            return picked;
        }

        Quaternion SnapAxesRotation(Transform baseT) => objSnapLocalAxes ? baseT.rotation : Quaternion.identity;

        void UpdateObjectSnapTarget(Vector2 mousePosition)
        {
            snapHasTarget = false;
            if (mode != Mode.ObjectSnap || !prefab) return;

            snapBase = PickSnapBase(mousePosition);
            if (!snapBase || !hasHover) return;

            Transform bt = snapBase.transform;
            Vector3 basePos = bt.position;
            Quaternion rot = SnapAxesRotation(bt);

            // 基準ピボットからのオフセットを（必要なら基準の向きで）ステップの倍数に丸める
            Vector3 local = Quaternion.Inverse(rot) * (hoverPoint - basePos);
            local.x = SnapAxis(local.x, objSnapStep.x);
            local.z = SnapAxis(local.z, objSnapStep.z);
            switch (objSnapY)
            {
                case SnapYMode.SameAsBase: local.y = 0f; break;
                case SnapYMode.Step: local.y = SnapAxis(local.y, objSnapStep.y); break;
                case SnapYMode.Cursor: break;
            }

            if (objSnapSkipBase && local.sqrMagnitude < 1e-6f) return;

            snapTarget = basePos + rot * local;
            snapHasTarget = true;
        }

        // step = 0 の軸は固定（基準と同じ位置）。符号は無関係（±どちら向きの倍数にもスナップ）
        static float SnapAxis(float v, float step)
        {
            float s = Mathf.Abs(step);
            return s > 1e-4f ? Mathf.Round(v / s) * s : 0f;
        }

        void DrawObjectSnapPreview(Bounds lb)
        {
            if (!snapBase) return;

            Transform bt = snapBase.transform;
            Bounds bb = GetWorldBounds(snapBase);
            Handles.color = AnchorColor;
            Handles.DrawWireCube(bb.center, bb.size);
            DrawMarker(bt.position, AnchorColor);

            if (objSnapShowCandidates)
            {
                Quaternion rot = SnapAxesRotation(bt);
                float yOff = objSnapY == SnapYMode.Cursor && snapHasTarget
                    ? (Quaternion.Inverse(rot) * (snapTarget - bt.position)).y
                    : 0f;
                Handles.color = new Color(1f, 1f, 1f, 0.6f);
                for (int ix = -2; ix <= 2; ix++)
                for (int iz = -2; iz <= 2; iz++)
                {
                    if (Mathf.Abs(objSnapStep.x) <= 1e-4f && ix != 0) continue;
                    if (Mathf.Abs(objSnapStep.z) <= 1e-4f && iz != 0) continue;
                    Vector3 c = bt.position + rot * new Vector3(ix * Mathf.Abs(objSnapStep.x), yOff, iz * Mathf.Abs(objSnapStep.z));
                    Handles.DotHandleCap(0, c, Quaternion.identity, HandleUtility.GetHandleSize(c) * 0.03f, EventType.Repaint);
                }
            }

            if (snapHasTarget)
            {
                DrawPivotBox(snapTarget, lb, TargetColor);
                Handles.color = TargetColor;
                Handles.DrawDottedLine(bt.position, snapTarget, 3f);
                Vector3 d = Quaternion.Inverse(SnapAxesRotation(bt)) * (snapTarget - bt.position);
                Handles.Label(snapTarget + Vector3.up * 0.3f, $"X{d.x:+0.##;-0.##;0}  Y{d.y:+0.##;-0.##;0}  Z{d.z:+0.##;-0.##;0}", EditorStyles.whiteBoldLabel);
            }
        }

        // =====================================================================
        // ⑥ 選択・名前
        // =====================================================================

        void DrawUtilityGUI()
        {
            var sceneObjects = GetSelectedSceneObjects();

            EditorGUILayout.LabelField("名前の (1) を削除", EditorStyles.boldLabel);
            renameIncludeChildren = EditorGUILayout.Toggle("子オブジェクトも対象", renameIncludeChildren);
            renameRemoveClone = EditorGUILayout.Toggle("\"(Clone)\" も削除", renameRemoveClone);
            using (new EditorGUI.DisabledScope(sceneObjects.Length == 0))
            {
                if (GUILayout.Button($"選択中の {sceneObjects.Length} 個をリネーム", GUILayout.Height(28)))
                    RemoveNumberSuffix(sceneObjects, renameIncludeChildren, renameRemoveClone);
            }

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("同じプレハブを一括選択", EditorStyles.boldLabel);
            selectMatchVariantBase = EditorGUILayout.Toggle(
                new GUIContent("バリアントも同一とみなす", "ON: 元をたどった大本のプレハブで比較（バリアント違いもまとめて選択）\nOFF: 直接参照しているプレハブ（バリアント）単位で比較"),
                selectMatchVariantBase);
            selectIncludeInactive = EditorGUILayout.Toggle("非アクティブも含める", selectIncludeInactive);

            var sources = GetSourcePrefabs(sceneObjects, selectMatchVariantBase);
            if (sources.Count > 0)
                EditorGUILayout.LabelField("対象プレハブ", string.Join(", ", sources.Select(s => s.name)), EditorStyles.wordWrappedMiniLabel);

            using (new EditorGUI.DisabledScope(sources.Count == 0))
            {
                if (GUILayout.Button("同じプレハブのオブジェクトをシーンから全選択", GUILayout.Height(28)))
                    SelectSamePrefabInstances(sources, selectMatchVariantBase, selectIncludeInactive);
            }
            if (sceneObjects.Length > 0 && sources.Count == 0)
                EditorGUILayout.HelpBox("選択中のオブジェクトはプレハブのインスタンスではありません。", MessageType.None);
        }

        static GameObject[] GetSelectedSceneObjects()
        {
            return Selection.gameObjects.Where(g => g && !EditorUtility.IsPersistent(g)).ToArray();
        }

        // 末尾の " (1)" "(12)" を（連続していても）まとめて削除。オプションで "(Clone)" も
        static readonly Regex NumberSuffix = new Regex(@"(\s*\(\d+\))+\s*$", RegexOptions.Compiled);
        static readonly Regex NumberOrCloneSuffix = new Regex(@"(\s*\((\d+|Clone)\))+\s*$", RegexOptions.Compiled);

        static void RemoveNumberSuffix(GameObject[] targets, bool includeChildren, bool removeClone)
        {
            var regex = removeClone ? NumberOrCloneSuffix : NumberSuffix;
            var all = includeChildren
                ? targets.SelectMany(t => t.GetComponentsInChildren<Transform>(true)).Select(t => t.gameObject).Distinct()
                : targets;

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Remove Number Suffix");

            int changed = 0;
            foreach (var go in all)
            {
                string newName = regex.Replace(go.name, "");
                if (newName.Length == 0 || newName == go.name) continue;
                Undo.RecordObject(go, "Remove Number Suffix");
                go.name = newName;
                changed++;
            }

            Undo.CollapseUndoOperations(group);
            Debug.Log($"[Placement Tool] {changed} 個のオブジェクトをリネームしました。");
        }

        // 選択オブジェクトが属するプレハブ（子を選んでいても最寄りのインスタンスルートから取得）
        static List<GameObject> GetSourcePrefabs(GameObject[] sceneObjects, bool originalSource)
        {
            var result = new List<GameObject>();
            foreach (var go in sceneObjects)
            {
                var root = PrefabUtility.GetNearestPrefabInstanceRoot(go);
                if (!root) continue;
                var asset = GetPrefabSource(root, originalSource);
                if (asset && !result.Contains(asset)) result.Add(asset);
            }
            return result;
        }

        static GameObject GetPrefabSource(GameObject instanceRoot, bool originalSource)
        {
            return originalSource
                ? PrefabUtility.GetCorrespondingObjectFromOriginalSource(instanceRoot)
                : PrefabUtility.GetCorrespondingObjectFromSource(instanceRoot);
        }

        static void SelectSamePrefabInstances(List<GameObject> sources, bool originalSource, bool includeInactive)
        {
            var targets = new HashSet<GameObject>(sources);
            var found = new List<Object>();

            foreach (var scene in GetSearchScenes())
            {
                foreach (var rootObj in scene.GetRootGameObjects())
                {
                    foreach (var t in rootObj.GetComponentsInChildren<Transform>(true))
                    {
                        var go = t.gameObject;
                        if (!includeInactive && !go.activeInHierarchy) continue;
                        if (!PrefabUtility.IsAnyPrefabInstanceRoot(go)) continue;
                        var src = GetPrefabSource(go, originalSource);
                        if (src && targets.Contains(src)) found.Add(go);
                    }
                }
            }

            Selection.objects = found.ToArray();
            Debug.Log($"[Placement Tool] {found.Count} 個を選択しました（{string.Join(", ", sources.Select(s => s.name))}）。");
        }

        static IEnumerable<Scene> GetSearchScenes()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null)
            {
                yield return stage.scene;
                yield break;
            }
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded) yield return s;
            }
        }

        // =====================================================================
        // ユーティリティ
        // =====================================================================

        static bool IsPrefabAsset(GameObject g)
        {
            return g
                && EditorUtility.IsPersistent(g)
                && g.transform.parent == null
                && PrefabUtility.GetPrefabAssetType(g) != PrefabAssetType.NotAPrefab;
        }

        void SetPrefab(GameObject g)
        {
            prefab = g;
            hasCachedBounds = false;
            ResetTransientState();
            SavePrefs();
            Repaint();
            SceneView.RepaintAll();
        }

        void ResetTransientState()
        {
            lineHasStart = false;
            lineSkipFirst = false;
            hasHover = false;
            connectHasTarget = false;
            snapBase = null;
            snapHasTarget = false;
        }

        Bounds GetPrefabLocalBounds()
        {
            if (!hasCachedBounds || cachedBoundsPrefab != prefab)
            {
                cachedBoundsPrefab = prefab;
                hasCachedBounds = true;
                if (prefab && TryGetWorldBounds(prefab, out var b))
                    prefabLocalBounds = new Bounds(b.center - prefab.transform.position, b.size);
                else
                    prefabLocalBounds = new Bounds(Vector3.zero, Vector3.one);
            }
            return prefabLocalBounds;
        }

        static Bounds GetWorldBounds(GameObject go)
        {
            return TryGetWorldBounds(go, out var b) ? b : new Bounds(go.transform.position, Vector3.one);
        }

        // プレハブアセットでもシーン内オブジェクトでも同じ方法でワールドAABBを計算する
        static bool TryGetWorldBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            bool has = false;

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled || !IsActiveUnder(r.transform, root.transform)) continue;

                Bounds local;
                if (r is MeshRenderer && r.TryGetComponent(out MeshFilter mf) && mf.sharedMesh) local = mf.sharedMesh.bounds;
                else if (r is SkinnedMeshRenderer smr && smr.sharedMesh) local = smr.sharedMesh.bounds;
                else if (r is SpriteRenderer sr && sr.sprite) local = sr.sprite.bounds;
                else continue;

                Encapsulate(ref has, ref bounds, local, r.transform.localToWorldMatrix);
            }

            if (!has)
            {
                foreach (var col in root.GetComponentsInChildren<BoxCollider>(true))
                {
                    if (!col.enabled || !IsActiveUnder(col.transform, root.transform)) continue;
                    Encapsulate(ref has, ref bounds, new Bounds(col.center, col.size), col.transform.localToWorldMatrix);
                }
            }
            return has;
        }

        static void Encapsulate(ref bool has, ref Bounds acc, Bounds local, Matrix4x4 m)
        {
            Vector3 c = local.center, e = local.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    c.x + ((i & 1) == 0 ? -e.x : e.x),
                    c.y + ((i & 2) == 0 ? -e.y : e.y),
                    c.z + ((i & 4) == 0 ? -e.z : e.z));
                Vector3 p = m.MultiplyPoint3x4(corner);
                if (!has)
                {
                    acc = new Bounds(p, Vector3.zero);
                    has = true;
                }
                else acc.Encapsulate(p);
            }
        }

        static bool IsActiveUnder(Transform t, Transform root)
        {
            for (; t != null; t = t.parent)
            {
                if (!t.gameObject.activeSelf) return false;
                if (t == root) break;
            }
            return true;
        }

        // ---------------------------------------------------------------------
        // 設定の保存
        // ---------------------------------------------------------------------

        static string GuidOf(Object o) => o ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(o)) : "";

        static GameObject LoadByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        void SavePrefs()
        {
            EditorPrefs.SetInt(Pref + "Mode", (int)mode);
            EditorPrefs.SetString(Pref + "Prefab", GuidOf(prefab));
            EditorPrefs.SetString(Pref + "Palette", string.Join(";", palette.Where(p => p).Select(p => GuidOf(p))));
            EditorPrefs.SetBool(Pref + "Follow", followProjectSelection);
            EditorPrefs.SetBool(Pref + "AlignBottom", alignBottom);
            EditorPrefs.SetFloat(Pref + "GroundHeight", groundHeight);
            EditorPrefs.SetBool(Pref + "SelectPlaced", selectPlaced);
            EditorPrefs.SetFloat(Pref + "PlaceYaw", placeYaw);
            EditorPrefs.SetFloat(Pref + "RotateStep", rotateStep);
            EditorPrefs.SetFloat(Pref + "LineSpacing", lineSpacing);
            EditorPrefs.SetBool(Pref + "LineSnap", lineSnapToGrid);
            EditorPrefs.SetBool(Pref + "LineChain", lineChain);
            EditorPrefs.SetFloat(Pref + "GridSize", gridSize);
            EditorPrefs.SetFloat(Pref + "GridOX", gridOrigin.x);
            EditorPrefs.SetFloat(Pref + "GridOY", gridOrigin.y);
            EditorPrefs.SetFloat(Pref + "GridOZ", gridOrigin.z);
            EditorPrefs.SetBool(Pref + "GridSnapY", gridSnapY);
            EditorPrefs.SetFloat(Pref + "ConnectGap", connectGap);
            EditorPrefs.SetBool(Pref + "ConnectVertical", connectAllowVertical);
            EditorPrefs.SetFloat(Pref + "ObjSnapX", objSnapStep.x);
            EditorPrefs.SetFloat(Pref + "ObjSnapY", objSnapStep.y);
            EditorPrefs.SetFloat(Pref + "ObjSnapZ", objSnapStep.z);
            EditorPrefs.SetInt(Pref + "ObjSnapYMode", (int)objSnapY);
            EditorPrefs.SetBool(Pref + "ObjSnapLocal", objSnapLocalAxes);
            EditorPrefs.SetBool(Pref + "ObjSnapPrefabRoot", objSnapUsePrefabRoot);
            EditorPrefs.SetBool(Pref + "ObjSnapSkipBase", objSnapSkipBase);
            EditorPrefs.SetBool(Pref + "ObjSnapCandidates", objSnapShowCandidates);
            SaveScatterPrefs();
            EditorPrefs.SetFloat(Pref + "CopyHeight", copyHeight);
            EditorPrefs.SetBool(Pref + "RenameChildren", renameIncludeChildren);
            EditorPrefs.SetBool(Pref + "RenameClone", renameRemoveClone);
            EditorPrefs.SetBool(Pref + "SelectVariantBase", selectMatchVariantBase);
            EditorPrefs.SetBool(Pref + "SelectInactive", selectIncludeInactive);
        }

        void LoadPrefs()
        {
            mode = (Mode)Mathf.Clamp(EditorPrefs.GetInt(Pref + "Mode", (int)mode), 0, ModeLabels.Length - 1);
            var p = LoadByGuid(EditorPrefs.GetString(Pref + "Prefab", ""));
            if (p) prefab = p;

            string paletteStr = EditorPrefs.GetString(Pref + "Palette", "");
            if (!string.IsNullOrEmpty(paletteStr))
            {
                palette = paletteStr.Split(';').Select(LoadByGuid).Where(g => g).Distinct().ToList();
            }
            palette ??= new List<GameObject>();

            followProjectSelection = EditorPrefs.GetBool(Pref + "Follow", followProjectSelection);
            alignBottom = EditorPrefs.GetBool(Pref + "AlignBottom", alignBottom);
            groundHeight = EditorPrefs.GetFloat(Pref + "GroundHeight", groundHeight);
            selectPlaced = EditorPrefs.GetBool(Pref + "SelectPlaced", selectPlaced);
            placeYaw = EditorPrefs.GetFloat(Pref + "PlaceYaw", placeYaw);
            rotateStep = EditorPrefs.GetFloat(Pref + "RotateStep", rotateStep);
            lineSpacing = EditorPrefs.GetFloat(Pref + "LineSpacing", lineSpacing);
            lineSnapToGrid = EditorPrefs.GetBool(Pref + "LineSnap", lineSnapToGrid);
            lineChain = EditorPrefs.GetBool(Pref + "LineChain", lineChain);
            gridSize = EditorPrefs.GetFloat(Pref + "GridSize", gridSize);
            gridOrigin = new Vector3(
                EditorPrefs.GetFloat(Pref + "GridOX", gridOrigin.x),
                EditorPrefs.GetFloat(Pref + "GridOY", gridOrigin.y),
                EditorPrefs.GetFloat(Pref + "GridOZ", gridOrigin.z));
            gridSnapY = EditorPrefs.GetBool(Pref + "GridSnapY", gridSnapY);
            connectGap = EditorPrefs.GetFloat(Pref + "ConnectGap", connectGap);
            connectAllowVertical = EditorPrefs.GetBool(Pref + "ConnectVertical", connectAllowVertical);
            objSnapStep = new Vector3(
                EditorPrefs.GetFloat(Pref + "ObjSnapX", objSnapStep.x),
                EditorPrefs.GetFloat(Pref + "ObjSnapY", objSnapStep.y),
                EditorPrefs.GetFloat(Pref + "ObjSnapZ", objSnapStep.z));
            objSnapY = (SnapYMode)Mathf.Clamp(EditorPrefs.GetInt(Pref + "ObjSnapYMode", (int)objSnapY), 0, SnapYLabels.Length - 1);
            objSnapLocalAxes = EditorPrefs.GetBool(Pref + "ObjSnapLocal", objSnapLocalAxes);
            objSnapUsePrefabRoot = EditorPrefs.GetBool(Pref + "ObjSnapPrefabRoot", objSnapUsePrefabRoot);
            objSnapSkipBase = EditorPrefs.GetBool(Pref + "ObjSnapSkipBase", objSnapSkipBase);
            objSnapShowCandidates = EditorPrefs.GetBool(Pref + "ObjSnapCandidates", objSnapShowCandidates);
            LoadScatterPrefs();
            copyHeight = EditorPrefs.GetFloat(Pref + "CopyHeight", copyHeight);
            renameIncludeChildren = EditorPrefs.GetBool(Pref + "RenameChildren", renameIncludeChildren);
            renameRemoveClone = EditorPrefs.GetBool(Pref + "RenameClone", renameRemoveClone);
            selectMatchVariantBase = EditorPrefs.GetBool(Pref + "SelectVariantBase", selectMatchVariantBase);
            selectIncludeInactive = EditorPrefs.GetBool(Pref + "SelectInactive", selectIncludeInactive);
            hasCachedBounds = false;
        }
    }
}
