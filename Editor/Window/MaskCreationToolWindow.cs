using UnityEngine;
using UnityEditor;

namespace NekoareMaskTool.Editor
{
    public class MaskCreationToolWindow : EditorWindow
    {
        private const float MIN_WIDTH = 940f;
        private const float MIN_HEIGHT = 600f;
        private const float TOOLBAR_BUTTON_SIZE = 60f;
        private const float TOOLBAR_CONTAINER_SIZE = 65f;
        private const int DEFAULT_TEXTURE_SIZE = 1024;

        private GameObject _targetGameObject;
        private Renderer _targetRenderer;
        private Mesh _targetMesh;

        private MaskCanvasModel _canvasModel;
        private MaskCanvasView _canvasView;
        private MaskDrawController _drawController;
        private MaskUndoRedoManager _undoRedoManager;
        private IslandSelector _islandSelector;

        private int _expansionMargin = 0;
        private MaskDisplayColor _maskDisplayColor = MaskDisplayColor.Gray;

        // ユーザーが選択するマテリアルインデックス（MeshDeleter互換の機能）
        private int _selectedMaterialIndex = 0;

        // マスクインポート時の合成モード
        private MaskBlendMode _importBlendMode = MaskBlendMode.Default;

        // 外部コンテキスト（CHM連携等）
        private MaskToolExternalContext _externalContext;

        // 出力先スロット（CHM連携時）
        private string _outputSlotProperty;
        private string _outputSlotDisplayName;

        // 正方形ボタン用カスタムスタイル（EditorStyles.miniButtonはfixedHeightが固定されているため）
        private GUIStyle _squareButtonStyle;
        private GUIStyle _squareToggleStyle;
        private GUIStyle _sidebarButtonStyle;
        private GUIStyle _sidebarToggleStyle;
        private GUIStyle _dropAreaStyle;

        // サイドバーアイコン
        private GUIContent _penIcon;
        private GUIContent _eraserIcon;
        private GUIContent _smudgeIcon;
        private GUIContent _selectionIcon;
        private GUIContent _clearIcon;
        private GUIContent _importIcon;
        private GUIContent _chmImportIcon;
        private GUIContent _undoIcon;
        private GUIContent _redoIcon;
        private GUIContent _invertIcon;
        private GUIContent _outputIcon;
        private GUIContent _outputSlotIcon;

        [MenuItem("キメラヘアマスター/マスク作成支援ツール")]
        public static void ShowWindow()
        {
            var window = GetWindow<MaskCreationToolWindow>("マスク作成支援ツール");
            window.minSize = new Vector2(MIN_WIDTH, MIN_HEIGHT);
            window.Show();
        }

        /// <summary>
        /// 外部コンテキストを指定してマスクツールを開く（CHM連携等）
        /// </summary>
        public static void OpenWithContext(MaskToolExternalContext context)
        {
            var window = GetWindow<MaskCreationToolWindow>("マスク作成支援ツール");
            window.minSize = new Vector2(MIN_WIDTH, MIN_HEIGHT);
            window.ApplyExternalContext(context);
            window.Show();
        }

        private void OnEnable()
        {
            _canvasModel = new MaskCanvasModel();
            _canvasModel.Initialize(DEFAULT_TEXTURE_SIZE, DEFAULT_TEXTURE_SIZE);

            _drawController = new MaskDrawController();
            _drawController.Initialize();

            _undoRedoManager = new MaskUndoRedoManager();
            _undoRedoManager.Initialize(DEFAULT_TEXTURE_SIZE, DEFAULT_TEXTURE_SIZE);

            _canvasView = new MaskCanvasView();
            _canvasView.SetModel(_canvasModel);
            _canvasView.SetDrawController(_drawController);
            _canvasView.OnDrawingComplete = () => RecordUndoState();
            _canvasView.OnIslandClick = (texCoord) => OnIslandSelected(texCoord);
            _canvasView.OnEyedropperPick = (texCoord) => OnEyedropperPicked(texCoord);

            // 初期状態を記録
            RecordUndoState();
        }

        private void OnDisable()
        {
            // 外部ツールにマスクツール終了を通知
            _externalContext?.onMaskToolClosed?.Invoke();
            _externalContext = null;

            // Scene上のプレビューをクリア（MeshDeleterWithTexture方式）
            if (_canvasView != null)
            {
                _canvasView.ClearScenePreview();
            }

            if (_canvasModel != null)
            {
                _canvasModel.Dispose();
                _canvasModel = null;
            }
        }

        /// <summary>
        /// 毎フレーム再描画（MeshDeleterWithTexture方式）
        /// ComputeShaderの描画結果を即座にキャンバスとSceneに反映する
        /// </summary>
        private void Update()
        {
            Repaint();
            // Sceneビューも再描画してリアルタイムプレビューを実現
            // EditorWindow.Repaint()はこのウィンドウのみ。Sceneは別ウィンドウなので明示的に指示が必要
            SceneView.RepaintAll();
        }

        private void OnGUI()
        {
            // カスタムスタイルの遅延初期化（OnGUI内でないとEditorStylesが使えない）
            if (_squareButtonStyle == null)
            {
                _squareButtonStyle = new GUIStyle(EditorStyles.miniButton)
                {
                    fixedHeight = 0,
                    alignment = TextAnchor.MiddleCenter,
                    imagePosition = ImagePosition.ImageAbove
                };
            }
            if (_squareToggleStyle == null)
            {
                _squareToggleStyle = new GUIStyle(EditorStyles.miniButton)
                {
                    fixedHeight = 0,
                    alignment = TextAnchor.MiddleCenter,
                    imagePosition = ImagePosition.ImageAbove
                };
            }
            if (_sidebarButtonStyle == null)
            {
                _sidebarButtonStyle = new GUIStyle(_squareButtonStyle)
                {
                    fixedWidth = TOOLBAR_BUTTON_SIZE
                };
            }
            if (_sidebarToggleStyle == null)
            {
                _sidebarToggleStyle = new GUIStyle(_squareToggleStyle)
                {
                    fixedWidth = TOOLBAR_BUTTON_SIZE
                };
            }
            if (_penIcon == null)
            {
                _penIcon = MakeIconContent("Grid.PaintTool", "ペン");
                _eraserIcon = MakeIconContent("Grid.EraserTool", "消しゴム");
                _smudgeIcon = MakeIconContent("d_scenepicking_pickable", "ぼかし");
                _selectionIcon = MakeIconContent("Grid.BoxTool", "選択");
                _clearIcon = MakeIconContent("TreeEditor.Trash", "クリア");
                _importIcon = MakeIconContent("d_FolderOpened Icon", "インポート");
                _chmImportIcon = MakeIconContent("d_FolderOpened Icon", "元の髪から\nインポート");
                _undoIcon = MakeIconContent("ArrowNavigationLeft", "Undo");
                _redoIcon = MakeIconContent("ArrowNavigationRight", "Redo");
                _invertIcon = MakeIconContent("d_Exposure", "塗りを\n反転");
                _outputIcon = MakeIconContent("d_Folder Icon", "出力");
                _outputSlotIcon = MakeIconContent("d_Shaded", "");
            }
            if (_dropAreaStyle == null)
            {
                _dropAreaStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 9,
                    wordWrap = true
                };
            }

            DrawToolbar();

            EditorGUILayout.BeginHorizontal();
            {
                DrawLeftSidebar();

                EditorGUILayout.BeginVertical();
                {
                    DrawCanvasArea();
                    DrawBottomBar();
                }
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            {
                EditorGUILayout.LabelField("対象:", GUILayout.Width(40));

                var newGameObject = (GameObject)EditorGUILayout.ObjectField(
                    _targetGameObject,
                    typeof(GameObject),
                    true,
                    GUILayout.Width(200)
                );

                if (newGameObject != _targetGameObject)
                {
                    _targetGameObject = newGameObject;
                    OnTargetGameObjectChanged();
                }

                // 選択マテリアルの UI
                if (_externalContext != null)
                {
                    // 外部コンテキストモード: アトラス全体を編集中（マテリアル選択不要）
                    EditorGUILayout.LabelField("(アトラス)", GUILayout.Width(80));
                }
                else if (_targetRenderer != null && _targetRenderer.sharedMaterials != null && _targetRenderer.sharedMaterials.Length > 0)
                {
                    string[] names = new string[_targetRenderer.sharedMaterials.Length];
                    for (int i = 0; i < names.Length; i++)
                    {
                        var mat = _targetRenderer.sharedMaterials[i];
                        names[i] = mat != null ? $"[{i}] {mat.name}" : $"[{i}] (null)";
                    }

                    var newIndex = EditorGUILayout.Popup(_selectedMaterialIndex, names, GUILayout.Width(250));
                    if (newIndex != _selectedMaterialIndex)
                    {
                        // 作業状態をリセット（旧マテリアルの復元含む）
                        ResetWorkingState();
                        _selectedMaterialIndex = newIndex;
                        // 新しいマテリアルから背景を読込む
                        LoadBackgroundTexture(_selectedMaterialIndex);

                        // IslandSelectorを新しいサブメッシュインデックスで再初期化
                        if (_targetMesh != null)
                        {
                            _islandSelector?.Dispose();
                            _islandSelector = new IslandSelector();
                            _islandSelector.Initialize(_targetMesh, DEFAULT_TEXTURE_SIZE, DEFAULT_TEXTURE_SIZE, _selectedMaterialIndex);

                            // はみ出し防止用クリッピングマスクを再生成
                            var clipMask = _islandSelector.GetClippingMask();
                            _drawController?.SetClipMask(clipMask);
                        }

                        RecordUndoState();
                    }
                } else {
                    _selectedMaterialIndex = 0; // リセット
                }

                // UVラインの色
                if (_canvasView != null)
                {
                    EditorGUILayout.LabelField("UVの色", GUILayout.Width(45));
                    _canvasView.UVLineColor = EditorGUILayout.ColorField(
                        GUIContent.none, _canvasView.UVLineColor,
                        false, false, false,
                        GUILayout.Width(40)
                    );
                }

                GUILayout.FlexibleSpace();
            }
            EditorGUILayout.EndHorizontal();
        }

        private const int ICON_SIZE = 16;

        private static GUIContent MakeIconContent(string iconName, string text)
        {
            var icon = EditorGUIUtility.IconContent(iconName);
            if (icon != null && icon.image != null)
            {
                var image = icon.image;
                if (image.width > ICON_SIZE || image.height > ICON_SIZE)
                {
                    var scaled = ScaleIcon(image, ICON_SIZE);
                    return new GUIContent(text, scaled);
                }
                return new GUIContent(text, image);
            }
            return new GUIContent(text);
        }

        private static Texture2D ScaleIcon(Texture source, int size)
        {
            var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            var result = new Texture2D(size, size, TextureFormat.RGBA32, false);
            result.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            result.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            result.hideFlags = HideFlags.HideAndDontSave;
            return result;
        }

        private void DrawLeftSidebar()
        {
            var prevColor = GUI.color;
            GUI.color = new Color(prevColor.r, prevColor.g, prevColor.b, 0.7f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Width(TOOLBAR_CONTAINER_SIZE), GUILayout.MaxWidth(TOOLBAR_CONTAINER_SIZE));
            {
                if (_drawController != null)
                {
                    if (GUILayout.Toggle(_drawController.CurrentTool == DrawTool.Pen, _penIcon, _sidebarToggleStyle, GUILayout.Height(TOOLBAR_BUTTON_SIZE)))
                    {
                        _drawController.CurrentTool = DrawTool.Pen;
                    }
                    if (GUILayout.Toggle(_drawController.CurrentTool == DrawTool.Eraser, _eraserIcon, _sidebarToggleStyle, GUILayout.Height(TOOLBAR_BUTTON_SIZE)))
                    {
                        _drawController.CurrentTool = DrawTool.Eraser;
                    }
                    if (GUILayout.Toggle(_drawController.CurrentTool == DrawTool.Smudge, _smudgeIcon, _sidebarToggleStyle, GUILayout.Height(TOOLBAR_BUTTON_SIZE)))
                    {
                        _drawController.CurrentTool = DrawTool.Smudge;
                    }
                    if (GUILayout.Toggle(_drawController.CurrentTool == DrawTool.Selection, _selectionIcon, _sidebarToggleStyle, GUILayout.Height(TOOLBAR_BUTTON_SIZE)))
                    {
                        _drawController.CurrentTool = DrawTool.Selection;
                    }
                }

                GUILayout.Space(10);

                if (GUILayout.Button(_clearIcon, _sidebarButtonStyle, GUILayout.Height(TOOLBAR_BUTTON_SIZE)))
                {
                    if (_canvasModel != null)
                    {
                        _canvasModel.Clear();
                        _canvasView?.RefreshComposite();
                        RecordUndoState();
                    }
                }

                GUILayout.Space(10);

                // CHM連携時: 元マテリアルからインポート / 通常時: ファイルからインポート
                bool isChmMode = _externalContext != null && _externalContext.sourceRenderers != null &&
                    _externalContext.sourceRenderers.Count > 0;

                if (isChmMode)
                {
                    var chmImportStyle = new GUIStyle(_sidebarButtonStyle) { fontSize = 11 };
                    if (GUILayout.Button(_chmImportIcon, chmImportStyle, GUILayout.Height(TOOLBAR_BUTTON_SIZE)))
                    {
                        ShowSourceMaskPicker();
                    }
                }
                else
                {
                    if (GUILayout.Button(_importIcon, _sidebarButtonStyle, GUILayout.Height(TOOLBAR_BUTTON_SIZE)))
                    {
                        ImportMask();
                    }
                }

                // インポート設定ボタン（現在のモードを表示）
                var importSettingsStyle = new GUIStyle(EditorStyles.miniButton)
                {
                    fontSize = 9,
                    fixedHeight = 0,
                    alignment = TextAnchor.MiddleCenter,
                    margin = new RectOffset(0, 0, 0, 0)
                };
                string modeName = GetBlendModeName(_importBlendMode);
                var importSettingsRect = GUILayoutUtility.GetRect(TOOLBAR_BUTTON_SIZE, 25, GUILayout.Width(TOOLBAR_BUTTON_SIZE));
                if (GUI.Button(importSettingsRect, $"設定：{modeName}", importSettingsStyle))
                {
                    var popup = new ImportSettingsPopup(_importBlendMode, (mode) =>
                    {
                        _importBlendMode = mode;
                    });
                    // ボタンの右側にポップアップを表示
                    var popupRect = new Rect(importSettingsRect.xMax, importSettingsRect.y, 0, 0);
                    PopupWindow.Show(popupRect, popup);
                }

                // ドロップエリア（残りのスペースを全て使う）
                var dropRect = GUILayoutUtility.GetRect(TOOLBAR_BUTTON_SIZE, 0, GUILayout.Width(TOOLBAR_BUTTON_SIZE), GUILayout.ExpandHeight(true));
                GUI.Box(dropRect, "マスク画像を\nドロップして\nインポート", _dropAreaStyle);
                HandleMaskDrop(dropRect);
            }
            EditorGUILayout.EndVertical();
            GUI.color = prevColor;
        }

        private void DrawCanvasArea()
        {
            var availableRect = GUILayoutUtility.GetRect(
                0, 0,
                GUILayout.ExpandWidth(true),
                GUILayout.ExpandHeight(true)
            );

            // キャンバスを正方形に維持し、利用可能な領域の中央に配置
            float size = Mathf.Min(availableRect.width, availableRect.height);
            var canvasRect = new Rect(
                availableRect.x + (availableRect.width - size) / 2f,
                availableRect.y + (availableRect.height - size) / 2f,
                size,
                size
            );

            if (_canvasView != null)
            {
                _canvasView.Draw(canvasRect);
            }

            // キャンバスへのGameObjectドロップ受付
            HandleGameObjectDrop(canvasRect);
        }

        private void DrawBottomBar()
        {
            var prevColor = GUI.color;
            GUI.color = new Color(prevColor.r, prevColor.g, prevColor.b, 0.7f);
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox, GUILayout.Height(TOOLBAR_CONTAINER_SIZE));
            {
                if (_drawController != null)
                {
                    // スマッジツール時は強度スライダーを表示
                    if (_drawController.CurrentTool == DrawTool.Smudge)
                    {
                        EditorGUILayout.LabelField("強度", GUILayout.Width(28));
                        _drawController.SmudgeStrength = GUILayout.HorizontalSlider(_drawController.SmudgeStrength, 0f, 1f, GUILayout.Width(100));
                        _drawController.SmudgeStrength = EditorGUILayout.FloatField(_drawController.SmudgeStrength, GUILayout.Width(40));
                    }

                    // 消しゴムツール時は強さスライダーを表示
                    if (_drawController.CurrentTool == DrawTool.Eraser)
                    {
                        EditorGUILayout.LabelField("強さ", GUILayout.Width(28));
                        _drawController.EraserStrength = GUILayout.HorizontalSlider(_drawController.EraserStrength, 0f, 1f, GUILayout.Width(100));
                        _drawController.EraserStrength = EditorGUILayout.FloatField(_drawController.EraserStrength, GUILayout.Width(40));
                    }

                    // 濃さ（ペン・選択ツール共通）
                    if (_drawController.CurrentTool == DrawTool.Pen || _drawController.CurrentTool == DrawTool.Selection)
                    {
                        // サムネイル（バー内に収まる20x20）、^はバーの上に描画
                        var thumbAreaRect = GUILayoutUtility.GetRect(20, 20, GUILayout.Width(20));
                        var thumbRect = thumbAreaRect;

                        // ^マークをサムネイル中央の真上に描画
                        float caretHeight = 14f;
                        float caretWidth = 20f;
                        float caretX = thumbAreaRect.x + (thumbAreaRect.width - caretWidth) * 0.5f;
                        var caretRect = new Rect(caretX, thumbAreaRect.y - caretHeight + 3f, caretWidth, caretHeight);
                        var caretStyle = new GUIStyle(GUI.skin.label)
                        {
                            alignment = TextAnchor.MiddleCenter,
                            fontSize = 18,
                            padding = new RectOffset(0, 0, 0, 0),
                            margin = new RectOffset(0, 0, 0, 0)
                        };
                        GUI.Label(caretRect, "^", caretStyle);

                        // サムネイル描画
                        EditorGUI.DrawRect(thumbRect, Color.black);
                        var innerRect = new Rect(thumbRect.x + 1, thumbRect.y + 1, thumbRect.width - 2, thumbRect.height - 2);
                        // サムネイルは濃さ(0=白, 1=黒)で表示
                        float d = 1f - _drawController.BrushValue;

                        Color thumbColor;
                        switch (_maskDisplayColor)
                        {
                            case MaskDisplayColor.Red:
                                thumbColor = new Color(1f, 0f, 0f, d);
                                break;
                            case MaskDisplayColor.Green:
                                thumbColor = new Color(0f, 1f, 0f, d);
                                break;
                            case MaskDisplayColor.Blue:
                                thumbColor = new Color(0f, 0f, 1f, d);
                                break;
                            default:
                                thumbColor = new Color(1f - d, 1f - d, 1f - d, 1f);
                                break;
                        }

                        if (_maskDisplayColor != MaskDisplayColor.Gray)
                        {
                            EditorGUI.DrawRect(innerRect, Color.white);
                            EditorGUI.DrawRect(innerRect, thumbColor);
                        }
                        else
                        {
                            EditorGUI.DrawRect(innerRect, thumbColor);
                        }

                        // クリックで色パレットポップアップ（^の真上、中心揃え）
                        var clickArea = new Rect(thumbRect.x, caretRect.y, thumbRect.width, thumbRect.yMax - caretRect.y);
                        if (Event.current.type == EventType.MouseDown && clickArea.Contains(Event.current.mousePosition))
                        {
                            var popup = new ColorPalettePopup(_maskDisplayColor, (color) =>
                            {
                                _maskDisplayColor = color;
                                SyncDisplayColorToView();
                            });
                            var popupSize = popup.GetWindowSize();
                            // ポップアップの中心X = ^の中心X
                            float anchorX = caretRect.x + caretRect.width * 0.5f - popupSize.x * 0.5f;
                            var anchorAbove = new Rect(anchorX, caretRect.y - popupSize.y, popupSize.x, 0);
                            PopupWindow.Show(anchorAbove, popup);
                            Event.current.Use();
                        }

                        EditorGUILayout.LabelField("濃さ", GUILayout.Width(28));
                        // UIでは濃さ(0=白/なし, 1=黒/フル)で表示し、内部BrushValue(0=黒, 1=白)に変換
                        float density = 1f - _drawController.BrushValue;
                        density = GUILayout.HorizontalSlider(density, 0f, 1f, GUILayout.Width(100));
                        density = EditorGUILayout.FloatField(density, GUILayout.Width(40));
                        _drawController.BrushValue = 1f - Mathf.Clamp01(density);

                        // スポイトボタン
                        var eyedropperIcon = EditorGUIUtility.IconContent("eyeDropper.Large");
                        bool isEyedropper = _drawController.IsEyedropperMode;
                        bool newEyedropper = GUILayout.Toggle(isEyedropper, eyedropperIcon, EditorStyles.miniButton, GUILayout.Width(30), GUILayout.Height(30));
                        if (newEyedropper != isEyedropper)
                        {
                            _drawController.IsEyedropperMode = newEyedropper;
                        }
                    }

                    GUILayout.Space(8);

                    // ブラシサイズ（ペン・消しゴム）
                    if (_drawController.CurrentTool == DrawTool.Pen || _drawController.CurrentTool == DrawTool.Eraser)
                    {
                        EditorGUILayout.LabelField("サイズ", GUILayout.Width(35));
                        _drawController.BrushSize = (int)GUILayout.HorizontalSlider(_drawController.BrushSize, 1, 300, GUILayout.Width(100));
                        _drawController.BrushSize = EditorGUILayout.IntField(_drawController.BrushSize, GUILayout.Width(40));
                    }

                    // ブラシサイズ（スマッジ）
                    if (_drawController.CurrentTool == DrawTool.Smudge)
                    {
                        EditorGUILayout.LabelField("サイズ", GUILayout.Width(35));
                        _drawController.SmudgeBrushSize = (int)GUILayout.HorizontalSlider(_drawController.SmudgeBrushSize, 1, 300, GUILayout.Width(100));
                        _drawController.SmudgeBrushSize = EditorGUILayout.IntField(_drawController.SmudgeBrushSize, GUILayout.Width(40));
                    }

                    // 塗り広げ（選択ツール時のみ）
                    if (_drawController.CurrentTool == DrawTool.Selection)
                    {
                        EditorGUILayout.LabelField("塗り広げ", GUILayout.Width(52));
                        _expansionMargin = (int)GUILayout.HorizontalSlider(_expansionMargin, -5, 5, GUILayout.Width(100));
                        _expansionMargin = EditorGUILayout.IntField(_expansionMargin, GUILayout.Width(40));
                        _expansionMargin = Mathf.Clamp(_expansionMargin, -5, 5);
                    }
                }

                GUILayout.FlexibleSpace();

                // はみ出し防止トグル（ペン・消しゴム・ぼかしツール時）
                if (_drawController != null && (_drawController.CurrentTool == DrawTool.Pen || _drawController.CurrentTool == DrawTool.Eraser || _drawController.CurrentTool == DrawTool.Smudge))
                {
                    bool newClamp = GUILayout.Toggle(
                        _drawController.ClampToIsland,
                        "はみ出し\n防止",
                        _squareButtonStyle,
                        GUILayout.Width(TOOLBAR_BUTTON_SIZE),
                        GUILayout.Height(TOOLBAR_BUTTON_SIZE)
                    );
                    _drawController.ClampToIsland = newClamp;
                }

                // 選択ツール: 塗る/消すトグル
                if (_drawController != null && _drawController.CurrentTool == DrawTool.Selection)
                {
                    bool isFill = _drawController.SelectionFillMode;
                    // Toggle: 塗るモード時はON（青色）、消すモード時はOFF
                    if (GUILayout.Toggle(
                        isFill,
                        isFill ? "塗る" : "消す",
                        _squareToggleStyle,
                        GUILayout.Width(TOOLBAR_BUTTON_SIZE),
                        GUILayout.Height(TOOLBAR_BUTTON_SIZE)) != isFill)
                    {
                        _drawController.SelectionFillMode = !isFill;
                    }
                }

                // 右側: 正方形ボタン
                GUILayout.Space(10);
                EditorGUI.BeginDisabledGroup(_undoRedoManager == null || !_undoRedoManager.CanUndo);
                if (GUILayout.Button(_undoIcon, _squareButtonStyle, GUILayout.Width(TOOLBAR_BUTTON_SIZE), GUILayout.Height(TOOLBAR_BUTTON_SIZE)))
                {
                    PerformUndo();
                }
                EditorGUI.EndDisabledGroup();

                EditorGUI.BeginDisabledGroup(_undoRedoManager == null || !_undoRedoManager.CanRedo);
                if (GUILayout.Button(_redoIcon, _squareButtonStyle, GUILayout.Width(TOOLBAR_BUTTON_SIZE), GUILayout.Height(TOOLBAR_BUTTON_SIZE)))
                {
                    PerformRedo();
                }
                EditorGUI.EndDisabledGroup();

                GUILayout.Space(10);
                if (GUILayout.Button(_invertIcon, _squareButtonStyle, GUILayout.Width(TOOLBAR_BUTTON_SIZE), GUILayout.Height(TOOLBAR_BUTTON_SIZE)))
                {
                    if (_canvasModel != null)
                    {
                        _canvasModel.Invert();
                        _canvasView?.RefreshComposite();
                        RecordUndoState();
                    }
                }

                // CHM連携時: 出力先指定 / 通常時: 見え方確認
                GUILayout.Space(10);
                if (_externalContext != null && _externalContext.onMaskApplied != null)
                {
                    // 出力先指定ボタン
                    string slotLabel = string.IsNullOrEmpty(_outputSlotDisplayName)
                        ? "出力先\n指定"
                        : $"出力先:\n{_outputSlotDisplayName}";
                    var outputSlotContent = new GUIContent(slotLabel, _outputSlotIcon?.image);
                    if (GUILayout.Button(outputSlotContent, _squareButtonStyle, GUILayout.Width(TOOLBAR_BUTTON_SIZE), GUILayout.Height(TOOLBAR_BUTTON_SIZE)))
                    {
                        ShowOutputSlotPicker();
                    }
                }
                else
                {
                    // 見え方確認モード（通常時）
                    bool isExternalPreview = _externalContext != null && _externalContext.onMaskTextureAvailable != null;
                    bool isSupported = !isExternalPreview && _canvasView != null && _canvasView.IsAppearancePreviewSupported();
                    EditorGUI.BeginDisabledGroup(!isSupported);

                    bool isPreview = _drawController != null && _drawController.IsAppearancePreview;
                    bool newPreview = GUILayout.Toggle(
                        isPreview,
                        "見え方\n確認",
                        _squareButtonStyle,
                        GUILayout.Width(TOOLBAR_BUTTON_SIZE),
                        GUILayout.Height(TOOLBAR_BUTTON_SIZE)
                    );

                    if (newPreview != isPreview && _drawController != null)
                    {
                        _drawController.IsAppearancePreview = newPreview;
                        _canvasView?.ClearScenePreview();
                        _canvasView?.SetupScenePreview(_selectedMaterialIndex);
                    }

                    EditorGUI.EndDisabledGroup();

                    // カラーフィールド（見え方確認モードON時のみ表示）
                    if (isPreview && _drawController != null)
                    {
                        var newColor = EditorGUILayout.ColorField(
                            GUIContent.none,
                            _drawController.Preview2ndColor,
                            false, false, false,
                            GUILayout.Width(20),
                            GUILayout.Height(TOOLBAR_BUTTON_SIZE)
                        );
                        if (newColor != _drawController.Preview2ndColor)
                        {
                            _drawController.Preview2ndColor = newColor;
                            _canvasView?.UpdateAppearancePreviewColor();
                        }
                    }
                }

                if (GUILayout.Button(_outputIcon, _squareButtonStyle, GUILayout.Width(TOOLBAR_BUTTON_SIZE), GUILayout.Height(TOOLBAR_BUTTON_SIZE)))
                {
                    ExportMask();
                }
            }
            EditorGUILayout.EndHorizontal();
            GUI.color = prevColor;
        }

        private void HandleMaskDrop(Rect dropRect)
        {
            if (_canvasModel == null)
                return;

            var e = Event.current;
            if (!dropRect.Contains(e.mousePosition))
                return;

            if (e.type == EventType.DragUpdated)
            {
                // ドラッグ中のオブジェクトにTexture2Dが含まれるかチェック
                foreach (var obj in DragAndDrop.objectReferences)
                {
                    if (obj is Texture2D)
                    {
                        DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                        e.Use();
                        return;
                    }
                }
            }
            else if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (var obj in DragAndDrop.objectReferences)
                {
                    if (obj is Texture2D)
                    {
                        var path = AssetDatabase.GetAssetPath(obj);
                        if (!string.IsNullOrEmpty(path) && MaskImportExporter.ImportMask(_canvasModel, path, _importBlendMode))
                        {
                            RecordUndoState();
                            _canvasView?.RefreshComposite();
                            Repaint();
                        }
                        e.Use();
                        return;
                    }
                }
            }
        }

        private void HandleGameObjectDrop(Rect dropRect)
        {
            var e = Event.current;
            if (!dropRect.Contains(e.mousePosition))
                return;

            if (e.type == EventType.DragUpdated)
            {
                foreach (var obj in DragAndDrop.objectReferences)
                {
                    if (obj is GameObject go && (go.GetComponent<SkinnedMeshRenderer>() != null || go.GetComponent<MeshRenderer>() != null))
                    {
                        DragAndDrop.visualMode = DragAndDropVisualMode.Link;
                        e.Use();
                        return;
                    }
                }
            }
            else if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (var obj in DragAndDrop.objectReferences)
                {
                    if (obj is GameObject go && (go.GetComponent<SkinnedMeshRenderer>() != null || go.GetComponent<MeshRenderer>() != null))
                    {
                        _targetGameObject = go;
                        OnTargetGameObjectChanged();
                        Repaint();
                        e.Use();
                        return;
                    }
                }
            }
        }

        private void ImportMask()
        {
            if (_canvasModel == null)
                return;

            var path = MaskImportExporter.ShowImportDialog();

            if (!string.IsNullOrEmpty(path))
            {
                if (MaskImportExporter.ImportMask(_canvasModel, path, _importBlendMode))
                {
                    RecordUndoState();
                    // Compositeを更新してプレビューを即時反映
                    _canvasView?.RefreshComposite();
                    Repaint();
                }
            }
        }

        private void ExportMask()
        {
            if (_canvasModel == null)
                return;

            var defaultName = "mask_output";
            if (_canvasModel.BackgroundTexture != null && !string.IsNullOrEmpty(_canvasModel.BackgroundTexture.name))
            {
                defaultName = _canvasModel.BackgroundTexture.name + "_mask";
            }

            var path = MaskImportExporter.ShowExportDialog(defaultName);

            if (!string.IsNullOrEmpty(path))
            {
                MaskImportExporter.ExportMask(_canvasModel, path);

                // 出力先が指定されていればコールバックで適用
                if (!string.IsNullOrEmpty(_outputSlotProperty) && _externalContext?.onMaskApplied != null)
                {
                    _externalContext.onMaskApplied.Invoke(path, _outputSlotProperty);
                }
            }
        }

        private void RecordUndoState()
        {
            if (_undoRedoManager != null && _canvasModel != null)
            {
                var snapshot = _canvasModel.GetSnapshot();
                _undoRedoManager.RecordState(snapshot);
            }
        }

        private void PerformUndo()
        {
            if (_undoRedoManager == null || _canvasModel == null)
                return;

            var state = _undoRedoManager.Undo();
            if (state != null)
            {
                _canvasModel.RestoreSnapshot(state);
                _canvasView?.RefreshComposite();
                Repaint();
            }
        }

        private void PerformRedo()
        {
            if (_undoRedoManager == null || _canvasModel == null)
                return;

            var state = _undoRedoManager.Redo();
            if (state != null)
            {
                _canvasModel.RestoreSnapshot(state);
                _canvasView?.RefreshComposite();
                Repaint();
            }
        }

        /// <summary>
        /// 作業状態をリセット（Renderer/マテリアル切り替え時）
        /// Sceneプレビューのマテリアルを復元し、マスクとUndo履歴をクリアする
        /// </summary>
        private void ResetWorkingState()
        {
            _canvasView?.ClearScenePreview();
            _canvasModel?.Clear();
            _undoRedoManager?.Clear();
        }

        private void OnTargetGameObjectChanged()
        {
            // 既存の作業状態をリセット（旧Rendererのマテリアル復元含む）
            ResetWorkingState();

            if (_targetGameObject == null)
            {
                _targetRenderer = null;
                _targetMesh = null;
                if (_canvasView != null)
                {
                    _canvasView.SetTargetRenderer(null, null, false);
                }
                _islandSelector = null;
                return;
            }

            // SkinnedMeshRenderer を優先、なければ MeshRenderer + MeshFilter を使用
            var skinned = _targetGameObject.GetComponent<SkinnedMeshRenderer>();
            if (skinned != null)
            {
                _targetRenderer = skinned;
                _targetMesh = skinned.sharedMesh;
            }
            else
            {
                var meshRenderer = _targetGameObject.GetComponent<MeshRenderer>();
                var meshFilter = _targetGameObject.GetComponent<MeshFilter>();
                _targetRenderer = meshRenderer;
                _targetMesh = meshFilter != null ? meshFilter.sharedMesh : null;
            }

            // 背景テクスチャを先に読み込みしておく（Canvas.SetTargetRendererで合成が走るため）
            LoadBackgroundTexture();

            if (_canvasView != null)
            {
                _canvasView.SetTargetRenderer(_targetRenderer, _targetMesh, true);
            }

            // アイランドセレクターを初期化（選択中のサブメッシュインデックスを渡す）
            if (_targetRenderer != null && _targetMesh != null)
            {
                _islandSelector = new IslandSelector();
                _islandSelector.Initialize(_targetMesh, DEFAULT_TEXTURE_SIZE, DEFAULT_TEXTURE_SIZE, _selectedMaterialIndex);

                // はみ出し防止用クリッピングマスクを生成してDrawControllerにセット
                var clipMask = _islandSelector.GetClippingMask();
                _drawController?.SetClipMask(clipMask);
            }

            // 選択マテリアルを初期化（存在する場合）
            _selectedMaterialIndex = 0;
            if (_targetRenderer != null && _targetRenderer.sharedMaterials != null && _targetRenderer.sharedMaterials.Length > 0)
            {
                // 自動で選択マテリアルから読み込む
                LoadBackgroundTexture(_selectedMaterialIndex);
            } else {
                // 背景がない場合はクリア
                _canvasModel.SetBackgroundTexture(null);
                _canvasView?.RefreshComposite();
            }

            // Compositeを更新（UV再生成 + 合成）
            _canvasView?.RefreshComposite();

            // 初期状態を記録
            RecordUndoState();
        }

        /// <summary>
        /// 外部コンテキストを適用してマスクツールを初期化する
        /// </summary>
        private void ApplyExternalContext(MaskToolExternalContext context)
        {
            if (context == null)
                return;

            _externalContext = context;

            // デフォルト出力先の設定
            _outputSlotProperty = context.currentMaskSlotName;
            _outputSlotDisplayName = null; // スロット名からは表示名を逆引きできないのでnull（ボタンに「出力先指定」と表示）

            // 既存の作業状態をリセット
            ResetWorkingState();

            // ターゲットの設定
            if (context.targetGameObject != null)
            {
                _targetGameObject = context.targetGameObject;
            }
            else if (context.targetRenderer != null)
            {
                _targetGameObject = context.targetRenderer.gameObject;
            }

            if (context.targetRenderer != null)
            {
                _targetRenderer = context.targetRenderer;
                _targetMesh = context.targetRenderer.sharedMesh;
            }

            // アトラス解像度でキャンバスを再初期化
            int resolution = context.atlasResolution > 0 ? context.atlasResolution : DEFAULT_TEXTURE_SIZE;

            _canvasModel.Initialize(resolution, resolution);
            _undoRedoManager.Clear();
            _undoRedoManager.Initialize(resolution, resolution);

            // 背景テクスチャ: ソーステクスチャからアトラスを合成
            if (context.sourceMasks != null && context.sourceMasks.Count > 0)
            {
                var atlasBackground = MaskCoordinateTransformer.ComposeAtlasBackground(
                    context.sourceMasks, resolution);
                _canvasModel.SetBackgroundTexture(atlasBackground);
            }
            else
            {
                LoadBackgroundTexture();
            }

            // Rendererをキャンバスビューに設定（UVマップ生成・プレビュー含む）
            if (_canvasView != null)
            {
                _canvasView.SetTargetRenderer(_targetRenderer, _targetMesh, _targetGameObject != null);

                // アトラスUVメッシュが提供されている場合、UVワイヤーフレームをオーバーライド
                if (context.atlasMesh != null)
                {
                    _canvasView.OverrideUVMesh(context.atlasMesh);
                }
            }

            // アイランドセレクターを初期化（アトラスメッシュが提供されていればそれを使用）
            Mesh meshForIslands = context.atlasMesh;
            if (meshForIslands == null && _targetMesh != null)
                meshForIslands = _targetMesh;

            if (meshForIslands != null)
            {
                _islandSelector = new IslandSelector();
                _islandSelector.Initialize(meshForIslands, resolution, resolution);

                var clipMask = _islandSelector.GetClippingMask();
                _drawController?.SetClipMask(clipMask);
            }

            // 座標変換: 元マスクをアトラス座標系に変換
            if (context.sourceMasks != null && context.sourceMasks.Count > 0)
            {
                var transformedMask = MaskCoordinateTransformer.TransformToAtlas(
                    context.sourceMasks, resolution);

                // 変換結果をモデルに設定
                System.Array.Copy(transformedMask, _canvasModel.MaskValues, transformedMask.Length);
                _canvasModel.SyncCpuToGpu();
            }

            // シーンプレビュー: 外部ツールにマスクRenderTextureを通知
            if (context.onMaskTextureAvailable != null)
            {
                context.onMaskTextureAvailable.Invoke(_canvasModel.MaskTexture);
                // マスクツール標準のシーンプレビューは無効化（外部ツール側で管理するため）
                _canvasView?.ClearScenePreview();
            }

            // キャンバス表示を更新
            _canvasView?.RefreshComposite();

            // 初期状態を記録
            RecordUndoState();

            Repaint();
        }

        private void LoadBackgroundTexture(int materialIndexHint = -1)
        {
            if (_targetRenderer == null || _canvasModel == null)
            {
                return;
            }

            var materials = _targetRenderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
            {
                _canvasModel.SetBackgroundTexture(null);
                _canvasView?.RefreshComposite();
                return;
            }

            Texture found = null;
            string foundDesc = null;

            // 探索する一般的なテクスチャプロパティ名
            string[] propCandidates = new string[] { "_MainTex", "_BaseMap", "_BaseColorMap" };

            // materialIndexHint が有効であればまずそれを試す
            if (materialIndexHint >= 0 && materialIndexHint < materials.Length)
            {
                var mat = materials[materialIndexHint];
                if (mat != null)
                {
                    if (mat.mainTexture != null)
                    {
                        found = mat.mainTexture;
                        foundDesc = $"materials[{materialIndexHint}].mainTexture";
                    }
                    else
                    {
                        foreach (var prop in propCandidates)
                        {
                            if (mat.HasProperty(prop))
                            {
                                var tex = mat.GetTexture(prop);
                                if (tex != null)
                                {
                                    found = tex;
                                    foundDesc = $"materials[{materialIndexHint}].{prop}";
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // 見つからない場合は従来の全探索
            if (found == null)
            {
                for (int i = 0; i < materials.Length; i++)
                {
                    var mat = materials[i];
                    if (mat == null) continue;

                    if (mat.mainTexture != null)
                    {
                        found = mat.mainTexture;
                        foundDesc = $"materials[{i}].mainTexture";
                        break;
                    }

                    foreach (var prop in propCandidates)
                    {
                        if (mat.HasProperty(prop))
                        {
                            var tex = mat.GetTexture(prop);
                            if (tex != null)
                            {
                                found = tex;
                                foundDesc = $"materials[{i}].{prop}";
                                break;
                            }
                        }
                    }

                    if (found != null) break;
                }
            }

            if (found == null)
            {
                _canvasModel.SetBackgroundTexture(null);
                _canvasView?.RefreshComposite();
                return;
            }

            // 読み取り可能な Texture2D に変換
            var readableTexture = MakeTextureReadable(found);
            if (readableTexture == null)
            {
                _canvasModel.SetBackgroundTexture(null);
                _canvasView?.RefreshComposite();
                return;
            }

            // BackgroundTextureをMaskTexture/PreviewTextureと同じサイズにリサイズ
            if (readableTexture.width != _canvasModel.Width || readableTexture.height != _canvasModel.Height)
            {
                var resized = ResizeTexture(readableTexture, _canvasModel.Width, _canvasModel.Height);
                Object.DestroyImmediate(readableTexture);
                readableTexture = resized;
            }

            _canvasModel.SetBackgroundTexture(readableTexture);

            // PreviewTextureを背景テクスチャで初期化（MeshDeleterWithTexture方式）
            _canvasModel.InitializePreviewFromBackground();

            // Scene上のプレビューを設定（_selectedMaterialIndexを先に更新する）
            // RefreshCompositeのGenerateUVMapが正しいサブメッシュのUVラインを生成するために必要
            if (materialIndexHint >= 0)
            {
                _canvasView?.SetupScenePreview(materialIndexHint);
            }

            // PreviewTextureを再合成 + UVマップ再生成
            _canvasView?.RefreshComposite();

            Repaint();
        }

        private Texture2D MakeTextureReadable(Texture source)
        {
            if (source == null) return null;

            if (source is Texture2D t2d)
            {
                Texture2D readable = new Texture2D(t2d.width, t2d.height, t2d.format, false);
                Graphics.CopyTexture(t2d, 0, 0, readable, 0, 0);
                readable.name = t2d.name;
                readable.Apply();
                return readable;
            }
            else if (source is RenderTexture rt)
            {
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                Texture2D readable = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                readable.Apply();
                RenderTexture.active = prev;
                return readable;
            }
            else
            {
                int width = source.width > 0 ? source.width : 1024;
                int height = source.height > 0 ? source.height : 1024;

                var renderTex = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                Graphics.Blit(source, renderTex);

                var prev = RenderTexture.active;
                RenderTexture.active = renderTex;
                var readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, renderTex.width, renderTex.height), 0, 0);
                readable.Apply();
                RenderTexture.active = prev;

                RenderTexture.ReleaseTemporary(renderTex);
                return readable;
            }
        }

        /// <summary>
        /// テクスチャを指定サイズにリサイズする
        /// Graphics.Blitでバイリニア補間されるため品質を保ったままリサイズできる
        /// </summary>
        private Texture2D ResizeTexture(Texture2D source, int targetWidth, int targetHeight)
        {
            var rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(source, rt);

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var resized = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false);
            resized.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
            resized.name = source.name;
            resized.Apply();
            RenderTexture.active = prev;

            RenderTexture.ReleaseTemporary(rt);
            return resized;
        }

        private void SyncDisplayColorToView()
        {
            if (_canvasView != null)
            {
                _canvasView.DisplayColor = _maskDisplayColor;
                _canvasView.RefreshComposite();
            }
            Repaint();
        }

        public void OnIslandSelected(Vector2Int texCoord)
        {
            if (_islandSelector == null || _canvasModel == null || _drawController == null)
                return;

            var pixels = _islandSelector.GetIslandPixels(texCoord, _expansionMargin);

            if (pixels.Count > 0)
            {
                _drawController.FillPixels(_canvasModel, pixels);
                _canvasView?.RefreshComposite();
                RecordUndoState();
                Repaint();
            }
        }

        private void OnEyedropperPicked(Vector2Int texCoord)
        {
            if (_canvasModel == null || _drawController == null)
                return;

            // マスク値を取得（内部値: 0=黒, 1=白）→ BrushValueに直接設定（同じ体系）
            _drawController.BrushValue = _canvasModel.GetValue(texCoord.x, texCoord.y);
            _drawController.IsEyedropperMode = false;
            Repaint();
        }

        private void ShowOutputSlotPicker()
        {
            var popup = new OutputSlotPickerPopup(_outputSlotProperty, (propertyName, displayName) =>
            {
                _outputSlotProperty = propertyName;
                _outputSlotDisplayName = displayName;
                // プレビュースロットの切り替えを通知
                _externalContext?.onOutputSlotChanged?.Invoke(propertyName);
                Repaint();
            });
            // ボタン付近にポップアップを表示
            var popupRect = new Rect(position.width - TOOLBAR_CONTAINER_SIZE * 3, position.height - TOOLBAR_CONTAINER_SIZE, 0, 0);
            PopupWindow.Show(popupRect, popup);
        }

        private void ShowSourceMaskPicker()
        {
            var popup = new SourceMaskPickerPopup(_externalContext, OnSourceMaskSelected);
            // 左サイドバーの右側に表示
            var popupRect = new Rect(TOOLBAR_CONTAINER_SIZE + 4, 100, 0, 0);
            PopupWindow.Show(popupRect, popup);
        }

        private void OnSourceMaskSelected(Texture2D texture, int rendererIndex, int materialIndex)
        {
            if (_canvasModel == null || _externalContext == null || texture == null)
                return;

            // sourceRendererIndex と sourceMaterialIndex が一致する SourceMaskEntry を抽出
            var matchingEntries = new System.Collections.Generic.List<SourceMaskEntry>();
            if (_externalContext.sourceMasks != null)
            {
                foreach (var entry in _externalContext.sourceMasks)
                {
                    if (entry.sourceRendererIndex == rendererIndex && entry.sourceMaterialIndex == materialIndex)
                    {
                        matchingEntries.Add(entry);
                    }
                }
            }

            if (matchingEntries.Count == 0)
            {
                Debug.LogWarning("SourceMaskPickerPopup: マッチするSourceMaskEntryが見つかりませんでした");
                return;
            }

            int resolution = _canvasModel.Width;

            // 各マッチしたエントリについて座標変換してキャンバスに合成
            foreach (var entry in matchingEntries)
            {
                // テクスチャを一時的に差し替えてBlitする
                var originalMask = entry.maskTexture;
                entry.maskTexture = texture;

                MaskCoordinateTransformer.BlitMaskToAtlas(
                    _canvasModel.MaskValues, entry, resolution, _importBlendMode);

                // 元に戻す
                entry.maskTexture = originalMask;
            }

            _canvasModel.SyncCpuToGpu();
            _canvasView?.RefreshComposite();
            RecordUndoState();
            Repaint();
        }

        private static string GetBlendModeName(MaskBlendMode mode)
        {
            switch (mode)
            {
                case MaskBlendMode.Default: return "標準";
                case MaskBlendMode.Min: return "最小";
                case MaskBlendMode.Max: return "最大";
                case MaskBlendMode.Multiply: return "乗算";
                default: return "標準";
            }
        }
    }

    /// <summary>
    /// 色パレットポップアップ（サムネイル縦並び）
    /// </summary>
    public class ColorPalettePopup : PopupWindowContent
    {
        private const float THUMB_SIZE = 24f;
        private const float PADDING = 4f;
        private readonly MaskDisplayColor _current;
        private readonly System.Action<MaskDisplayColor> _onSelect;

        private static readonly (MaskDisplayColor color, Color displayColor)[] _entries =
        {
            (MaskDisplayColor.Gray, Color.black),
            (MaskDisplayColor.Red, Color.red),
            (MaskDisplayColor.Green, Color.green),
            (MaskDisplayColor.Blue, Color.blue),
        };

        public ColorPalettePopup(MaskDisplayColor current, System.Action<MaskDisplayColor> onSelect)
        {
            _current = current;
            _onSelect = onSelect;
        }

        public override Vector2 GetWindowSize()
        {
            float width = THUMB_SIZE + PADDING * 2;
            float height = _entries.Length * (THUMB_SIZE + PADDING) + PADDING;
            return new Vector2(width, height);
        }

        public override void OnGUI(Rect rect)
        {
            float y = PADDING;
            for (int i = 0; i < _entries.Length; i++)
            {
                var entry = _entries[i];
                var thumbRect = new Rect(PADDING, y, THUMB_SIZE, THUMB_SIZE);

                // 選択中は枠を白に
                EditorGUI.DrawRect(thumbRect, entry.color == _current ? Color.white : Color.gray);
                var innerRect = new Rect(thumbRect.x + 2, thumbRect.y + 2, thumbRect.width - 4, thumbRect.height - 4);
                EditorGUI.DrawRect(innerRect, entry.displayColor);

                if (Event.current.type == EventType.MouseDown && thumbRect.Contains(Event.current.mousePosition))
                {
                    _onSelect?.Invoke(entry.color);
                    editorWindow.Close();
                    Event.current.Use();
                }

                y += THUMB_SIZE + PADDING;
            }
        }
    }

    /// <summary>
    /// インポート設定ポップアップ（合成モード選択）
    /// </summary>
    public class ImportSettingsPopup : PopupWindowContent
    {
        private const float ITEM_HEIGHT = 22f;
        private const float PADDING = 4f;
        private const float WIDTH = 100f;
        private readonly MaskBlendMode _current;
        private readonly System.Action<MaskBlendMode> _onSelect;

        private static readonly (MaskBlendMode mode, string label, string tooltip)[] _entries =
        {
            (MaskBlendMode.Default, "デフォルト", "画像の黒い範囲で塗りを上書き"),
            (MaskBlendMode.Min, "最小値", "重複した場合、塗りの薄い方を採用"),
            (MaskBlendMode.Max, "最大値", "重複した場合、塗りの濃い方を採用"),
            (MaskBlendMode.Multiply, "乗算", "重複するとより暗くなる"),
        };

        public ImportSettingsPopup(MaskBlendMode current, System.Action<MaskBlendMode> onSelect)
        {
            _current = current;
            _onSelect = onSelect;
        }

        public override Vector2 GetWindowSize()
        {
            float height = _entries.Length * ITEM_HEIGHT + PADDING * 2;
            return new Vector2(WIDTH, height);
        }

        public override void OnGUI(Rect rect)
        {
            float y = PADDING;
            for (int i = 0; i < _entries.Length; i++)
            {
                var entry = _entries[i];
                var itemRect = new Rect(PADDING, y, WIDTH - PADDING * 2, ITEM_HEIGHT - 2);

                // 選択中はハイライト
                bool isSelected = entry.mode == _current;
                if (isSelected)
                {
                    EditorGUI.DrawRect(itemRect, new Color(0.3f, 0.5f, 0.8f, 0.5f));
                }

                // ラベル描画
                var labelStyle = new GUIStyle(EditorStyles.label)
                {
                    alignment = TextAnchor.MiddleLeft,
                    fontStyle = isSelected ? FontStyle.Bold : FontStyle.Normal
                };
                GUI.Label(itemRect, new GUIContent(entry.label, entry.tooltip), labelStyle);

                if (Event.current.type == EventType.MouseDown && itemRect.Contains(Event.current.mousePosition))
                {
                    _onSelect?.Invoke(entry.mode);
                    editorWindow.Close();
                    Event.current.Use();
                }

                y += ITEM_HEIGHT;
            }
        }
    }
}
