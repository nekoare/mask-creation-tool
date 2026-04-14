using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace NekoareMaskTool.Editor
{
    /// <summary>
    /// Scene View 上の左クリックを購読し、ポリゴン/アイランド単位でマスクを塗る。
    /// 右クリック・中クリック・Alt+ドラッグなど既存のScene操作には触れない（パススルー）。
    /// </summary>
    public class SceneInputHandler
    {
        private bool _enabled;
        private bool _subscribed;

        private MaskCanvasModel _model;
        private MaskDrawController _drawController;
        private IslandSelector _islandSelector;
        private Renderer _targetRenderer;
        private int _selectedMaterialIndex;

        private SceneMeshPicker _picker = new SceneMeshPicker();
        private bool _isDrawing;

        // ホバープレビュー用の候補triangle（描画用）
        private List<int> _hoverTriangles;
        private Vector3[] _hoverIslandTriVerts;  // Selection用、3点ずつ並ぶ flat 配列
        // Pen/Eraser用: 実際に塗られるスクリーンサークルを表す disc
        private bool _hasHoverBrush;
        private Vector3 _hoverBrushWorldPoint;
        private Vector3 _hoverBrushCameraDir;
        private float _hoverBrushPixelRadius;
        private static readonly Color HoverColorPen = new Color(0.2f, 1f, 0.3f, 0.35f);
        private static readonly Color HoverColorSelection = new Color(0.3f, 0.6f, 1f, 0.35f);

        /// <summary>
        /// ストローク完了時に呼ばれるコールバック（Window側のUndo記録用）
        /// </summary>
        public Action OnStrokeComplete;

        /// <summary>
        /// キャンバス再合成リクエスト（Window側がRefreshComposite/Repaintを呼ぶ）
        /// </summary>
        public Action OnPaintApplied;

        public void SetDependencies(MaskCanvasModel model, MaskDrawController drawController, IslandSelector islandSelector)
        {
            _model = model;
            _drawController = drawController;
            _islandSelector = islandSelector;
        }

        public void SetIslandSelector(IslandSelector islandSelector)
        {
            _islandSelector = islandSelector;
        }

        public void SetEnabled(bool enabled)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;
            UpdateSubscription();
            UpdatePickerLifecycle();
        }

        public void SetTarget(Renderer renderer, int materialIndex)
        {
            bool changed = _targetRenderer != renderer || _selectedMaterialIndex != materialIndex;
            _targetRenderer = renderer;
            _selectedMaterialIndex = materialIndex;
            if (changed)
            {
                UpdatePickerLifecycle();
                ClearHover();
            }
        }

        private void UpdatePickerLifecycle()
        {
            // 有効＋ターゲット設定済み の場合のみ Picker を維持（ホバー用にレイキャスト可能にしておく）
            if (_enabled && _targetRenderer != null)
            {
                _picker.BeginPicking(_targetRenderer, _selectedMaterialIndex);
            }
            else
            {
                _picker.EndPicking();
                ClearHover();
            }
        }

        private void ClearHover()
        {
            _hoverTriangles = null;
            _hoverIslandTriVerts = null;
            _hasHoverBrush = false;
        }

        public void Dispose()
        {
            _enabled = false;
            UpdateSubscription();
            _picker?.EndPicking();
        }

        private void UpdateSubscription()
        {
            if (_enabled && !_subscribed)
            {
                SceneView.duringSceneGui += OnSceneGUI;
                _subscribed = true;
            }
            else if (!_enabled && _subscribed)
            {
                SceneView.duringSceneGui -= OnSceneGUI;
                _subscribed = false;
                AbortStroke();
            }
        }

        private void AbortStroke()
        {
            if (_isDrawing)
            {
                _isDrawing = false;
                _picker?.EndPicking();
            }
        }

        private void OnSceneGUI(SceneView sceneView)
        {
            if (!_enabled || _targetRenderer == null || _model == null || _drawController == null) return;

            var e = Event.current;

            // マウスが SceneView から出たらホバーを消す
            if (e.type == EventType.MouseLeaveWindow)
            {
                if (_hoverTriangles != null || _hoverIslandTriVerts != null)
                {
                    ClearHover();
                    sceneView.Repaint();
                }
                return;
            }

            // ホバー更新（左クリック中以外）— button 0 でも MouseMove は button=-1 や 0 など
            if (e.type == EventType.MouseMove && !_isDrawing)
            {
                UpdateHover(e.mousePosition, sceneView);
                sceneView.Repaint();
                return;
            }

            // Repaintで毎回オーバーレイ描画。ただしマウスがSceneViewの描画領域外なら描かない
            if (e.type == EventType.Repaint)
            {
                if (IsMouseInsideSceneView(sceneView))
                {
                    DrawHoverOverlay();
                }
                return;
            }

            // 左クリック以外のマウスイベントはパススルー（カメラ操作維持）
            if (e.button != 0) return;

            switch (e.type)
            {
                case EventType.MouseDown:
                    BeginStroke(e.mousePosition, sceneView);
                    UpdateHover(e.mousePosition, sceneView);
                    e.Use();
                    break;
                case EventType.MouseDrag:
                    if (!_isDrawing) return;
                    ContinueStroke(e.mousePosition, sceneView);
                    UpdateHover(e.mousePosition, sceneView);
                    e.Use();
                    break;
                case EventType.MouseUp:
                    if (!_isDrawing) return;
                    EndStroke();
                    sceneView.Repaint();
                    e.Use();
                    break;
            }
        }

        private void UpdateHover(Vector2 mousePos, SceneView sceneView)
        {
            var camera = sceneView != null ? sceneView.camera : null;
            if (camera == null || !_picker.IsActive)
            {
                ClearHover();
                return;
            }

            switch (_drawController.CurrentTool)
            {
                case DrawTool.Pen:
                case DrawTool.Eraser:
                {
                    // Penオーバーレイは「塗られる円形範囲」を示す screen-space ディスクで表現する
                    // （以前の triangle 列挙は実塗り範囲と一致しなかったため）
                    int radius = Mathf.Max(1, _drawController.BrushSize);
                    if (_picker.TryPick(mousePos, camera, out var hit))
                    {
                        _hoverBrushWorldPoint = hit.worldPoint;
                        _hoverBrushCameraDir = (hit.worldPoint - camera.transform.position).normalized;
                        _hoverBrushPixelRadius = radius;
                        _hasHoverBrush = true;
                    }
                    else
                    {
                        _hasHoverBrush = false;
                    }
                    _hoverTriangles = null;
                    _hoverIslandTriVerts = null;
                    break;
                }
                case DrawTool.Selection:
                {
                    if (_picker.TryPick(mousePos, camera, out var hit) && _islandSelector != null)
                    {
                        var triVerts = ResolveIslandTriangleWorldVerts(hit.uv);
                        _hoverIslandTriVerts = triVerts;
                        _hoverTriangles = null;
                    }
                    else
                    {
                        ClearHover();
                    }
                    break;
                }
                default:
                    ClearHover();
                    break;
            }
        }

        private Vector3[] ResolveIslandTriangleWorldVerts(Vector2 uv)
        {
            if (_islandSelector == null) return null;
            int width = _model.Width;
            int height = _model.Height;
            var texCoord = new Vector2Int(
                Mathf.Clamp(Mathf.FloorToInt(uv.x * width), 0, width - 1),
                Mathf.Clamp(Mathf.FloorToInt(uv.y * height), 0, height - 1)
            );
            var triNumbers = _islandSelector.GetIslandTriangleNumbers(texCoord);
            if (triNumbers == null || triNumbers.Count == 0) return null;
            var verts = new Vector3[triNumbers.Count * 3];
            int w = 0;
            foreach (var triNum in triNumbers)
            {
                if (_picker.TryGetTriangleWorldVerts(triNum, out var v0, out var v1, out var v2))
                {
                    verts[w++] = v0;
                    verts[w++] = v1;
                    verts[w++] = v2;
                }
            }
            if (w == 0) return null;
            // 余白を切り詰める
            if (w < verts.Length)
            {
                var trimmed = new Vector3[w];
                System.Array.Copy(verts, trimmed, w);
                return trimmed;
            }
            return verts;
        }

        /// <summary>
        /// マウスが SceneView 上にあるかチェック。他ウィンドウに行ったら残留ホバーを描画しない。
        /// </summary>
        private static bool IsMouseInsideSceneView(SceneView sceneView)
        {
            return sceneView != null && EditorWindow.mouseOverWindow == sceneView;
        }

        private void DrawHoverOverlay()
        {
            bool isSelection = _drawController.CurrentTool == DrawTool.Selection;
            Color color = isSelection ? HoverColorSelection : HoverColorPen;

            // Pen/Eraser: 実塗り範囲の screen-space 円形ディスクを描画
            if (_hasHoverBrush)
            {
                var cam = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null;
                if (cam != null)
                {
                    // world半径 ≒ pixel半径 / カメラでの world-per-pixel スケール
                    float worldPerPixel = HandleUtility.GetHandleSize(_hoverBrushWorldPoint) * 0.01f;
                    // 試行錯誤なしで正確にスクリーン半径に合わせるため、screen と world の換算を計算
                    Vector3 offsetWS = cam.transform.right;
                    Vector3 pA = cam.WorldToScreenPoint(_hoverBrushWorldPoint);
                    Vector3 pB = cam.WorldToScreenPoint(_hoverBrushWorldPoint + offsetWS);
                    float pxPerWorld = (pB - pA).magnitude;
                    float worldRadius = pxPerWorld > 0 ? _hoverBrushPixelRadius / pxPerWorld : worldPerPixel * _hoverBrushPixelRadius;

                    Handles.color = color;
                    // 視線方向と逆向きの法線でカメラ向きに disc を貼る
                    Handles.DrawSolidDisc(_hoverBrushWorldPoint, -_hoverBrushCameraDir, worldRadius);
                }
            }
            else if (_hoverTriangles != null && _hoverTriangles.Count > 0)
            {
                Handles.color = color;
                foreach (var triNum in _hoverTriangles)
                {
                    if (_picker.TryGetTriangleWorldVerts(triNum, out var v0, out var v1, out var v2))
                    {
                        Handles.DrawAAConvexPolygon(v0, v1, v2);
                    }
                }
            }
            else if (_hoverIslandTriVerts != null && _hoverIslandTriVerts.Length >= 3)
            {
                Handles.color = color;
                for (int i = 0; i + 2 < _hoverIslandTriVerts.Length; i += 3)
                {
                    Handles.DrawAAConvexPolygon(_hoverIslandTriVerts[i], _hoverIslandTriVerts[i + 1], _hoverIslandTriVerts[i + 2]);
                }
            }
        }

        private Vector2 _lastApplyPos;
        private bool _hasLastApplyPos;
        // 補間中に1回の SyncCpuToGpu でまとめて適用するためのピクセル蓄積
        private readonly HashSet<int> _accumulatedPixels = new HashSet<int>();

        private void BeginStroke(Vector2 mousePos, SceneView sceneView)
        {
            _picker.BeginPicking(_targetRenderer, _selectedMaterialIndex);
            _isDrawing = true;
            _hasLastApplyPos = false;
            // Pen/Eraserは内挿バッチ経路、それ以外（Selection等）はApplyAt即時
            if (IsBrushTool())
            {
                _accumulatedPixels.Clear();
                AccumulateAt(mousePos, sceneView);
                FlushAccumulated();
            }
            else
            {
                ApplyAt(mousePos, sceneView);
            }
            _lastApplyPos = mousePos;
            _hasLastApplyPos = true;
        }

        private void ContinueStroke(Vector2 mousePos, SceneView sceneView)
        {
            if (IsBrushTool())
            {
                // 前回位置→今回位置の間を内挿して全ステップのピクセルを1回の SyncCpuToGpu で適用
                _accumulatedPixels.Clear();
                if (_hasLastApplyPos)
                {
                    int radius = Mathf.Max(1, _drawController.BrushSize);
                    float step = Mathf.Max(2f, radius * 0.5f);
                    Vector2 delta = mousePos - _lastApplyPos;
                    float dist = delta.magnitude;
                    if (dist > step)
                    {
                        int steps = Mathf.CeilToInt(dist / step);
                        for (int i = 1; i < steps; i++)
                        {
                            Vector2 interp = Vector2.Lerp(_lastApplyPos, mousePos, (float)i / steps);
                            AccumulateAt(interp, sceneView);
                        }
                    }
                }
                AccumulateAt(mousePos, sceneView);
                FlushAccumulated();
            }
            else
            {
                ApplyAt(mousePos, sceneView);
            }
            _lastApplyPos = mousePos;
            _hasLastApplyPos = true;
        }

        private bool IsBrushTool()
        {
            return _drawController.CurrentTool == DrawTool.Pen || _drawController.CurrentTool == DrawTool.Eraser;
        }

        private void AccumulateAt(Vector2 mousePos, SceneView sceneView)
        {
            var camera = sceneView != null ? sceneView.camera : null;
            if (camera == null) return;
            int radius = Mathf.Max(1, _drawController.BrushSize);
            float searchRadius = radius * 2f;
            var triangleNumbers = _picker.EnumerateVisibleTrianglesInScreenRadius(mousePos, camera, searchRadius);
            if (triangleNumbers.Count == 0) return;
            var triangles = new List<SceneStrokeApplier.TriangleData>(triangleNumbers.Count);
            foreach (var triNum in triangleNumbers)
            {
                if (!_picker.TryGetTriangleUVs(triNum, out var uv0, out var uv1, out var uv2)) continue;
                if (!_picker.TryGetTriangleWorldVerts(triNum, out var v0, out var v1, out var v2)) continue;
                triangles.Add(new SceneStrokeApplier.TriangleData
                {
                    uv0 = uv0, uv1 = uv1, uv2 = uv2,
                    v0WS = v0, v1WS = v1, v2WS = v2
                });
            }
            if (triangles.Count == 0) return;
            Vector2 mouseScreen = HandleUtility.GUIPointToScreenPixelCoordinate(mousePos);
            SceneStrokeApplier.GatherPixelsScreenCircle(triangles, camera, mouseScreen, radius, _model.Width, _model.Height, _accumulatedPixels);
        }

        private void FlushAccumulated()
        {
            if (_accumulatedPixels.Count == 0) return;
            float value = _drawController.CurrentTool == DrawTool.Eraser ? 0f : _drawController.BrushValue;
            SceneStrokeApplier.ApplyPixels(_model, _accumulatedPixels, value);
            OnPaintApplied?.Invoke();
        }

        private void EndStroke()
        {
            _isDrawing = false;
            _hasLastApplyPos = false;
            _accumulatedPixels.Clear();
            OnStrokeComplete?.Invoke();
        }

        private void ApplyAt(Vector2 mousePos, SceneView sceneView)
        {
            var camera = sceneView != null ? sceneView.camera : null;
            if (camera == null) return;

            switch (_drawController.CurrentTool)
            {
                case DrawTool.Pen:
                    PaintPolygons(mousePos, camera, _drawController.BrushValue);
                    break;
                case DrawTool.Eraser:
                    PaintPolygons(mousePos, camera, 0f);
                    break;
                case DrawTool.Selection:
                    PaintIsland(mousePos, camera);
                    break;
                // Smudge は Sceneクリック対象外
            }
        }

        private void PaintPolygons(Vector2 mousePos, Camera camera, float value)
        {
            int radius = Mathf.Max(1, _drawController.BrushSize);
            float searchRadius = radius * 2f;
            var triangleNumbers = _picker.EnumerateVisibleTrianglesInScreenRadius(mousePos, camera, searchRadius);
            if (triangleNumbers.Count == 0) return;

            var triangles = new List<SceneStrokeApplier.TriangleData>(triangleNumbers.Count);
            foreach (var triNum in triangleNumbers)
            {
                if (!_picker.TryGetTriangleUVs(triNum, out var uv0, out var uv1, out var uv2)) continue;
                if (!_picker.TryGetTriangleWorldVerts(triNum, out var v0, out var v1, out var v2)) continue;
                triangles.Add(new SceneStrokeApplier.TriangleData
                {
                    uv0 = uv0, uv1 = uv1, uv2 = uv2,
                    v0WS = v0, v1WS = v1, v2WS = v2
                });
            }
            if (triangles.Count == 0) return;

            Vector2 mouseScreen = HandleUtility.GUIPointToScreenPixelCoordinate(mousePos);
            // ピクセル蓄積のみ（SyncCpuToGpuはストローク全体で1回だけ呼ぶ）
            _accumulatedPixels.Clear();
            SceneStrokeApplier.GatherPixelsScreenCircle(triangles, camera, mouseScreen, radius, _model.Width, _model.Height, _accumulatedPixels);
            if (_accumulatedPixels.Count > 0)
            {
                SceneStrokeApplier.ApplyPixels(_model, _accumulatedPixels, value);
                OnPaintApplied?.Invoke();
            }
        }

        private void PaintIsland(Vector2 mousePos, Camera camera)
        {
            if (_islandSelector == null) return;
            if (!_picker.TryPick(mousePos, camera, out var hit)) return;

            // hit.uv をテクセル座標に変換し、IslandSelector に渡す
            int width = _model.Width;
            int height = _model.Height;
            var texCoord = new Vector2Int(
                Mathf.Clamp(Mathf.FloorToInt(hit.uv.x * width), 0, width - 1),
                Mathf.Clamp(Mathf.FloorToInt(hit.uv.y * height), 0, height - 1)
            );

            var pixels = _islandSelector.GetIslandPixels(texCoord);
            if (pixels == null || pixels.Count == 0) return;
            _drawController.FillPixels(_model, pixels);
            OnPaintApplied?.Invoke();
        }
    }
}
