using UnityEngine;
using UnityEditor;

namespace NekoareMaskTool.Editor
{
    public enum DrawTool
    {
        Pen = 0,
        Eraser = 1,
        Selection = 2,
        Smudge = 3
    }

    public enum MaskDisplayColor
    {
        Gray = 0,
        Red = 1,
        Green = 2,
        Blue = 3
    }

    /// <summary>
    /// ComputeShaderを使用したマスク描画コントローラー
    /// </summary>
    public class MaskDrawController
    {
        private ComputeShader _drawShader;
        private int _drawKernel;
        private int _rebuildKernel;
        private int _smudgePickupKernel;
        private int _smudgeApplyKernel;

        private DrawTool _currentTool = DrawTool.Pen;
        private int _brushSize = 16;
        private float _brushValue = 0f; // グレースケール値 (0.0~1.0)
        private Vector2Int _previousPosition = new Vector2Int(-1, -1); // 前回の描画位置（MeshDeleterWithTexture方式）

        // 消しゴムの強さ
        private float _eraserStrength = 1f;

        // スマッジ用
        private int _smudgeBrushSize = 16;
        private float _smudgeStrength = 0.2f;
        private ComputeBuffer _smudgeBuffer;
        private int _smudgeBufferDiameter;

        // 選択ツール: 塗る/消すモード（trueで塗る、falseで消す）
        private bool _selectionFillMode = true;

        // スポイトモード（一時的にONにして濃さをピックする）
        private bool _isEyedropperMode = false;

        // 見え方確認モード（Sceneプレビューで2ndカラーとしてマスクを適用）
        private bool _isAppearancePreview = false;
        private Color _preview2ndColor = Color.red;

        // はみ出し防止用
        private bool _clampToIsland = true;
        private RenderTexture _clipMask;

        // 消しゴムスナップショット（ストローク中の重複適用防止）
        private RenderTexture _eraserSnapshot;

        public DrawTool CurrentTool
        {
            get => _currentTool;
            set => _currentTool = value;
        }

        public int BrushSize
        {
            get => _brushSize;
            set => _brushSize = Mathf.Clamp(value, 1, 300);
        }

        public float BrushValue
        {
            get => _brushValue;
            set => _brushValue = Mathf.Clamp01(value);
        }

        public float EraserStrength
        {
            get => _eraserStrength;
            set => _eraserStrength = Mathf.Clamp01(value);
        }

        public int SmudgeBrushSize
        {
            get => _smudgeBrushSize;
            set => _smudgeBrushSize = Mathf.Clamp(value, 1, 300);
        }

        public float SmudgeStrength
        {
            get => _smudgeStrength;
            set => _smudgeStrength = Mathf.Clamp01(value);
        }

        public bool SelectionFillMode
        {
            get => _selectionFillMode;
            set => _selectionFillMode = value;
        }

        public bool IsEyedropperMode
        {
            get => _isEyedropperMode;
            set => _isEyedropperMode = value;
        }

        public bool IsAppearancePreview
        {
            get => _isAppearancePreview;
            set => _isAppearancePreview = value;
        }

        public Color Preview2ndColor
        {
            get => _preview2ndColor;
            set => _preview2ndColor = value;
        }

        public bool ClampToIsland
        {
            get => _clampToIsland;
            set => _clampToIsland = value;
        }

        /// <summary>
        /// はみ出し防止用クリッピングマスクを設定
        /// </summary>
        public void SetClipMask(RenderTexture clipMask)
        {
            _clipMask = clipMask;
        }

        public void Initialize()
        {
            // ComputeShaderを読み込み（パスに依存しないGUID検索）
            var guids = AssetDatabase.FindAssets("NekoareMaskToolDraw t:ComputeShader");
            if (guids.Length > 0)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                _drawShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            }

            if (_drawShader == null)
            {
                Debug.LogError("[NekoareMaskTool] NekoareMaskToolDraw.compute が見つかりません");
                return;
            }

            _drawKernel = _drawShader.FindKernel("CSDrawBrush");
            _rebuildKernel = _drawShader.FindKernel("CSRebuild");
            _smudgePickupKernel = _drawShader.FindKernel("CSSmudgePickup");
            _smudgeApplyKernel = _drawShader.FindKernel("CSSmudgeApply");
        }

        public void DrawAt(RenderTexture maskTexture, Vector2Int position)
        {
            if (_drawShader == null || maskTexture == null)
                return;

            // MaskTextureのみ更新（PreviewTextureの再構成はComposeCompositeに委譲）
            _drawShader.SetTexture(_drawKernel, "MaskTex", maskTexture);
            _drawShader.SetInt("ClampToIsland", _clampToIsland && _clipMask != null ? 1 : 0);
            if (_clampToIsland && _clipMask != null)
                _drawShader.SetTexture(_drawKernel, "ClipMask", _clipMask);
            _drawShader.SetInt("BrushSize", _brushSize);
            _drawShader.SetFloat("BrushValue", _brushValue);
            _drawShader.SetFloat("EraserStrength", _eraserStrength);
            _drawShader.SetInt("BrushMode", (int)_currentTool);
            // 消しゴムモード時はスナップショットを設定（重複適用防止）
            if (_currentTool == DrawTool.Eraser && _eraserSnapshot != null)
                _drawShader.SetTexture(_drawKernel, "SnapshotTex", _eraserSnapshot);
            else
                _drawShader.SetTexture(_drawKernel, "SnapshotTex", maskTexture);
            // HLSL定数バッファの16バイトアライメント対応（MeshDeleterWithTexture方式）
            // int[2]の各要素が16バイト境界に配置されるため、sizeof(int)=4を乗算してオフセット
            var posArray = new int[2 * sizeof(int)];
            posArray[0 * sizeof(int)] = position.x;
            posArray[1 * sizeof(int)] = position.y;
            _drawShader.SetInts("Pos", posArray);

            var previousPosArray = new int[2 * sizeof(int)];
            previousPosArray[0 * sizeof(int)] = _previousPosition.x;
            previousPosArray[1 * sizeof(int)] = _previousPosition.y;
            _drawShader.SetInts("PreviousPos", previousPosArray);
            _drawShader.SetInt("Width", maskTexture.width);
            _drawShader.SetInt("Height", maskTexture.height);

            // Dispatch（MeshDeleterWithTexture方式：32x32スレッドグループで全体）
            int threadGroupsX = Mathf.CeilToInt(maskTexture.width / 32f);
            int threadGroupsY = Mathf.CeilToInt(maskTexture.height / 32f);
            _drawShader.Dispatch(_drawKernel, threadGroupsX, threadGroupsY, 1);

            // 現在位置を次回の前回位置として保存
            _previousPosition = position;
        }

        /// <summary>
        /// 前回位置をリセット（MeshDeleterWithTexture方式）
        /// </summary>
        public void ResetPreviousPosition()
        {
            _previousPosition = new Vector2Int(-1, -1);
        }

        /// <summary>
        /// 消しゴムストローク開始時にMaskTexのスナップショットを作成
        /// ストローク中は常にスナップショットの値からlerpすることで重複適用を防止
        /// </summary>
        public void BeginEraserStroke(RenderTexture maskTexture)
        {
            if (maskTexture == null) return;

            if (_eraserSnapshot == null ||
                _eraserSnapshot.width != maskTexture.width ||
                _eraserSnapshot.height != maskTexture.height)
            {
                if (_eraserSnapshot != null)
                    _eraserSnapshot.Release();
                // sRGB無効(Linear)で作成: Texture2D(SRV)として読み取る際にガンマ変換が発生しなくなり、
                // RWTexture2DのMaskTexと同じ生値が読める
                var desc = new RenderTextureDescriptor(maskTexture.width, maskTexture.height, maskTexture.format, 0)
                {
                    sRGB = false
                };
                _eraserSnapshot = new RenderTexture(desc);
                _eraserSnapshot.Create();
            }

            // CopyTextureで生のメモリコピー（Blitはシェーダ経由でガンマ変換が入る）
            Graphics.CopyTexture(maskTexture, _eraserSnapshot);
        }

        /// <summary>
        /// 消しゴムストローク終了時にスナップショットを解放
        /// </summary>
        public void EndEraserStroke()
        {
            if (_eraserSnapshot != null)
            {
                _eraserSnapshot.Release();
                _eraserSnapshot = null;
            }
        }

        public void DrawLine(RenderTexture maskTexture, Vector2Int from, Vector2Int to)
        {
            // Bresenhamのライン描画アルゴリズム
            int dx = Mathf.Abs(to.x - from.x);
            int dy = Mathf.Abs(to.y - from.y);
            int sx = from.x < to.x ? 1 : -1;
            int sy = from.y < to.y ? 1 : -1;
            int err = dx - dy;

            int x = from.x;
            int y = from.y;

            while (true)
            {
                DrawAt(maskTexture, new Vector2Int(x, y));

                if (x == to.x && y == to.y)
                    break;

                int e2 = 2 * err;

                if (e2 > -dy)
                {
                    err -= dy;
                    x += sx;
                }

                if (e2 < dx)
                {
                    err += dx;
                    y += sy;
                }
            }
        }

        /// <summary>
        /// PreviewTextureをBackgroundTex × MaskTexで再構成する
        /// CSDrawBrushと同じテクセル読み取り方法を使うため、色空間の不一致が起きない
        /// Import/Undo/Redo後にComposeCompositeから呼ばれる
        /// </summary>
        public void Rebuild(RenderTexture maskTexture, RenderTexture previewTexture, Texture2D backgroundTexture)
        {
            if (_drawShader == null || maskTexture == null || previewTexture == null || backgroundTexture == null)
                return;

            _drawShader.SetTexture(_rebuildKernel, "BackgroundTex", backgroundTexture);
            _drawShader.SetTexture(_rebuildKernel, "MaskTex", maskTexture);
            _drawShader.SetTexture(_rebuildKernel, "PreviewTex", previewTexture);

            int threadGroupsX = Mathf.CeilToInt(maskTexture.width / 32f);
            int threadGroupsY = Mathf.CeilToInt(maskTexture.height / 32f);
            _drawShader.Dispatch(_rebuildKernel, threadGroupsX, threadGroupsY, 1);
        }

        /// <summary>
        /// スマッジ: ブラシ範囲のマスク値をGPUバッファにピックアップ（ストローク開始時）
        /// </summary>
        public void SmudgePickup(RenderTexture maskTexture, Vector2Int position)
        {
            if (_drawShader == null || maskTexture == null)
                return;

            int diameter = _smudgeBrushSize * 2 + 1;

            // バッファサイズが変わった場合は再作成
            if (_smudgeBuffer == null || _smudgeBufferDiameter != diameter)
            {
                _smudgeBuffer?.Dispose();
                _smudgeBuffer = new ComputeBuffer(diameter * diameter, sizeof(float));
                _smudgeBufferDiameter = diameter;
            }

            _drawShader.SetTexture(_smudgePickupKernel, "MaskTex", maskTexture);
            _drawShader.SetBuffer(_smudgePickupKernel, "SmudgeBuffer", _smudgeBuffer);
            _drawShader.SetInt("SmudgeBrushDiameter", diameter);
            _drawShader.SetInt("Width", maskTexture.width);
            _drawShader.SetInt("Height", maskTexture.height);

            var posArray = new int[2 * sizeof(int)];
            posArray[0 * sizeof(int)] = position.x;
            posArray[1 * sizeof(int)] = position.y;
            _drawShader.SetInts("Pos", posArray);

            int threadGroups = Mathf.CeilToInt(diameter / 32f);
            _drawShader.Dispatch(_smudgePickupKernel, threadGroups, threadGroups, 1);
        }

        /// <summary>
        /// スマッジ: バッファの値を移動先に塗り込み、バッファも更新（ドラッグ中）
        /// </summary>
        public void SmudgeApply(RenderTexture maskTexture, Vector2Int position, float strength)
        {
            if (_drawShader == null || maskTexture == null || _smudgeBuffer == null)
                return;

            _drawShader.SetTexture(_smudgeApplyKernel, "MaskTex", maskTexture);
            _drawShader.SetInt("ClampToIsland", _clampToIsland && _clipMask != null ? 1 : 0);
            if (_clampToIsland && _clipMask != null)
                _drawShader.SetTexture(_smudgeApplyKernel, "ClipMask", _clipMask);
            _drawShader.SetBuffer(_smudgeApplyKernel, "SmudgeBuffer", _smudgeBuffer);
            _drawShader.SetInt("SmudgeBrushDiameter", _smudgeBufferDiameter);
            _drawShader.SetFloat("SmudgeStrength", strength);
            _drawShader.SetInt("Width", maskTexture.width);
            _drawShader.SetInt("Height", maskTexture.height);

            var posArray = new int[2 * sizeof(int)];
            posArray[0 * sizeof(int)] = position.x;
            posArray[1 * sizeof(int)] = position.y;
            _drawShader.SetInts("Pos", posArray);

            int threadGroups = Mathf.CeilToInt(_smudgeBufferDiameter / 32f);
            _drawShader.Dispatch(_smudgeApplyKernel, threadGroups, threadGroups, 1);
        }

        /// <summary>
        /// スマッジバッファを解放（ストローク終了時）
        /// </summary>
        public void DisposeSmudgeBuffer()
        {
            _smudgeBuffer?.Dispose();
            _smudgeBuffer = null;
            _smudgeBufferDiameter = 0;
        }

        /// <summary>
        /// 指定されたピクセルリストを塗りつぶす（アイランド選択用）
        /// </summary>
        public void FillPixels(MaskCanvasModel model, System.Collections.Generic.List<Vector2Int> pixels)
        {
            if (model == null || pixels == null)
                return;

            float value = _selectionFillMode ? _brushValue : 1f;

            foreach (var pixel in pixels)
            {
                model.SetValue(pixel.x, pixel.y, value);
            }

            model.SyncCpuToGpu();
        }
    }
}
