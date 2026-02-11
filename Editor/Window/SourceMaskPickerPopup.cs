using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace NekoareMaskTool.Editor
{
    /// <summary>
    /// 元マテリアルからマスクテクスチャを選択するツリーピッカー
    /// lilToonのInspector構造に準拠した表示
    /// </summary>
    public class SourceMaskPickerPopup : PopupWindowContent
    {
        private const float WINDOW_WIDTH = 350f;
        private const float MAX_WINDOW_HEIGHT = 500f;
        private const float INDENT_WIDTH = 16f;
        private const float ROW_HEIGHT = 20f;
        private const float THUMB_SIZE = 16f;
        private const float PADDING = 4f;

        private readonly MaskToolExternalContext _context;
        private readonly Action<Texture2D, int, int> _onSelect; // texture, rendererIndex, materialIndex
        private readonly HashSet<string> _foldoutState = new HashSet<string>();
        private Vector2 _scrollPos;
        private float _contentHeight;

        /// <summary>
        /// lilToonのマスクスロット定義
        /// </summary>
        private struct SlotDef
        {
            public string propertyName;
            public string displayName;
            public string category;
            /// <summary>trueの場合、マテリアルが半透明/カットアウトの時のみ表示</summary>
            public bool transparentOnly;
        }

        private static readonly SlotDef[] _slotDefs =
        {
            // メインカラー
            new SlotDef { category = "メインカラー", propertyName = "_Main2ndBlendMask", displayName = "2nd カラー マスク" },
            new SlotDef { category = "メインカラー", propertyName = "_Main3rdBlendMask", displayName = "3rd カラー マスク" },
            // 影
            new SlotDef { category = "影", propertyName = "_ShadowBorderMask", displayName = "影境界 マスク" },
            new SlotDef { category = "影", propertyName = "_ShadowBlurMask", displayName = "ぼかし量マスク" },
            // 発光
            new SlotDef { category = "発光", propertyName = "_EmissionBlendMask", displayName = "発光 マスク" },
            new SlotDef { category = "発光", propertyName = "_Emission2ndBlendMask", displayName = "発光2nd マスク" },
            // 光沢
            new SlotDef { category = "光沢", propertyName = "_SmoothnessTex", displayName = "滑らかさ" },
            new SlotDef { category = "光沢", propertyName = "_ReflectionColorTex", displayName = "反射 マスク" },
            // マットキャップ
            new SlotDef { category = "マットキャップ", propertyName = "_MatCapBlendMask", displayName = "マットキャップ マスク" },
            new SlotDef { category = "マットキャップ", propertyName = "_MatCap2ndBlendMask", displayName = "マットキャップ2nd マスク" },
            // 逆光ライト
            new SlotDef { category = "逆光ライト", propertyName = "_BacklightColorTex", displayName = "逆光ライト カラー" },
            // ラメ
            new SlotDef { category = "ラメ", propertyName = "_GlitterColorTex", displayName = "ラメ カラー" },
            // アルファマスク
            new SlotDef { category = "アルファマスク", propertyName = "_AlphaMask", displayName = "アルファマスク", transparentOnly = true },
            // アウトライン
            new SlotDef { category = "アウトライン", propertyName = "_OutlineWidthMask", displayName = "アウトライン幅 マスク" },
        };

        /// <summary>
        /// ツリーの表示データ（事前に構築）
        /// </summary>
        private struct TreeItem
        {
            public enum ItemType { Renderer, Category, Slot }
            public ItemType type;
            public string label;
            public string foldoutKey;
            public int indentLevel;
            // Slot用
            public Texture2D texture;
            public int rendererIndex;
            public int materialIndex;
        }

        private readonly List<TreeItem> _treeItems = new List<TreeItem>();

        public SourceMaskPickerPopup(MaskToolExternalContext context, Action<Texture2D, int, int> onSelect)
        {
            _context = context;
            _onSelect = onSelect;
            BuildTree();
        }

        private void BuildTree()
        {
            _treeItems.Clear();

            if (_context?.sourceRenderers == null)
                return;

            for (int ri = 0; ri < _context.sourceRenderers.Count; ri++)
            {
                var renderer = _context.sourceRenderers[ri];
                if (renderer == null)
                    continue;

                var materials = renderer.sharedMaterials;
                if (materials == null || materials.Length == 0)
                    continue;

                // このRendererに表示するスロットがあるかチェック
                bool hasAnySlot = false;
                var rendererItems = new List<TreeItem>();

                for (int mi = 0; mi < materials.Length; mi++)
                {
                    var mat = materials[mi];
                    if (mat == null)
                        continue;

                    // lilToonかどうかチェック
                    if (mat.shader == null || !mat.shader.name.ToLowerInvariant().Contains("liltoon"))
                        continue;

                    bool isTransparent = IsTransparentMaterial(mat);
                    string lastCategory = null;
                    bool categoryHasSlots = false;
                    var categoryItems = new List<TreeItem>();
                    string categoryFoldoutKey = null;

                    foreach (var slot in _slotDefs)
                    {
                        // 半透明限定スロットのチェック
                        if (slot.transparentOnly && !isTransparent)
                            continue;

                        if (!mat.HasProperty(slot.propertyName))
                            continue;

                        var tex = mat.GetTexture(slot.propertyName) as Texture2D;
                        if (tex == null)
                            continue;

                        // カテゴリヘッダー
                        if (slot.category != lastCategory)
                        {
                            // 前のカテゴリにスロットがなければ除去
                            if (!categoryHasSlots && categoryItems.Count > 0)
                            {
                                // カテゴリヘッダーを除去
                                categoryItems.RemoveAt(categoryItems.Count - 1);
                            }

                            lastCategory = slot.category;
                            categoryHasSlots = false;
                            categoryFoldoutKey = $"r{ri}_m{mi}_{slot.category}";

                            categoryItems.Add(new TreeItem
                            {
                                type = TreeItem.ItemType.Category,
                                label = slot.category,
                                foldoutKey = categoryFoldoutKey,
                                indentLevel = 1
                            });

                            // デフォルトで展開
                            _foldoutState.Add(categoryFoldoutKey);
                        }

                        categoryHasSlots = true;
                        hasAnySlot = true;

                        categoryItems.Add(new TreeItem
                        {
                            type = TreeItem.ItemType.Slot,
                            label = slot.displayName,
                            indentLevel = 2,
                            texture = tex,
                            rendererIndex = ri,
                            materialIndex = mi
                        });
                    }

                    // 最後のカテゴリにスロットがなければ除去
                    if (!categoryHasSlots && categoryItems.Count > 0)
                    {
                        categoryItems.RemoveAt(categoryItems.Count - 1);
                    }

                    rendererItems.AddRange(categoryItems);
                }

                if (!hasAnySlot)
                    continue;

                // Rendererヘッダー
                string rendererKey = $"r{ri}";
                _foldoutState.Add(rendererKey); // デフォルトで展開

                _treeItems.Add(new TreeItem
                {
                    type = TreeItem.ItemType.Renderer,
                    label = $"{renderer.name} ({renderer.GetType().Name})",
                    foldoutKey = rendererKey,
                    indentLevel = 0
                });

                _treeItems.AddRange(rendererItems);
            }

            // コンテンツの高さを計算
            _contentHeight = CalculateVisibleHeight();
        }

        private float CalculateVisibleHeight()
        {
            float h = PADDING * 2;
            bool[] visible = GetVisibility();
            for (int i = 0; i < _treeItems.Count; i++)
            {
                if (visible[i])
                    h += ROW_HEIGHT;
            }
            return h;
        }

        private bool[] GetVisibility()
        {
            var visible = new bool[_treeItems.Count];
            // 親のfoldout状態に基づいて可視性を決定
            bool rendererOpen = true;
            bool categoryOpen = true;
            string currentRendererKey = null;
            string currentCategoryKey = null;

            for (int i = 0; i < _treeItems.Count; i++)
            {
                var item = _treeItems[i];

                switch (item.type)
                {
                    case TreeItem.ItemType.Renderer:
                        visible[i] = true;
                        currentRendererKey = item.foldoutKey;
                        rendererOpen = _foldoutState.Contains(currentRendererKey);
                        break;

                    case TreeItem.ItemType.Category:
                        visible[i] = rendererOpen;
                        currentCategoryKey = item.foldoutKey;
                        categoryOpen = _foldoutState.Contains(currentCategoryKey);
                        break;

                    case TreeItem.ItemType.Slot:
                        visible[i] = rendererOpen && categoryOpen;
                        break;
                }
            }

            return visible;
        }

        public override Vector2 GetWindowSize()
        {
            float height = Mathf.Min(_contentHeight, MAX_WINDOW_HEIGHT);
            return new Vector2(WINDOW_WIDTH, Mathf.Max(height, ROW_HEIGHT * 2));
        }

        public override void OnGUI(Rect rect)
        {
            bool[] visible = GetVisibility();

            _scrollPos = GUI.BeginScrollView(
                new Rect(0, 0, rect.width, rect.height),
                _scrollPos,
                new Rect(0, 0, rect.width - 16f, _contentHeight)
            );

            float y = PADDING;

            for (int i = 0; i < _treeItems.Count; i++)
            {
                if (!visible[i])
                    continue;

                var item = _treeItems[i];
                float indent = item.indentLevel * INDENT_WIDTH + PADDING;
                var rowRect = new Rect(0, y, rect.width - 16f, ROW_HEIGHT);

                // ホバー
                if (item.type == TreeItem.ItemType.Slot && rowRect.Contains(Event.current.mousePosition))
                {
                    EditorGUI.DrawRect(rowRect, new Color(0.3f, 0.5f, 0.8f, 0.3f));
                }

                switch (item.type)
                {
                    case TreeItem.ItemType.Renderer:
                    case TreeItem.ItemType.Category:
                        DrawFoldoutRow(item, indent, y, rect.width - 16f);
                        break;

                    case TreeItem.ItemType.Slot:
                        DrawSlotRow(item, indent, y, rect.width - 16f);
                        break;
                }

                y += ROW_HEIGHT;
            }

            GUI.EndScrollView();

            // 空のツリーの場合
            if (_treeItems.Count == 0)
            {
                EditorGUI.LabelField(new Rect(PADDING, PADDING, rect.width - PADDING * 2, ROW_HEIGHT),
                    "テクスチャが設定されたスロットがありません");
            }
        }

        private void DrawFoldoutRow(TreeItem item, float indent, float y, float width)
        {
            bool isOpen = _foldoutState.Contains(item.foldoutKey);
            var foldoutRect = new Rect(indent, y, width - indent, ROW_HEIGHT);

            // 太字スタイル（Rendererのみ）
            var style = item.type == TreeItem.ItemType.Renderer
                ? EditorStyles.boldLabel
                : EditorStyles.label;

            // フォールドアウト三角 + ラベル
            string prefix = isOpen ? "\u25BC " : "\u25B6 ";
            GUI.Label(foldoutRect, prefix + item.label, style);

            // クリックでフォールドアウト切り替え
            if (Event.current.type == EventType.MouseDown && foldoutRect.Contains(Event.current.mousePosition))
            {
                if (isOpen)
                    _foldoutState.Remove(item.foldoutKey);
                else
                    _foldoutState.Add(item.foldoutKey);

                _contentHeight = CalculateVisibleHeight();
                Event.current.Use();
                editorWindow?.Repaint();
            }
        }

        private void DrawSlotRow(TreeItem item, float indent, float y, float width)
        {
            // サムネイル
            if (item.texture != null)
            {
                var thumbRect = new Rect(indent, y + (ROW_HEIGHT - THUMB_SIZE) * 0.5f, THUMB_SIZE, THUMB_SIZE);
                GUI.DrawTexture(thumbRect, item.texture, ScaleMode.ScaleToFit);
            }

            // スロット名
            float labelX = indent + THUMB_SIZE + PADDING;
            var labelRect = new Rect(labelX, y, width * 0.5f - labelX, ROW_HEIGHT);
            GUI.Label(labelRect, item.label);

            // テクスチャ名
            if (item.texture != null)
            {
                float nameX = width * 0.5f;
                var nameRect = new Rect(nameX, y, width - nameX - PADDING, ROW_HEIGHT);
                var nameStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleRight
                };
                GUI.Label(nameRect, item.texture.name, nameStyle);
            }

            // クリックでインポート
            var clickRect = new Rect(indent, y, width - indent, ROW_HEIGHT);
            if (Event.current.type == EventType.MouseDown && clickRect.Contains(Event.current.mousePosition))
            {
                _onSelect?.Invoke(item.texture, item.rendererIndex, item.materialIndex);
                editorWindow?.Close();
                Event.current.Use();
            }
        }

        /// <summary>
        /// マテリアルが半透明/カットアウトかどうかを判定
        /// </summary>
        private static bool IsTransparentMaterial(Material mat)
        {
            if (mat == null)
                return false;

            // lilToonの _TransparentMode で判定
            if (mat.HasProperty("_TransparentMode"))
            {
                float mode = mat.GetFloat("_TransparentMode");
                return mode > 0f; // 0=不透明, 1以上=半透明/カットアウト
            }

            // シェーダー名で判定（フォールバック）
            string shaderName = mat.shader.name.ToLowerInvariant();
            return shaderName.Contains("transparent") || shaderName.Contains("cutout");
        }
    }
}
