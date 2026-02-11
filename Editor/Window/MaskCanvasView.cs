using UnityEngine;
using UnityEditor;

namespace NekoareMaskTool.Editor
{
    /// <summary>
    /// マスクキャンバスの描画と入力処理を担当するビュークラス
    /// MeshDeleterWithTextureのCanvasView方式を採用
    /// </summary>
    public class MaskCanvasView
    {
        private MaskCanvasModel _model;
        private Renderer _targetRenderer;
        private bool _hasGameObject; // GameObjectは設定されているがRendererがない状態の判別用
        private Mesh _sharedMesh;
        private MaskDrawController _drawController;

        // 描画用マテリアル（MeshDeleterWithTexture方式）
        private Material _drawMaterial;

        // PreviewTexture再構成用マテリアル（ComposeComposite用）
        // ズーム/スクロール/UVオーバーレイなしで bg × mask を計算する
        private Material _rebuildMat;

        // Scene上のプレビュー用マテリアル（MeshDeleterWithTexture方式）
        private Material _previewMaterial;
        private Material[] _originalMaterials;
        private int _selectedMaterialIndex = 0;

        // ズーム・スクロール（MeshDeleterWithTexture方式）
        private float _zoomScale = 1.0f;  // 0.1 ~ 1.0
        private Vector2 _scrollOffset = Vector2.zero;  // [-invZoomScale, invZoomScale]

        private const float MIN_ZOOM_SCALE = 0.1f;
        private const float MAX_ZOOM_SCALE = 1.0f;
        private const float ZOOM_STEP = 0.1f;

        private bool _isDrawing = false;
        private Vector2Int _lastDrawPosition;

        // 描画終了時のコールバック
        public System.Action OnDrawingComplete;

        // アイランドクリック時コールバック（選択ツール）
        public System.Action<Vector2Int> OnIslandClick;

        // スポイトピック時コールバック
        public System.Action<Vector2Int> OnEyedropperPick;

        // UVラインの色
        public Color UVLineColor { get; set; } = Color.white;

        // マスク表示色
        public MaskDisplayColor DisplayColor { get; set; } = MaskDisplayColor.Gray;

        public void SetModel(MaskCanvasModel model)
        {
            _model = model;
        }

        public void SetDrawController(MaskDrawController drawController)
        {
            _drawController = drawController;
        }

    // UVマップをテクスチャ化してオーバーレイ描画する（Handles毎フレーム描画を回避）
    private Texture2D _uvMapTexture;


        public void SetTargetRenderer(Renderer renderer, Mesh sharedMesh, bool hasGameObject = false)
        {
            _targetRenderer = renderer;
            _hasGameObject = hasGameObject;
            _sharedMesh = sharedMesh;

            // 描画用マテリアルを初期化（MeshDeleterWithTexture方式）
            if (_drawMaterial == null)
            {
                _drawMaterial = new Material(Shader.Find("Hidden/Nekoare/MaskToolCompositeEditor"));
                // 初期値を設定
                _drawMaterial.SetFloat("_TextureScale", _zoomScale);
                _drawMaterial.SetVector("_Offset", new Vector4(_scrollOffset.x, _scrollOffset.y, 0, 0));
                _drawMaterial.SetColor("_UVMapLineColor", UVLineColor);
                _drawMaterial.SetTexture("_UVMap", Texture2D.blackTexture); // 初期値
                // ガンマ補正を設定（Linear色空間の場合に適用）
                _drawMaterial.SetFloat("_ApplyGammaCorrection", PlayerSettings.colorSpace == ColorSpace.Linear ? 1 : 0);
            }

            // UVマップテクスチャを生成
            if (_sharedMesh != null && _model != null)
            {
                GenerateUVMap();
                // 背景が既にある（またはマスクがある）場合にのみ合成を更新する
                // これにより、背景が未設定のまま合成が走って白/黒表示されるのを防ぐ
                if (_model.BackgroundTexture != null || _model.MaskTexture != null)
                {
                    RefreshComposite();
                }
            }
            else
            {
                if (_uvMapTexture != null)
                {
                    Object.DestroyImmediate(_uvMapTexture);
                    _uvMapTexture = null;
                }
            }
        }

        /// <summary>
        /// UVワイヤーフレーム生成用のメッシュをオーバーライドする
        /// アトラス座標系のUVを持つメッシュを設定することで、正しいUVラインを表示する
        /// </summary>
        public void OverrideUVMesh(Mesh mesh)
        {
            if (mesh == null) return;
            _sharedMesh = mesh;
            _selectedMaterialIndex = 0;
            GenerateUVMap();
        }

        /// <summary>
        /// Scene上でのプレビューを有効化（MeshDeleterWithTexture方式）
        /// </summary>
        public void SetupScenePreview(int materialIndex)
        {
            if (_targetRenderer == null || _model == null || _model.PreviewTexture == null)
                return;

            _selectedMaterialIndex = materialIndex;

            // 元のマテリアルを保存
            if (_originalMaterials == null)
            {
                _originalMaterials = _targetRenderer.sharedMaterials;
            }

            var materials = _targetRenderer.sharedMaterials;
            if (materialIndex < 0 || materialIndex >= materials.Length)
                return;

            // プレビュー用マテリアルを作成（元のマテリアルからコピー）
            if (_previewMaterial != null)
            {
                Object.DestroyImmediate(_previewMaterial);
            }

            // 元のマテリアルからコピーし、既存の2ndカラー設定をリセット
            _previewMaterial = new Material(materials[materialIndex]);
            Reset2ndColorProperties(_previewMaterial);

            // 見え方確認モード: 元のmainTextureを保持し、2ndカラープロパティを設定
            // 通常モード: mainTextureをPreviewTextureに差し替え
            if (_drawController != null && _drawController.IsAppearancePreview)
            {
                _previewMaterial.name = "_MaskAppearancePreview";
                ApplyAppearancePreviewProperties();
            }
            else
            {
                _previewMaterial.name = "_MaskPreview";
                _previewMaterial.mainTexture = _model.PreviewTexture;
            }

            // マテリアルを差し替え
            materials[materialIndex] = _previewMaterial;
            _targetRenderer.sharedMaterials = materials;
        }

        /// <summary>
        /// Scene上のプレビューを更新
        /// </summary>
        public void UpdateScenePreview()
        {
            if (_previewMaterial == null || _model == null)
                return;

            if (_drawController != null && _drawController.IsAppearancePreview)
            {
                // 見え方確認モード: 2ndカラープロパティを再適用（MaskTextureは直接参照なので自動更新）
                ApplyAppearancePreviewProperties();
            }
            else
            {
                // 通常モード: mainTextureをPreviewTextureに設定
                if (_model.PreviewTexture != null)
                {
                    _previewMaterial.mainTexture = _model.PreviewTexture;
                }
            }
        }

        /// <summary>
        /// Scene上のプレビューを解除
        /// </summary>
        public void ClearScenePreview()
        {
            if (_targetRenderer != null && _originalMaterials != null)
            {
                _targetRenderer.sharedMaterials = _originalMaterials;
                _originalMaterials = null;
            }

            if (_previewMaterial != null)
            {
                Object.DestroyImmediate(_previewMaterial);
                _previewMaterial = null;
            }
        }

        public void Draw(Rect canvasRect)
        {
            // 背景を描画
            EditorGUI.DrawRect(canvasRect, new Color(0.2f, 0.2f, 0.2f));

            if (_model == null || _targetRenderer == null || _sharedMesh == null)
            {
                DrawPlaceholder(canvasRect);
                return;
            }

            // イベント処理
            HandleInput(canvasRect);

            // 背景テクスチャ+マスクを描画（クリッピング付き）
            if (_model.BackgroundTexture != null || _model.MaskTexture != null)
            {
                DrawCompositeTextureClipped(canvasRect);
            }

            // UVワイヤーフレームを描画
            DrawUVWireframe(canvasRect);

            // ブラシプレビュー円を描画
            DrawBrushPreview(canvasRect);

            // スポイトプレビュー円を描画
            DrawEyedropperPreview(canvasRect);

            // ズーム情報を表示
            DrawZoomInfo(canvasRect);
        }

        private void DrawPlaceholder(Rect canvasRect)
        {
            var labelStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.gray }
            };

            string message;
            if (_targetRenderer == null && _hasGameObject)
                message = "GameObjectにSkinnedMeshRendererを設定してください";
            else if (_targetRenderer == null)
                message = "Hierarchyからここにドロップして設定できます";
            else
                message = "メッシュが見つかりません";

            GUI.Label(canvasRect, message, labelStyle);
        }

        private void HandleInput(Rect canvasRect)
        {
            var e = Event.current;

            if (!canvasRect.Contains(e.mousePosition))
            {
                if (e.type == EventType.MouseUp && _isDrawing)
                {
                    _isDrawing = false;
                }
                return;
            }

            // ズーム処理（マウスホイール）
            if (e.type == EventType.ScrollWheel)
            {
                UpdateZoom(e.delta);
                e.Use();
            }

            // パン処理（右クリックまたは中クリックドラッグ）
            if ((e.type == EventType.MouseDrag && (e.button == 1 || e.button == 2)))
            {
                UpdateScroll(e.delta, canvasRect.size);
                e.Use();
            }

            // 描画処理（左クリックドラッグ）
            if (e.button == 0 && _drawController != null && _model != null)
            {
                // スポイトモード: 通常の描画処理より先にチェック
                if (_drawController.IsEyedropperMode)
                {
                    if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
                    {
                        _drawController.IsEyedropperMode = false;
                        e.Use();
                    }
                    else if (e.type == EventType.MouseDown)
                    {
                        var texCoord = WindowPosToTexturePos(e.mousePosition, canvasRect);
                        if (IsValidTexCoord(texCoord))
                        {
                            OnEyedropperPick?.Invoke(texCoord);
                            e.Use();
                        }
                    }
                    return;
                }

                if (_drawController.CurrentTool == DrawTool.Pen || _drawController.CurrentTool == DrawTool.Eraser)
                {
                    if (e.type == EventType.MouseDown)
                    {
                        var texCoord = WindowPosToTexturePos(e.mousePosition, canvasRect);
                        if (IsValidTexCoord(texCoord))
                        {
                            _isDrawing = true;
                            _lastDrawPosition = texCoord;
                            _drawController.ResetPreviousPosition();
                            // 消しゴムストローク開始時にスナップショットを作成（重複適用防止）
                            if (_drawController.CurrentTool == DrawTool.Eraser)
                                _drawController.BeginEraserStroke(_model.MaskTexture);
                            // MaskTextureのみ更新し、PreviewTextureはComposeCompositeで再構成
                            _drawController.DrawAt(_model.MaskTexture, texCoord);
                            ComposeComposite();
                            e.Use();
                        }
                    }
                    else if (e.type == EventType.MouseDrag && _isDrawing)
                    {
                        var texCoord = WindowPosToTexturePos(e.mousePosition, canvasRect);
                        if (IsValidTexCoord(texCoord))
                        {
                            if (texCoord != _lastDrawPosition)
                            {
                                _drawController.DrawAt(_model.MaskTexture, texCoord);
                                _lastDrawPosition = texCoord;
                                ComposeComposite();
                            }
                            e.Use();
                        }
                    }
                    else if (e.type == EventType.MouseUp)
                    {
                        _isDrawing = false;
                        _drawController.EndEraserStroke();
                        _model.SyncGpuToCpu();
                        ComposeComposite();
                        OnDrawingComplete?.Invoke();
                        e.Use();
                    }
                }
                else if (_drawController.CurrentTool == DrawTool.Smudge)
                {
                    // スマッジツール
                    if (e.type == EventType.MouseDown)
                    {
                        var texCoord = WindowPosToTexturePos(e.mousePosition, canvasRect);
                        if (IsValidTexCoord(texCoord))
                        {
                            _isDrawing = true;
                            _lastDrawPosition = texCoord;
                            _drawController.SmudgePickup(_model.MaskTexture, texCoord);
                            e.Use();
                        }
                    }
                    else if (e.type == EventType.MouseDrag && _isDrawing)
                    {
                        var texCoord = WindowPosToTexturePos(e.mousePosition, canvasRect);
                        if (IsValidTexCoord(texCoord))
                        {
                            if (texCoord != _lastDrawPosition)
                            {
                                _drawController.SmudgeApply(_model.MaskTexture, texCoord, _drawController.SmudgeStrength);
                                _lastDrawPosition = texCoord;
                                ComposeComposite();
                            }
                            e.Use();
                        }
                    }
                    else if (e.type == EventType.MouseUp)
                    {
                        _isDrawing = false;
                        _drawController.DisposeSmudgeBuffer();
                        _model.SyncGpuToCpu();
                        ComposeComposite();
                        OnDrawingComplete?.Invoke();
                        e.Use();
                    }
                }
                else if (_drawController.CurrentTool == DrawTool.Selection)
                {
                    // 選択ツール
                    if (e.type == EventType.MouseDown)
                    {
                        var texCoord = WindowPosToTexturePos(e.mousePosition, canvasRect);
                        if (IsValidTexCoord(texCoord))
                        {
                            OnIslandClick?.Invoke(texCoord);
                            e.Use();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// ズーム更新（MeshDeleterWithTexture方式）
        /// </summary>
        private void UpdateZoom(Vector2 delta)
        {
            _zoomScale = Mathf.Clamp(
                _zoomScale + Mathf.Sign(delta.y) * ZOOM_STEP,
                MIN_ZOOM_SCALE,
                MAX_ZOOM_SCALE
            );

            // 縮小時にオフセットを中心に戻す
            if (Mathf.Sign(delta.y) > 0)
            {
                if (_zoomScale < MAX_ZOOM_SCALE)
                    _scrollOffset *= _zoomScale;
                else
                    _scrollOffset = Vector2.zero;
            }

            // マテリアルのプロパティを更新
            if (_drawMaterial != null)
            {
                _drawMaterial.SetFloat("_TextureScale", _zoomScale);
                _drawMaterial.SetVector("_Offset", new Vector4(_scrollOffset.x, _scrollOffset.y, 0, 0));
            }
        }

        /// <summary>
        /// スクロールオフセット更新（MeshDeleterWithTexture方式）
        /// </summary>
        private void UpdateScroll(Vector2 delta, Vector2 rectSize)
        {
            var invZoomScale = 1 - _zoomScale;

            if (delta.x != 0)
            {
                _scrollOffset.x = Mathf.Clamp(
                    _scrollOffset.x - delta.x / rectSize.x,
                    -invZoomScale,
                    invZoomScale
                );
            }

            if (delta.y != 0)
            {
                _scrollOffset.y = Mathf.Clamp(
                    _scrollOffset.y + delta.y / rectSize.y,
                    -invZoomScale,
                    invZoomScale
                );
            }

            // マテリアルのプロパティを更新
            if (_drawMaterial != null)
            {
                _drawMaterial.SetVector("_Offset", new Vector4(_scrollOffset.x, _scrollOffset.y, 0, 0));
            }
        }

        /// <summary>
        /// ウィンドウ座標 → テクスチャ座標変換
        /// UVToScreenPointの正確な逆変換
        /// </summary>
        private Vector2Int WindowPosToTexturePos(Vector2 windowPos, Rect rect)
        {
            // スクリーン座標を正規化（0-1）
            float normalizedX = (windowPos.x - rect.x) / rect.width;
            float normalizedY = (windowPos.y - rect.y) / rect.height;

            // UVToScreenPointと同じパラメータを計算
            var invZoomScale = 1 - _zoomScale;
            var normalizedOffset = _zoomScale < 1 ? new Vector2(
                Mathf.InverseLerp(-invZoomScale, invZoomScale, _scrollOffset.x) * 2f - 1f,
                Mathf.InverseLerp(-invZoomScale, invZoomScale, _scrollOffset.y) * 2f - 1f
            ) : Vector2.zero;

            var minU = 0.5f - _zoomScale / 2f + normalizedOffset.x * (invZoomScale / 2f);
            var maxU = 0.5f + _zoomScale / 2f + normalizedOffset.x * (invZoomScale / 2f);
            var minV = 0.5f - _zoomScale / 2f - normalizedOffset.y * (invZoomScale / 2f);
            var maxV = 0.5f + _zoomScale / 2f - normalizedOffset.y * (invZoomScale / 2f);

            // UVToScreenPointの逆変換
            // 順方向: normalizedX = InverseLerp(minU, maxU, uv.x)
            //         normalizedY = InverseLerp(minV, maxV, 1 - uv.y)
            // 逆方向:
            float uvX = Mathf.Lerp(minU, maxU, normalizedX);
            float uvY = 1.0f - Mathf.Lerp(minV, maxV, normalizedY);

            int x = (int)(uvX * _model.Width);
            int y = (int)(uvY * _model.Height);

            return new Vector2Int(x, y);
        }

        private bool IsValidTexCoord(Vector2Int texCoord)
        {
            return texCoord.x >= 0 && texCoord.x < _model.Width &&
                   texCoord.y >= 0 && texCoord.y < _model.Height;
        }

        private Color GetMaskShaderColor()
        {
            switch (DisplayColor)
            {
                case MaskDisplayColor.Red:   return new Color(1f, 0f, 0f, 1f);
                case MaskDisplayColor.Green: return new Color(0f, 1f, 0f, 1f);
                case MaskDisplayColor.Blue:  return new Color(0f, 0f, 1f, 1f);
                default:                     return new Color(0f, 0f, 0f, 0f); // alpha=0でグレーモード
            }
        }

        /// <summary>
        /// キャンバスに BackgroundTexture × MaskTexture をシェーダで合成表示する
        /// tex2D()によるUVサンプリングなのでテクスチャサイズに依存せず正しく表示される
        /// PreviewTextureはSceneプレビュー専用であり、キャンバス表示には使わない
        /// </summary>
        private void DrawCompositeTextureClipped(Rect canvasRect)
        {
            if (_drawMaterial == null)
                return;

            // UVマップテクスチャを設定（ある場合はオーバーレイ、ない場合は黒テクスチャ）
            _drawMaterial.SetTexture("_UVMap", _uvMapTexture != null ? _uvMapTexture : Texture2D.blackTexture);
            _drawMaterial.SetColor("_UVMapLineColor", UVLineColor);
            _drawMaterial.SetColor("_MaskColor", GetMaskShaderColor());

            if (_model.BackgroundTexture != null)
            {
                // BackgroundTexture × MaskTexture をシェーダでリアルタイム合成
                _drawMaterial.SetTexture("_MainTex", _model.BackgroundTexture);
                _drawMaterial.SetTexture("_MaskTex", _model.MaskTexture);
                Graphics.DrawTexture(canvasRect, _model.BackgroundTexture, _drawMaterial);
            }
            else if (_model.MaskTexture != null)
            {
                _drawMaterial.SetTexture("_MainTex", _model.MaskTexture);
                _drawMaterial.SetTexture("_MaskTex", Texture2D.whiteTexture);
                Graphics.DrawTexture(canvasRect, _model.MaskTexture, _drawMaterial);
            }
        }

        /// <summary>
        /// UVワイヤーフレームを描画（クリッピング付き）
        /// </summary>
        private void DrawUVWireframe(Rect canvasRect)
        {
            if (_sharedMesh == null)
                return;

            // UVマップテクスチャがあれば、既にDrawCompositeTextureClippedでオーバーレイを描画しているためここでは何もしない
            if (_uvMapTexture != null)
            {
                return;
            }

            Handles.BeginGUI();
            Handles.color = UVLineColor;

            var uvs = _sharedMesh.uv;
            var triangles = _selectedMaterialIndex < _sharedMesh.subMeshCount
                ? _sharedMesh.GetTriangles(_selectedMaterialIndex)
                : _sharedMesh.triangles;

            if (uvs == null || uvs.Length == 0)
            {
                Handles.EndGUI();
                return;
            }

            // 三角形のエッジを描画（フォールバック）
            for (int i = 0; i < triangles.Length; i += 3)
            {
                var uv0 = uvs[triangles[i]];
                var uv1 = uvs[triangles[i + 1]];
                var uv2 = uvs[triangles[i + 2]];

                var p0 = UVToScreenPoint(uv0, canvasRect);
                var p1 = UVToScreenPoint(uv1, canvasRect);
                var p2 = UVToScreenPoint(uv2, canvasRect);

                // 3点全てがキャンバス外なら描画スキップ
                if (!canvasRect.Contains(p0) && !canvasRect.Contains(p1) && !canvasRect.Contains(p2))
                    continue;

                Handles.DrawLine(p0, p1);
                Handles.DrawLine(p1, p2);
                Handles.DrawLine(p2, p0);
            }

            Handles.EndGUI();
        }

        /// <summary>
        /// メッシュからUVマップテクスチャを生成してキャッシュする
        /// </summary>
        private void GenerateUVMap()
        {
            if (_sharedMesh == null || _model == null) return;

            var uvs = new System.Collections.Generic.List<Vector2>();
            _sharedMesh.GetUVs(0, uvs);
            if (uvs.Count == 0) return;

            var triangles = _selectedMaterialIndex < _sharedMesh.subMeshCount
                ? _sharedMesh.GetTriangles(_selectedMaterialIndex)
                : _sharedMesh.triangles;

            ComputeShader cs = null;
            var guids = AssetDatabase.FindAssets("NekoareMaskToolGetUVMap t:ComputeShader");
            if (guids.Length > 0)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            }
            if (cs == null) return;

            int kernel = cs.FindKernel("CSMain");

            var uvMapRT = new RenderTexture(_model.Width, _model.Height, 0, RenderTextureFormat.ARGB32)
            {
                enableRandomWrite = true
            };
            uvMapRT.Create();

            var triBuffer = new ComputeBuffer(triangles.Length, sizeof(int));
            var uvBuffer = new ComputeBuffer(uvs.Count, sizeof(float) * 2);
            triBuffer.SetData(triangles);
            uvBuffer.SetData(uvs.ToArray());

            cs.SetTexture(kernel, "UVMap", uvMapRT);
            cs.SetInt("Width", _model.Width);
            cs.SetInt("Height", _model.Height);
            cs.SetBuffer(kernel, "Triangles", triBuffer);
            cs.SetBuffer(kernel, "UVs", uvBuffer);

            cs.Dispatch(kernel, Mathf.Max(1, triangles.Length / 3), 1, 1);

            triBuffer.Release();
            uvBuffer.Release();

            var original = RenderTexture.active;
            RenderTexture.active = uvMapRT;
            var tex = new Texture2D(uvMapRT.width, uvMapRT.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, uvMapRT.width, uvMapRT.height), 0, 0);
            tex.Apply();
            RenderTexture.active = original;

            uvMapRT.Release();

            if (_uvMapTexture != null)
                Object.DestroyImmediate(_uvMapTexture);

            _uvMapTexture = tex;
        }

        /// <summary>
        /// PreviewTextureをGraphics.Blitで再構成する
        /// MaskCompositeEditorシェーダのtex2D()によるUVサンプリングを使うため、
        /// BackgroundTextureとPreviewTextureのサイズが異なっても正しくマッピングされる
        /// </summary>
        private void ComposeComposite()
        {
            if (_model == null) return;

            var previewRT = _model.PreviewTexture;
            if (previewRT == null) return;

            // 背景が無い場合は白で埋める
            if (_model.BackgroundTexture == null)
            {
                var prev = RenderTexture.active;
                RenderTexture.active = previewRT;
                GL.Clear(true, true, Color.white);
                RenderTexture.active = prev;
                UpdateScenePreview();
                return;
            }

            // 再構成用マテリアルを作成（ズーム/スクロール/UVオーバーレイなし）
            if (_rebuildMat == null)
            {
                _rebuildMat = new Material(Shader.Find("Hidden/Nekoare/MaskToolCompositeEditor"));
                _rebuildMat.SetFloat("_TextureScale", 1f);
                _rebuildMat.SetVector("_Offset", Vector4.zero);
                _rebuildMat.SetFloat("_ApplyGammaCorrection", 0);
                _rebuildMat.SetTexture("_UVMap", Texture2D.blackTexture);
            }

            // _MaskTex にマスクテクスチャを設定
            _rebuildMat.SetTexture("_MaskTex", _model.MaskTexture);
            _rebuildMat.SetColor("_MaskColor", GetMaskShaderColor());

            // Graphics.Blitで BackgroundTexture × MaskTexture を PreviewTexture に描画
            // tex2D()はUVベースでサンプリングするためサイズ差を自動的に処理する
            Graphics.Blit(_model.BackgroundTexture, previewRT, _rebuildMat);

            // Scene上のプレビューを更新
            UpdateScenePreview();
        }

        public void RefreshComposite()
        {
            GenerateUVMap();
            ComposeComposite();
        }

        /// <summary>
        /// UV座標（0-1）をスクリーン座標に変換
        /// </summary>
        private Vector2 UVToScreenPoint(Vector2 uv, Rect canvasRect)
        {
            var invZoomScale = 1 - _zoomScale;

            // オフセット値を[-1, 1]に正規化
            var normalizedOffset = _zoomScale < 1 ? new Vector2(
                Mathf.InverseLerp(-invZoomScale, invZoomScale, _scrollOffset.x) * 2f - 1f,
                Mathf.InverseLerp(-invZoomScale, invZoomScale, _scrollOffset.y) * 2f - 1f
            ) : Vector2.zero;

            // 表示されているUV範囲
            var minU = 0.5f - _zoomScale / 2f + normalizedOffset.x * (invZoomScale / 2f);
            var maxU = 0.5f + _zoomScale / 2f + normalizedOffset.x * (invZoomScale / 2f);
            var minV = 0.5f - _zoomScale / 2f - normalizedOffset.y * (invZoomScale / 2f);
            var maxV = 0.5f + _zoomScale / 2f - normalizedOffset.y * (invZoomScale / 2f);

            // UVを表示範囲内での相対位置に変換
            float normalizedX = Mathf.InverseLerp(minU, maxU, uv.x);
            float normalizedY = Mathf.InverseLerp(minV, maxV, 1.0f - uv.y);  // Y軸反転

            // スクリーン座標に変換
            float screenX = canvasRect.x + normalizedX * canvasRect.width;
            float screenY = canvasRect.y + normalizedY * canvasRect.height;

            return new Vector2(screenX, screenY);
        }

        /// <summary>
        /// ブラシサイズのプレビュー円を描画
        /// マウスがキャンバス上にあり、クリックしていない時のみ表示
        /// </summary>
        private void DrawBrushPreview(Rect canvasRect)
        {
            if (_drawController == null || _model == null)
                return;

            // スポイトモード中はブラシプレビューを非表示
            if (_drawController.IsEyedropperMode)
                return;

            // ペン・消しゴム・スマッジツール時のみ表示
            if (_drawController.CurrentTool != DrawTool.Pen && _drawController.CurrentTool != DrawTool.Eraser && _drawController.CurrentTool != DrawTool.Smudge)
                return;

            var e = Event.current;

            // マウスがキャンバス内にあり、クリックしていない時のみ表示
            if (!canvasRect.Contains(e.mousePosition) || _isDrawing)
                return;

            // ブラシサイズをテクスチャピクセルからスクリーンピクセルに変換
            // キャンバス幅 / テクスチャ幅 * ズームスケール で1テクセルあたりのスクリーンピクセル数を算出
            float texelToScreen = (canvasRect.width / _model.Width) / _zoomScale;
            int brushSize = _drawController.CurrentTool == DrawTool.Smudge
                ? _drawController.SmudgeBrushSize
                : _drawController.BrushSize;
            float radius = brushSize * texelToScreen;

            Handles.BeginGUI();
            Handles.color = Color.red;
            // Handles.DrawWireDiscは3D空間用なので、GUI座標でCircleを描く
            Vector3 center = new Vector3(e.mousePosition.x, e.mousePosition.y, 0);
            Handles.DrawWireDisc(center, Vector3.forward, radius);
            Handles.EndGUI();
        }

        /// <summary>
        /// スポイトモード中のプレビュー円を描画
        /// カーソル付近に、マウス位置のマスク値に対応するグレーの縁取り付き円を表示
        /// </summary>
        private void DrawEyedropperPreview(Rect canvasRect)
        {
            if (_drawController == null || _model == null)
                return;

            if (!_drawController.IsEyedropperMode)
                return;

            var e = Event.current;
            if (!canvasRect.Contains(e.mousePosition))
                return;

            // マウス位置からテクスチャ座標を取得
            var texCoord = WindowPosToTexturePos(e.mousePosition, canvasRect);
            if (!IsValidTexCoord(texCoord))
                return;

            // マスク値を取得（内部値: 0=黒, 1=白）
            float maskValue = _model.GetValue(texCoord.x, texCoord.y);

            // UIの「濃さ」に合わせた表示色（density = 1 - maskValue）
            float density = 1f - maskValue;
            Color previewColor = new Color(1f - density, 1f - density, 1f - density, 1f);

            // カーソルの右上にオフセットして表示
            float previewRadius = 10f;
            float borderWidth = 2f;
            float offsetX = 16f;
            float offsetY = -16f;
            Vector3 center = new Vector3(
                e.mousePosition.x + offsetX,
                e.mousePosition.y + offsetY,
                0);

            Handles.BeginGUI();
            // 縁取り（黒）
            Handles.color = Color.black;
            Handles.DrawSolidDisc(center, Vector3.forward, previewRadius + borderWidth);
            // 内側（マスク値に対応するグレー）
            Handles.color = previewColor;
            Handles.DrawSolidDisc(center, Vector3.forward, previewRadius);
            Handles.EndGUI();
        }

        private void DrawZoomInfo(Rect canvasRect)
        {
            var labelRect = new Rect(canvasRect.x + 5, canvasRect.yMax - 25, 150, 20);
            var labelStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = Color.white }
            };

            float zoomPercent = _zoomScale * 100f;
            GUI.Label(labelRect, $"Zoom: {zoomPercent:F0}%", labelStyle);
        }

        public void ResetView()
        {
            _zoomScale = 1.0f;
            _scrollOffset = Vector2.zero;

            // マテリアルのプロパティを更新
            if (_drawMaterial != null)
            {
                _drawMaterial.SetFloat("_TextureScale", _zoomScale);
                _drawMaterial.SetVector("_Offset", new Vector4(_scrollOffset.x, _scrollOffset.y, 0, 0));
            }
        }

        // --- 見え方確認モード ---

        private enum PreviewShaderType { Unknown, LilToon, Poiyomi }

        private struct ShaderPropertySet
        {
            public string enableProperty;
            public string colorProperty;
            public string maskProperty;
            public string textureProperty;
            public string keyword; // シェーダーキーワード（nullの場合はキーワード不要）
        }

        /// <summary>
        /// 対象マテリアルのシェーダーがlilToonかPoiyomiかを判定
        /// </summary>
        private PreviewShaderType DetectShaderType(Material material)
        {
            if (material == null || material.shader == null)
                return PreviewShaderType.Unknown;

            string shaderName = material.shader.name.ToLowerInvariant();
            if (shaderName.Contains("liltoon"))
                return PreviewShaderType.LilToon;
            if (shaderName.Contains("poiyomi") || shaderName.Contains(".poyi/"))
                return PreviewShaderType.Poiyomi;

            return PreviewShaderType.Unknown;
        }

        private ShaderPropertySet GetPropertySet(PreviewShaderType type)
        {
            switch (type)
            {
                case PreviewShaderType.LilToon:
                    return new ShaderPropertySet
                    {
                        enableProperty = "_UseMain2ndTex",
                        colorProperty = "_Color2nd",
                        maskProperty = "_Main2ndBlendMask",
                        textureProperty = "_Main2ndTex",
                        keyword = null
                    };
                case PreviewShaderType.Poiyomi:
                    return new ShaderPropertySet
                    {
                        enableProperty = "_DetailEnabled",
                        colorProperty = "_DetailTint",
                        maskProperty = "_DetailMask",
                        textureProperty = "_DetailTex",
                        keyword = "FINALPASS"
                    };
                default:
                    return default;
            }
        }

        /// <summary>
        /// 現在のマテリアルが見え方確認モードに対応しているか判定
        /// </summary>
        public bool IsAppearancePreviewSupported()
        {
            if (_targetRenderer == null)
                return false;

            var materials = _originalMaterials ?? _targetRenderer.sharedMaterials;
            if (materials == null || _selectedMaterialIndex < 0 || _selectedMaterialIndex >= materials.Length)
                return false;

            return DetectShaderType(materials[_selectedMaterialIndex]) != PreviewShaderType.Unknown;
        }

        /// <summary>
        /// マテリアルの2ndカラー関連プロパティを無効化する
        /// 元のマテリアルに既存の設定があった場合の競合を防ぐ
        /// </summary>
        private void Reset2ndColorProperties(Material material)
        {
            if (material == null)
                return;

            var shaderType = DetectShaderType(material);
            if (shaderType == PreviewShaderType.Unknown)
                return;

            var props = GetPropertySet(shaderType);

            material.SetFloat(props.enableProperty, 0f);
            if (!string.IsNullOrEmpty(props.keyword))
                material.DisableKeyword(props.keyword);
            material.SetTexture(props.maskProperty, Texture2D.whiteTexture);
            material.SetTexture(props.textureProperty, Texture2D.whiteTexture);
            material.SetColor(props.colorProperty, Color.white);
        }

        /// <summary>
        /// 見え方確認モードのプレビューマテリアルに2ndカラープロパティを設定する
        /// </summary>
        private void ApplyAppearancePreviewProperties()
        {
            if (_previewMaterial == null || _drawController == null || _model == null || _model.MaskTexture == null)
                return;

            // 元のマテリアルからシェーダータイプを検出
            var originalMaterials = _originalMaterials ?? _targetRenderer?.sharedMaterials;
            if (originalMaterials == null || _selectedMaterialIndex < 0 || _selectedMaterialIndex >= originalMaterials.Length)
                return;

            var shaderType = DetectShaderType(originalMaterials[_selectedMaterialIndex]);
            if (shaderType == PreviewShaderType.Unknown)
                return;

            var props = GetPropertySet(shaderType);

            // 2ndカラー機能を有効化
            _previewMaterial.SetFloat(props.enableProperty, 1f);
            if (!string.IsNullOrEmpty(props.keyword))
                _previewMaterial.EnableKeyword(props.keyword);
            // マスクを設定（MaskTextureをそのまま使用）
            _previewMaterial.SetTexture(props.maskProperty, _model.MaskTexture);
            // テクスチャは白（色がそのまま出る）
            _previewMaterial.SetTexture(props.textureProperty, Texture2D.whiteTexture);
            // 色を設定
            _previewMaterial.SetColor(props.colorProperty, _drawController.Preview2ndColor);
        }

        /// <summary>
        /// 見え方確認モードの色だけを更新する
        /// </summary>
        public void UpdateAppearancePreviewColor()
        {
            if (_previewMaterial == null || _drawController == null)
                return;

            var originalMaterials = _originalMaterials ?? _targetRenderer?.sharedMaterials;
            if (originalMaterials == null || _selectedMaterialIndex < 0 || _selectedMaterialIndex >= originalMaterials.Length)
                return;

            var shaderType = DetectShaderType(originalMaterials[_selectedMaterialIndex]);
            if (shaderType == PreviewShaderType.Unknown)
                return;

            var props = GetPropertySet(shaderType);
            _previewMaterial.SetColor(props.colorProperty, _drawController.Preview2ndColor);
        }
    }
}
