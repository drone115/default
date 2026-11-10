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
        enum Mode { Place, Line, Grid, Connect, CopyUp, Utility }

        static readonly string[] ModeLabels = { "① 配置", "② ライン", "③ グリッド", "④ 連結", "⑤ 上にコピー", "⑥ 選択・名前" };
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

        [SerializeField] float lineSpacing = 2f;
        [SerializeField] bool lineSnapToGrid;
        [SerializeField] bool lineChain;

        [SerializeField] float gridSize = 1f;
        [SerializeField] Vector3 gridOrigin;
        [SerializeField] bool gridSnapY;

        [SerializeField] GameObject anchor;
        [SerializeField] float connectGap;
        [SerializeField] bool connectAllowVertical;

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
        void OnProjectChange() => hasCachedBounds = false;

        void OnSelectionChange()
        {
            if (followProjectSelection && Selection.activeObject is GameObject g && IsPrefabAsset(g))
                SetPrefab(g);
            Repaint();
        }

        void OnUndoRedo()
        {
            // 連結モード: 直前のパーツが Undo で消えたら、復元された選択を基準にする
            if (!anchor && Selection.activeGameObject && !EditorUtility.IsPersistent(Selection.activeGameObject))
                anchor = Selection.activeGameObject;
            connectHasTarget = false;
            Repaint();
            SceneView.RepaintAll();
        }

        void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            var newMode = (Mode)GUILayout.SelectionGrid((int)mode, ModeLabels, 3, GUILayout.Height(48));
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
                }
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(GetHelpText(), MessageType.Info);

            if (EditorGUI.EndChangeCheck())
            {
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
            if (mode != Mode.Connect)
                alignBottom = EditorGUILayout.Toggle(
                    new GUIContent("底面を接地", "ピボットではなくバウンズの底面をクリックした面に合わせます"), alignBottom);
            groundHeight = EditorGUILayout.FloatField(
                new GUIContent("地面の高さ", "何もない場所をクリックしたときに使う水平面の高さ (Y)"), groundHeight);
            selectPlaced = EditorGUILayout.Toggle("配置後に選択する", selectPlaced);
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
            if (!sceneInputEnabled || mode >= Mode.CopyUp) return;

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
                    case Mode.Connect: state = anchor ? $"基準: {anchor.name}" : "最初のパーツをクリックで配置"; break;
                    default: state = $"左クリックで配置: {prefab.name}"; break;
                }
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
                        Vector3 pivot = connectTargetCenter - GetPrefabLocalBounds().center;
                        var created = PlaceMany(new[] { pivot }, "Place Prefab (Connect)", connectTargetCenter);
                        if (created.Count > 0) anchor = created[0];
                    }
                    UpdateConnectTarget();
                    Repaint();
                    break;
            }
        }

        void UpdateConnectTarget()
        {
            connectHasTarget = false;
            if (mode != Mode.Connect || !anchor || !hasHover || !prefab) return;

            Bounds a = GetWorldBounds(anchor);
            Bounds lb = GetPrefabLocalBounds();
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
                        Handles.DrawWireCube(connectTargetCenter, lb.size);
                    }
                    break;
            }
        }

        static void DrawPivotBox(Vector3 pivot, Bounds localBounds, Color color)
        {
            Handles.color = color;
            Handles.DrawWireCube(pivot + localBounds.center, localBounds.size);
            DrawMarker(pivot, color);
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
            copyHeight = EditorPrefs.GetFloat(Pref + "CopyHeight", copyHeight);
            renameIncludeChildren = EditorPrefs.GetBool(Pref + "RenameChildren", renameIncludeChildren);
            renameRemoveClone = EditorPrefs.GetBool(Pref + "RenameClone", renameRemoveClone);
            selectMatchVariantBase = EditorPrefs.GetBool(Pref + "SelectVariantBase", selectMatchVariantBase);
            selectIncludeInactive = EditorPrefs.GetBool(Pref + "SelectInactive", selectIncludeInactive);
            hasCachedBounds = false;
        }
    }
}
